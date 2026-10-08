using System.Security.Cryptography;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Android;

/// <summary>
/// File access rooted in the application's private sandbox. Android gives each
/// app its own directory; nothing here reaches outside it (spec 5).
/// </summary>
public sealed class AndroidFileSystemService : IFileSystemService
{
    private readonly string _filesDir;
    private readonly string _cacheDir;

    public AndroidFileSystemService()
    {
        var context = global::Android.App.Application.Context;
        _filesDir = context.FilesDir?.AbsolutePath ?? Path.GetTempPath();
        _cacheDir = context.CacheDir?.AbsolutePath ?? Path.GetTempPath();
        Directory.CreateDirectory(_filesDir);
    }

    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public bool DirectoryExists(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    public long GetFileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>
    /// Free space on the volume the sandbox lives on. Reads the real mounted volume rather than a
    /// figure derived from the app's own usage, because the question being answered is "does this
    /// machine have room", and returns null when the volume cannot be queried.
    /// </summary>
    public long? GetAvailableFreeSpace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Directory.CreateDirectory(path);
        }

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
        var path = Path.Combine(_cacheDir, "work");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetApplicationDataDirectory()
    {
        var path = Path.Combine(_filesDir, "data");
        Directory.CreateDirectory(path);
        return path;
    }

    public IEnumerable<string> EnumerateFiles(string directory, string pattern, bool recursive)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
}
