using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EriReborn.Core.Domain;

namespace EriReborn.Extension.Signing;

/// <summary>
/// A detached signature over the package's contents. The signed payload covers
/// every file (not just the manifest), so swapping the assembly invalidates it.
/// </summary>
public sealed record PackageSignature
{
    public const string FileName = "signature.json";

    public required string KeyId { get; init; }

    public string Algorithm { get; init; } = "ecdsa-p256-sha256";

    /// <summary>Base64 signature over <see cref="ComputePayload"/>.</summary>
    public required string Signature { get; init; }

    public string? Publisher { get; init; }

    /// <summary>
    /// Canonical payload: every file except the signature itself, as
    /// "relative/path:sha256" lines in ordinal order.
    /// </summary>
    public static byte[] ComputePayload(string directory)
    {
        var root = Path.GetFullPath(directory);
        var lines = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Relative: Path.GetRelativePath(root, path).Replace('\\', '/'), Full: path))
            .Where(entry => !string.Equals(entry.Relative, FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Relative, StringComparer.Ordinal)
            .Select(entry => $"{entry.Relative}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry.Full))).ToLowerInvariant()}");

        return Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }

    public static PackageSignature? TryRead(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<PackageSignature>(File.ReadAllText(path), options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record SignatureCheck(bool IsValid, string Message, SoftwareTrust Trust, string? Publisher);

/// <summary>
/// Verifies a package signature against the trusted key store. Verification is
/// real asymmetric cryptography; a package that is not signed by a stored key
/// gains no trust, whatever its manifest claims.
/// </summary>
public static class SignatureVerifier
{
    /// <summary>Trust an unsigned package can reach at most.</summary>
    public const SoftwareTrust UnsignedTrust = SoftwareTrust.Community;

    /// <summary>The two algorithms a trusted key may name.</summary>
    public const string EcdsaAlgorithm = "ecdsa-p256-sha256";

    public const string RsaAlgorithm = "rsa-sha256";

    public static SignatureCheck Verify(string directory, PackageSignature signature, TrustedKeyStore keys)
    {
        var key = keys.Find(signature.KeyId);
        if (key is null)
        {
            return new SignatureCheck(false, $"签名使用的密钥 '{signature.KeyId}' 不在信任列表中。", SoftwareTrust.Invalid, null);
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException)
        {
            return new SignatureCheck(false, "签名不是合法的 base64。", SoftwareTrust.Invalid, null);
        }

        var payload = PackageSignature.ComputePayload(directory);

        // A closed set, from the two algorithms the trust store documents. The old
        // default fell through to ECDSA for anything unrecognised, which fails safely
        // but reports "签名与包内容不匹配，可能已被篡改" — sending someone to hunt for
        // tampering when the real answer is that the key names an algorithm this build
        // cannot check.
        var algorithm = (key.Algorithm ?? string.Empty).Trim().ToLowerInvariant();

        if (algorithm is not (RsaAlgorithm or EcdsaAlgorithm))
        {
            return new SignatureCheck(
                false,
                $"信任列表中的密钥 '{key.KeyId}' 指定了不支持的签名算法 '{key.Algorithm}'。",
                SoftwareTrust.Invalid,
                null);
        }

        try
        {
            var ok = algorithm == RsaAlgorithm
                ? VerifyRsa(key.PublicKey, payload, signatureBytes)
                : VerifyEcdsa(key.PublicKey, payload, signatureBytes);

            return ok
                ? new SignatureCheck(true, $"签名有效（{key.KeyId}）。", key.Trust, key.Publisher)
                : new SignatureCheck(false, "签名与包内容不匹配，可能已被篡改。", SoftwareTrust.Invalid, null);
        }
        catch (CryptographicException ex)
        {
            return new SignatureCheck(false, $"签名校验失败：{ex.Message}", SoftwareTrust.Invalid, null);
        }
    }

    private static bool VerifyEcdsa(string publicKey, byte[] payload, byte[] signature)
    {
        using var ecdsa = ECDsa.Create();
        ImportKey(ecdsa, publicKey);
        return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256);
    }

    private static bool VerifyRsa(string publicKey, byte[] payload, byte[] signature)
    {
        using var rsa = RSA.Create();
        ImportKey(rsa, publicKey);
        return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static void ImportKey(AsymmetricAlgorithm algorithm, string publicKey)
    {
        var trimmed = publicKey.Trim();
        if (trimmed.Contains("BEGIN", StringComparison.Ordinal))
        {
            algorithm.ImportFromPem(trimmed);
            return;
        }

        var bytes = Convert.FromBase64String(trimmed);
        switch (algorithm)
        {
            case ECDsa ecdsa:
                ecdsa.ImportSubjectPublicKeyInfo(bytes, out _);
                break;
            case RSA rsa:
                rsa.ImportSubjectPublicKeyInfo(bytes, out _);
                break;
            default:
                throw new CryptographicException("Unsupported key type.");
        }
    }
}
