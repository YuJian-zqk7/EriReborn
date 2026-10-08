using System.Security.Cryptography;
using System.Text.Json;
using EriReborn.Core.Logging;

namespace EriReborn.Core.Catalog;

public enum CatalogSignatureState
{
    /// <summary>The official file list matched a signature from a pinned key.</summary>
    Verified,

    /// <summary>No signature was shipped, so nothing can be trusted as official.</summary>
    NoSignature,

    /// <summary>A signature exists but did not verify. Tampering is the usual cause.</summary>
    Invalid,
}

/// <summary>The outcome of verifying the official catalog's signature.</summary>
public sealed record CatalogVerdict(
    CatalogSignatureState State,
    string Message,
    string? KeyId = null,
    string? Publisher = null,
    IReadOnlySet<string>? VerifiedFiles = null)
{
    /// <summary>Only a verified signature confers official authority.</summary>
    public bool GrantsOfficialAuthority => State == CatalogSignatureState.Verified;

    public IReadOnlySet<string> OfficialFiles { get; } =
        VerifiedFiles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static CatalogVerdict NoSignature(string message) =>
        new(CatalogSignatureState.NoSignature, message);

    public static CatalogVerdict Invalid(string message) =>
        new(CatalogSignatureState.Invalid, message);
}

/// <summary>
/// Verifies the official catalog against the pinned trust anchor. The anchor is
/// compiled into the assembly on purpose: a key file sitting next to the data it
/// protects could be swapped along with the data, which would make the signature
/// decorative rather than protective.
/// </summary>
public static class CatalogVerifier
{
    /// <param name="anchor">
    /// The trusted keys. Defaults to the pinned anchor; tests inject their own so
    /// they can sign with a throwaway key.
    /// </param>
    public static CatalogVerdict Verify(
        string directory,
        IAppLogger log,
        IReadOnlyList<CatalogTrustKey>? anchor = null)
    {
        anchor ??= CatalogTrustAnchor.Keys;

        var path = Path.Combine(directory, CatalogSignature.FileName);
        if (!File.Exists(path))
        {
            var message = $"官方目录未随附签名（缺少 {CatalogSignature.FileName}），因此没有任何数据能获得官方权威。";
            log.Warn("catalog.signature", message);
            return CatalogVerdict.NoSignature(message);
        }

        CatalogSignature? signature;
        try
        {
            signature = JsonSerializer.Deserialize<CatalogSignature>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            var message = $"目录签名文件无法解析：{ex.Message}";
            log.Error("catalog.signature", message, ex);
            return CatalogVerdict.Invalid(message);
        }

        if (signature is null || string.IsNullOrWhiteSpace(signature.Signature))
        {
            return Reject(log, "目录签名文件为空或缺少签名字段。");
        }

        var key = anchor.FirstOrDefault(k => string.Equals(k.KeyId, signature.KeyId, StringComparison.Ordinal));
        if (key is null)
        {
            // A key that is not pinned is exactly how an attacker would try to
            // sign their own catalog: swap the key store, then sign.
            return Reject(log, $"签名密钥 '{signature.KeyId}' 不在内置信任根中，拒绝授予官方权威。");
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException)
        {
            return Reject(log, "目录签名不是合法的 base64。");
        }

        bool verified;
        try
        {
            verified = VerifySignature(key, signature.Payload(), signatureBytes);
        }
        catch (CryptographicException ex)
        {
            return Reject(log, $"目录签名校验异常：{ex.Message}");
        }

        if (!verified)
        {
            return Reject(log, "目录签名与文件清单不匹配，官方目录可能已被篡改。");
        }

        // The signature covers the file list, so now confirm the files themselves.
        var verifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in signature.Files)
        {
            var file = Path.Combine(directory, entry.Path);
            if (!File.Exists(file))
            {
                return Reject(log, $"签名清单中的文件 '{entry.Path}' 缺失。");
            }

            var actual = CatalogSignature.HashFile(file);
            if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Reject(log, $"文件 '{entry.Path}' 的摘要与签名清单不符（可能已被修改）。");
            }

            verifiedFiles.Add(entry.Path);
        }

        var ok = $"官方目录签名有效（{key.KeyId}，{verifiedFiles.Count} 个文件）。";
        log.Info("catalog.signature", ok);
        return new CatalogVerdict(CatalogSignatureState.Verified, ok, key.KeyId, key.Publisher, verifiedFiles);
    }

    private static CatalogVerdict Reject(IAppLogger log, string message)
    {
        // Never silent: an unverified official catalog is a security event.
        log.Error("catalog.signature", message, null);
        return CatalogVerdict.Invalid(message);
    }

    private static bool VerifySignature(CatalogTrustKey key, byte[] payload, byte[] signature)
    {
        switch (key.Algorithm.ToLowerInvariant())
        {
            case "rsa-sha256":
                using (var rsa = RSA.Create())
                {
                    ImportKey(rsa, key.PublicKey);
                    return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }

            default:
                using (var ecdsa = ECDsa.Create())
                {
                    ImportKey(ecdsa, key.PublicKey);
                    return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256);
                }
        }
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
