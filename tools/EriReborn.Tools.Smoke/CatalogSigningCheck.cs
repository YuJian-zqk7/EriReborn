using System.Security.Cryptography;
using System.Text.Json;
using EriReborn.App.Shared;
using EriReborn.Core.Catalog;
using EriReborn.Core.Logging;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Release tooling for the official catalog signature.
///
/// The private key is deliberately kept OUTSIDE the repository, under the user
/// data directory. It is never shipped and never committed; losing it means the
/// current pinned public key can no longer be signed with, and a new key must be
/// pinned in an application update.
/// </summary>
internal static class CatalogSigningCheck
{
    private const string KeyId = "erireborn-catalog-2026";

    private static string KeyFilePath(AppPaths paths)
        => Path.Combine(paths.UserDataDirectory, "keys", "catalog-signing-key.json");

    /// <summary>Generates a fresh P-256 key pair and prints the public half to pin.</summary>
    public static int GenerateKey(AppPaths paths)
    {
        var path = KeyFilePath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        var payload = new
        {
            schema = 1,
            keyId = KeyId,
            algorithm = CatalogTrustAnchor.DefaultAlgorithm,
            publisher = "EriReborn",
            publicKey,
            privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem(),
            note = "目录签名私钥。绝不入库、绝不随包分发。",
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"      private key: {path}");
        Console.WriteLine();
        Console.WriteLine("      pin this public key into CatalogTrustAnchor:");
        Console.WriteLine($"      new CatalogTrustKey(\"{KeyId}\", CatalogTrustAnchor.DefaultAlgorithm, \"{publicKey}\", \"EriReborn\"),");

        return 0;
    }

    /// <summary>Re-signs every *.json in the catalog directory.</summary>
    public static int Sign(AppPaths paths)
    {
        var keyPath = KeyFilePath(paths);
        if (!File.Exists(keyPath))
        {
            Console.WriteLine($"SKIP sign: no signing key at {keyPath}. Run --gencatalogkey first.");
            return 0;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(keyPath));
        var pem = document.RootElement.GetProperty("privateKeyPem").GetString()
            ?? throw new InvalidOperationException("Signing key file has no privateKeyPem.");
        var publisher = document.RootElement.TryGetProperty("publisher", out var p) ? p.GetString() : null;

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);

        var files = CatalogSignature.BuildFileList(paths.CatalogDirectory);
        if (files.Count == 0)
        {
            Console.WriteLine("FAIL sign: the catalog directory has no json files.");
            return 1;
        }

        var payload = CatalogSignature.ComputePayload(files);
        var signature = Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256));

        var record = new CatalogSignature
        {
            KeyId = document.RootElement.GetProperty("keyId").GetString() ?? KeyId,
            Algorithm = document.RootElement.TryGetProperty("algorithm", out var a) ? a.GetString() ?? CatalogTrustAnchor.DefaultAlgorithm : CatalogTrustAnchor.DefaultAlgorithm,
            Publisher = publisher,
            Files = files,
            Signature = signature,
        };

        var target = Path.Combine(paths.CatalogDirectory, CatalogSignature.FileName);
        File.WriteAllText(target, CatalogSignature.Serialize(record));

        Console.WriteLine($"      signed {files.Count} file(s) -> {target}");

        // Re-verify through the production path so a bad signature cannot ship.
        var verdict = CatalogVerifier.Verify(paths.CatalogDirectory, AppLog.For("Sign"));
        Console.WriteLine($"      re-verify: {verdict.State} — {verdict.Message}");

        return verdict.State == CatalogSignatureState.Verified ? 0 : 1;
    }
}
