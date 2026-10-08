using System.Security.Cryptography;
using System.Text;

namespace EriReborn.Core.Catalog;

/// <summary>One catalog file and its SHA-256 digest.</summary>
public sealed record CatalogFileDigest(string Path, string Sha256);

/// <summary>
/// A detached signature over the official catalog's file list. This is a
/// different mechanism from the extension package signature: it covers
/// different content, is verified against a different anchor, and confers
/// official authority rather than package trust.
/// </summary>
public sealed record CatalogSignature
{
    public const string FileName = "catalog.sig";

    public int Schema { get; init; } = 1;

    public required string KeyId { get; init; }

    public string Algorithm { get; init; } = CatalogTrustAnchor.DefaultAlgorithm;

    public string? Publisher { get; init; }

    /// <summary>Every file the signature covers, with its digest.</summary>
    public IReadOnlyList<CatalogFileDigest> Files { get; init; } = Array.Empty<CatalogFileDigest>();

    /// <summary>Base64 signature over <see cref="ComputePayload"/>.</summary>
    public required string Signature { get; init; }

    /// <summary>
    /// Canonical payload: "path:sha256" lines in ordinal order. The file list is
    /// inside the signed payload, so adding, removing or reordering entries
    /// invalidates the signature.
    /// </summary>
    public static byte[] ComputePayload(IEnumerable<CatalogFileDigest> files)
    {
        var lines = files
            .Select(file => $"{file.Path}:{file.Sha256}")
            .OrderBy(line => line, StringComparer.Ordinal);

        return Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }

    public byte[] Payload() => ComputePayload(Files);

    public static string HashFile(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>The digest list a signature over this directory would cover.</summary>
    public static IReadOnlyList<CatalogFileDigest> BuildFileList(string directory)
        => Directory
            .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new CatalogFileDigest(Path.GetFileName(path), HashFile(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

    public static string Serialize(CatalogSignature signature) => System.Text.Json.JsonSerializer.Serialize(
        signature,
        new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
}
