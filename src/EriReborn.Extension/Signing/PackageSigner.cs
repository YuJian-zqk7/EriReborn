using System.Security.Cryptography;
using System.Text.Json;

namespace EriReborn.Extension.Signing;

/// <summary>
/// Produces package signatures. This is the publishing side of
/// <see cref="SignatureVerifier"/>, used by tooling and tests so a signed
/// package can be produced and then verified for real.
/// </summary>
public static class PackageSigner
{
    public const string EcdsaAlgorithm = "ecdsa-p256-sha256";

    /// <summary>Creates a P-256 key pair as (base64 public key, PKCS#8 private PEM).</summary>
    public static (string PublicKey, string PrivateKey) CreateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()), ecdsa.ExportPkcs8PrivateKeyPem());
    }

    public static PackageSignature Sign(
        string directory,
        string keyId,
        string privateKeyPem,
        string? publisher = null)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);

        var payload = PackageSignature.ComputePayload(directory);
        var signature = ecdsa.SignData(payload, HashAlgorithmName.SHA256);

        return new PackageSignature
        {
            KeyId = keyId,
            Algorithm = EcdsaAlgorithm,
            Signature = Convert.ToBase64String(signature),
            Publisher = publisher,
        };
    }

    /// <summary>Writes signature.json into the package directory.</summary>
    public static void Write(string directory, PackageSignature signature)
        => File.WriteAllText(
            Path.Combine(directory, PackageSignature.FileName),
            JsonSerializer.Serialize(signature, new JsonSerializerOptions { WriteIndented = true }));
}
