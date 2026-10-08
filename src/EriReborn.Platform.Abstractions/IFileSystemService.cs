namespace EriReborn.Platform.Abstractions;

/// <summary>File operations behind an abstraction so Core stays platform-free (spec 5).</summary>
public interface IFileSystemService
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    long GetFileSize(string path);

    /// <summary>
    /// Free bytes on the volume that holds <paramref name="path"/>, or null when it cannot be read.
    ///
    /// <para>
    /// Null rather than zero: "we could not read this" and "this disk is full" lead to different
    /// decisions, and a plan that reported 0 would stop an install that is perfectly fine (spec 10/151).
    /// </para>
    /// </summary>
    long? GetAvailableFreeSpace(string path);

    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default);

    Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    string GetTempDirectory();

    string GetApplicationDataDirectory();

    IEnumerable<string> EnumerateFiles(string directory, string pattern, bool recursive);
}
