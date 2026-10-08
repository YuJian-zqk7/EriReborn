using System.Security.Cryptography;
using System.Text;

namespace EriReborn.Engine.Download;

/// <summary>
/// Cache identity deliberately excludes a bare URL (spec 25). Software id,
/// version, architecture, provider, URL and the expected hash all take part,
/// so two builds of the same product never collide.
/// </summary>
public sealed record DownloadCacheKey(
    string? SoftwareId,
    string? Version,
    string? Architecture,
    string? ProviderId,
    string Url,
    string? Sha256)
{
    public string Canonical()
    {
        var sb = new StringBuilder();
        sb.Append("sw=").Append(SoftwareId ?? "-").Append('|');
        sb.Append("ver=").Append(Version ?? "-").Append('|');
        sb.Append("arch=").Append(Architecture ?? "-").Append('|');
        sb.Append("provider=").Append(ProviderId ?? "-").Append('|');
        sb.Append("url=").Append(Url).Append('|');
        sb.Append("sha=").Append(Sha256 ?? "-");
        return sb.ToString();
    }

    /// <summary>Stable, filesystem-safe file name for this identity.</summary>
    public string ToStableName()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>On-disk cache of verified downloads, keyed by <see cref="DownloadCacheKey"/>.</summary>
public sealed class DownloadCache(string rootDirectory)
{
    private readonly string _root = rootDirectory;

    public string Root => _root;

    public string GetEntryPath(DownloadCacheKey key) => Path.Combine(_root, key.ToStableName());

    public bool TryGetVerified(DownloadCacheKey key, out string path)
    {
        path = GetEntryPath(key);
        if (!File.Exists(path))
        {
            return false;
        }

        // A cache entry is only trusted when it carries a hash and still matches it.
        if (string.IsNullOrWhiteSpace(key.Sha256))
        {
            return false;
        }

        try
        {
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (string.Equals(actual, key.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            File.Delete(path);
            return false;
        }
        catch
        {
            return false;
        }
    }

    public void Store(DownloadCacheKey key, string sourceFile)
    {
        Directory.CreateDirectory(_root);
        var target = GetEntryPath(key);
        if (string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        File.Copy(sourceFile, target, overwrite: true);
    }
}
