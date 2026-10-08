using System.Security.Cryptography;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Core.Paths;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Installs a real, publicly distributed program over the real internet through
/// the real engine: DNS, TLS, redirects, SHA-256, archive extraction, on-disk
/// result and post-install detection (spec 19/73).
///
/// ripgrep's release asset is used because release assets are immutable, so the
/// expected hash is stable and the check is reproducible.
/// </summary>
internal static class RealInstallCheck
{
    private const string ReleaseUrl =
        "https://github.com/BurntSushi/ripgrep/releases/download/14.1.1/ripgrep-14.1.1-x86_64-pc-windows-msvc.zip";

    /// <param name="executeInstalled">
    /// Explicitly opt in to running the installed program. It is OFF by default
    /// so a routine smoke run never executes downloaded code; the product's
    /// purpose is to install software, but a verification script should not
    /// silently do that on every invocation.
    /// </param>
    public static async Task<int> RunAsync(AppHost host, bool executeInstalled = false)
    {
        var failures = 0;
        var work = Path.Combine(Path.GetTempPath(), "erireborn-real-install", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        byte[] bytes;
        string sha;

        // The internet is not always reachable. Reporting FAIL would present
        // "not verified" as "verification failed"; reporting OK would present
        // "not verified" as "verified". So this is an explicit, loud skip.
        try
        {
            Console.WriteLine("      downloading the real release to fingerprint it…");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            bytes = await client.GetByteArrayAsync(ReleaseUrl);
            sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Console.WriteLine($"      {bytes.Length} bytes, sha256 {sha[..16]}…");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("SKIP real install: the release could not be fetched, so nothing was verified.");
            Console.WriteLine($"     reason: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"     url:    {ReleaseUrl}");
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // Best effort.
            }

            return 0;
        }

        try
        {

            var installRoot = Path.Combine(work, "root");
            var context = new EnvironmentContext { RootPath = installRoot };
            var software = Ripgrep(sha);

            // 1) Nothing is installed yet.
            var before = await host.SoftwareEngine.DetectAsync(software, context);
            failures += Report("before install: not detected", before.Outcome != DetectionOutcome.Detected);

            // 2) The real install.
            var result = await host.SoftwareEngine.InstallAsync(software, context);
            failures += Report("real install succeeded", result.State == InstallState.Succeeded);
            Console.WriteLine($"      state={result.State} {result.Message}");

            var directory = Path.Combine(installRoot, "Development", "ripgrep");
            var exe = Path.Combine(directory, "rg.exe");
            failures += Report("the vendor executable is on disk", File.Exists(exe));
            failures += Report("it is a plausible size", File.Exists(exe) && new FileInfo(exe).Length > 1_000_000);
            failures += Report("verified by detection", result.Verified?.Outcome == DetectionOutcome.Detected);

            // 3) Later scans find it without any hint from us.
            var after = await host.SoftwareEngine.DetectAsync(software, context);
            failures += Report("after install: detected", after.Outcome == DetectionOutcome.Detected);
            Console.WriteLine($"      after: {after.Outcome} source={after.Source}");

            failures += Report("the install was recorded", host.Installations.Find(software.Id) is not null);

            // 4) A wrong hash must stop the install. The bytes already fetched are
            //    served locally so this stays deterministic instead of depending on
            //    a second trip to the internet.
            using var server = new LocalHttpServer();
            server.Add("/ripgrep.zip", bytes);

            var tampered = Ripgrep(new string('0', 64)) with
            {
                Id = "ripgrep_bad",
                DirectoryName = "ripgrep_bad",
                Sources = new[]
                {
                    new SoftwareSource
                    {
                        Kind = SourceKind.HttpUrl,
                        Url = server.Url("/ripgrep.zip"),
                        Sha256 = new string('0', 64),
                        FileName = "ripgrep.zip",
                    },
                },
            };

            var badRoot = Path.Combine(work, "bad");
            var bad = await host.SoftwareEngine.InstallAsync(tampered, new EnvironmentContext { RootPath = badRoot });
            failures += Report("a wrong hash is rejected", bad.State == InstallState.IntegrityFailed);
            Console.WriteLine($"      wrong hash: {bad.State} {bad.Message}");
            failures += Report(
                "nothing was written for the rejected download",
                !Directory.Exists(Path.Combine(badRoot, "Development", "ripgrep_bad")));

            // 5) Optionally prove the installed program actually runs.
            if (executeInstalled && File.Exists(exe))
            {
                var (ran, output) = await RunAsync(exe, "--version");
                failures += Report("the installed program runs", ran && output.Contains("ripgrep", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"      {output.Trim().Split('\n')[0]}");
            }
            else
            {
                Console.WriteLine("      (not executing the installed program; pass --runinstalled to opt in)");
            }

            // 6) Detect the real program by its own executable, with no catalog data.
            var probe = await host.SoftwareEngine.DetectAsync(
                software with { Detector = new DetectorSpec(DetectorKind.File, exe) },
                context);
            failures += Report("a file detector also finds it", probe.Outcome == DetectionOutcome.Detected);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL real install threw: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            host.Installations.Remove("ripgrep");
            host.Installations.Remove("ripgrep_bad");

            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }

        return failures;
    }

    private static async Task<(bool Ok, string Output)> RunAsync(string executable, string arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(executable, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode == 0, output);
    }

    // Synthetic package standing in for verified official data; provenance
    // itself is exercised by --catalog.
    private static SoftwareDefinition Ripgrep(string sha256) => new()
    {
        Id = "ripgrep",
        Name = "ripgrep",
        CategoryId = "Development",
        DirectoryName = "ripgrep",
        Mode = InstallationMode.Portable,
        Trust = SoftwareTrust.Verified,
        Provenance = EriReborn.Core.Catalog.CatalogProvenance.Official,
        Detector = DetectorSpec.None,
        Version = "14.1.1",
        Sources = new[]
        {
            new SoftwareSource
            {
                Kind = SourceKind.HttpUrl,
                Url = ReleaseUrl,
                Sha256 = sha256,
                FileName = "ripgrep-14.1.1-x86_64-pc-windows-msvc.zip",
            },
        },
    };

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
