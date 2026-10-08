using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Core.Paths;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Drives the real Windows install pipeline end to end through the actual
/// composition root: HTTP download → SHA-256 → package handling → on-disk
/// result → verification by detection (spec 19/73).
///
/// The payload is served locally so the run never modifies the user's machine,
/// but every step in between is the production implementation.
/// </summary>
internal static class InstallCheck
{
    public static async Task<int> RunAsync(AppHost host)
    {
        var failures = 0;
        var work = Path.Combine(Path.GetTempPath(), "erireborn-install", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var installRoot = Path.Combine(work, "root");
            var context = new EnvironmentContext { RootPath = installRoot };

            var payload = BuildZip(("demo.txt", "erireborn payload"), ("sub/inner.txt", "nested"));
            var sha = Sha(payload);

            using var server = new LocalHttpServer();
            server.Add("/demo.zip", payload);

            // ---------- 1. before: not installed ----------
            var software = Portable(server.Url("/demo.zip"), sha);
            var before = await host.SoftwareEngine.DetectAsync(software, context);
            failures += Report("before install: not detected", before.Outcome != DetectionOutcome.Detected);

            // ---------- 2. real install ----------
            var result = await host.SoftwareEngine.InstallAsync(software, context);

            failures += Report("install succeeded", result.State == InstallState.Succeeded);
            Console.WriteLine($"      state={result.State} {result.Message}");

            var installedDirectory = Path.Combine(installRoot, "Utility", "Demo_Portable");
            failures += Report("payload landed on disk", File.Exists(Path.Combine(installedDirectory, "demo.txt")));
            failures += Report("nested payload landed on disk", File.Exists(Path.Combine(installedDirectory, "sub", "inner.txt")));
            failures += Report(
                "content is intact",
                File.Exists(Path.Combine(installedDirectory, "demo.txt"))
                && await File.ReadAllTextAsync(Path.Combine(installedDirectory, "demo.txt")) == "erireborn payload");

            // ---------- 3. verification by detection ----------
            failures += Report("verified by detection", result.Verified?.Outcome == DetectionOutcome.Detected);

            var after = await host.SoftwareEngine.DetectAsync(software, context);
            failures += Report("after install: detected", after.Outcome == DetectionOutcome.Detected);
            Console.WriteLine($"      after: {after.Outcome} source={after.Source} path={after.Detail}");

            // ---------- 4. recorded for later scans ----------
            var record = host.Installations.Find(software.Id);
            failures += Report("install recorded", record is not null);
            failures += Report("record points at the real directory", record?.Directory == installedDirectory);

            // ---------- 5. a tampered download must not install ----------
            var tampered = Portable(server.Url("/demo.zip"), Sha(Encoding.UTF8.GetBytes("wrong")));
            var badRoot = Path.Combine(work, "bad");
            var tampered2 = tampered with { DirectoryName = "Demo_Tampered", Id = "demo_tampered" };
            var tamperedResult = await host.SoftwareEngine.InstallAsync(
                tampered2,
                new EnvironmentContext { RootPath = badRoot });

            failures += Report("tampered hash is rejected", tamperedResult.State == InstallState.IntegrityFailed);
            Console.WriteLine($"      tampered: {tamperedResult.State} {tamperedResult.Message}");
            failures += Report(
                "nothing written for a tampered package",
                !Directory.Exists(Path.Combine(badRoot, "Utility", "Demo_Tampered")));

            // ---------- 6. a downloaded script itself is refused ----------
            // Note: the package extension is what matters, so the payload must be
            // the .ps1 itself rather than an archive containing one.
            var scriptBytes = Encoding.UTF8.GetBytes("Write-Host 'should-not-run'");
            server.Add("/setup.ps1", scriptBytes);
            var scriptSoftware = Portable(server.Url("/setup.ps1"), Sha(scriptBytes)) with
            {
                Id = "demo_script",
                DirectoryName = "Demo_Script",
                Mode = InstallationMode.Script,
                Sources = new[]
                {
                    new SoftwareSource
                    {
                        Kind = SourceKind.HttpUrl,
                        Url = server.Url("/setup.ps1"),
                        Sha256 = Sha(scriptBytes),
                        FileName = "setup.ps1",
                    },
                },
            };

            var scriptResult = await host.SoftwareEngine.InstallAsync(
                scriptSoftware,
                new EnvironmentContext { RootPath = Path.Combine(work, "scripts") });

            failures += Report("script install refused", scriptResult.State == InstallState.Unsupported);
            Console.WriteLine($"      script: {scriptResult.State} {scriptResult.Message}");

            // ---------- 7. an unvetted entry is blocked by the trust gate ----------
            var untrusted = Portable(server.Url("/demo.zip"), sha) with
            {
                Id = "demo_untrusted",
                DirectoryName = "Demo_Untrusted",
                Trust = SoftwareTrust.Unknown,
            };

            var untrustedResult = await host.SoftwareEngine.InstallAsync(
                untrusted,
                new EnvironmentContext { RootPath = Path.Combine(work, "untrusted") });

            failures += Report("unvetted entry blocked", untrustedResult.State == InstallState.Unsupported);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // Best effort.
            }

            // The payload was installed into a temp sandbox; do not leave records
            // pointing at it in the user's real data.
            host.Installations.Remove("demo_portable");
            host.Installations.Remove("demo_tampered");
            host.Installations.Remove("demo_script");
            host.Installations.Remove("demo_untrusted");
        }

        return failures;
    }

    // Synthetic package standing in for verified official data; provenance
    // itself is exercised by --catalog.
    private static SoftwareDefinition Portable(string url, string sha) => new()
    {
        Id = "demo_portable",
        Name = "Demo Portable",
        CategoryId = "Utility",
        DirectoryName = "Demo_Portable",
        Mode = InstallationMode.Portable,
        Trust = SoftwareTrust.Verified,
        Provenance = EriReborn.Core.Catalog.CatalogProvenance.Official,
        Detector = DetectorSpec.None,
        Sources = new[]
        {
            new SoftwareSource { Kind = SourceKind.HttpUrl, Url = url, Sha256 = sha, FileName = "demo.zip" },
        },
    };

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = entry.Open();
                writer.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return stream.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
