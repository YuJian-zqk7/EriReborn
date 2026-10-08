using System.Security.Cryptography;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Core.Tests.TestSupport;

/// <summary>Test double for IFileSystemService (spec 68 allows doubles in tests).</summary>
public sealed class TestFileSystemService(string tempRoot) : IFileSystemService
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public long GetFileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>
    /// Free space this double reports, or null for "the platform could not tell".
    ///
    /// <para>
    /// Null by default on purpose: a test that does not care about disk space should see the same
    /// "unknown" a platform without the capability reports, not a plausible-looking number.
    /// </para>
    /// </summary>
    public long? FreeSpaceBytes { get; set; }

    public long? GetAvailableFreeSpace(string path) => FreeSpaceBytes;

    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public string GetTempDirectory()
    {
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    public string GetApplicationDataDirectory() => tempRoot;

    public IEnumerable<string> EnumerateFiles(string directory, string pattern, bool recursive)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
}
