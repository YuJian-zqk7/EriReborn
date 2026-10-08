using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Extension.Marketplace;
using EriReborn.Extension.Signing;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Drives real signed installation through the real installer. The same package
/// claims the same trust level in every case; only the signature differs, which
/// is the whole point (spec 35).
/// </summary>
internal static class SignatureCheck
{
    /// <summary>Declares the highest trust level, which only a signature may grant.</summary>
    private const string ManifestJson = """
    {
      "id": "sample_hello",
      "name": "Hello Extension",
      "version": "1.0.0",
      "assembly": "EriReborn.SampleExtension.dll",
      "trust": "official",
      "permissions": [ "network.http" ]
    }
    """;

    public static async Task<int> RunAsync(AppHost host)
    {
        var failures = 0;
        var work = Path.Combine(Path.GetTempPath(), "erireborn-signature", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var sampleAssembly = Path.Combine(AppContext.BaseDirectory, "EriReborn.SampleExtension.dll");
            if (!File.Exists(sampleAssembly))
            {
                Console.WriteLine($"FAIL sample extension assembly missing: {sampleAssembly}");
                return 1;
            }

            // A key pair generated for this run; only its public half is trusted.
            var (publicKey, privateKey) = PackageSigner.CreateKeyPair();
            host.TrustedKeys.Add(new TrustedKey
            {
                KeyId = "smoke-run",
                PublicKey = publicKey,
                Trust = SoftwareTrust.Official,
                Publisher = "Smoke Run",
            });

            var extensionsRoot = Path.Combine(work, "extensions");
            using var server = new LocalHttpServer();

            // 1) Unsigned, but claiming "official": must be refused.
            var unsignedOutcome = await InstallAsync(
                host, server, work, extensionsRoot, sampleAssembly, "/unsigned.zip", sign: false, tamper: false);
            failures += Report("unsigned package claiming official is refused", !unsignedOutcome.Success);
            Console.WriteLine($"      {unsignedOutcome.Message}");

            // 2) Correctly signed: accepted.
            var signedOutcome = await InstallAsync(
                host, server, work, extensionsRoot, sampleAssembly, "/signed.zip",
                sign: true, tamper: false, signingKey: privateKey);
            failures += Report("signed package installs", signedOutcome.Success);
            Console.WriteLine($"      {signedOutcome.Message}");

            // 3) Signed, then tampered with: refused.
            var tamperedOutcome = await InstallAsync(
                host, server, work, extensionsRoot, sampleAssembly, "/tampered.zip",
                sign: true, tamper: true, signingKey: privateKey);
            failures += Report("tampered signed package is refused", !tamperedOutcome.Success);
            Console.WriteLine($"      {tamperedOutcome.Message}");

            // 4) A signature from a key nobody trusts: refused.
            var stranger = PackageSigner.CreateKeyPair();
            var strangerOutcome = await InstallAsync(
                host, server, work, extensionsRoot, sampleAssembly, "/stranger.zip",
                sign: true, tamper: false, signingKey: stranger.PrivateKey, keyId: "not-in-store");
            failures += Report("signature from an untrusted key is refused", !strangerOutcome.Success);
            Console.WriteLine($"      {strangerOutcome.Message}");
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
        }

        return failures;
    }

    private static async Task<ExtensionInstallOutcome> InstallAsync(
        AppHost host,
        LocalHttpServer server,
        string work,
        string extensionsRoot,
        string assembly,
        string url,
        bool sign,
        bool tamper,
        string? signingKey = null,
        string keyId = "smoke-run")
    {
        var packageDirectory = Path.Combine(work, "pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "manifest.json"), ManifestJson);
        var dllPath = Path.Combine(packageDirectory, "EriReborn.SampleExtension.dll");
        await File.WriteAllBytesAsync(dllPath, await File.ReadAllBytesAsync(assembly));

        if (sign)
        {
            var signature = PackageSigner.Sign(packageDirectory, keyId, signingKey!, "Smoke Run");

            // Tampering happens AFTER signing, which is what a signature must catch.
            if (tamper)
            {
                await File.WriteAllTextAsync(dllPath, "tampered payload");
            }

            PackageSigner.Write(packageDirectory, signature);
        }

        var package = ZipDirectory(packageDirectory);
        var sha = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        server.Add(url, package);

        var entry = new MarketplaceEntry
        {
            Id = "sample_hello",
            Name = "Hello Extension",
            Version = "1.0.0",
            Author = "EriReborn",
            Trust = SoftwareTrust.Official,
            DownloadUrl = server.Url(url),
            Sha256 = sha,
            SizeBytes = package.Length,
        };

        var target = Path.Combine(extensionsRoot, "sample_hello");
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        return await host.ExtensionInstaller.InstallAsync(entry, extensionsRoot, work);
    }

    private static byte[] ZipDirectory(string directory)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, relative);
            }
        }

        return stream.ToArray();
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
