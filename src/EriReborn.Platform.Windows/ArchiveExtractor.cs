using System.Diagnostics;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace EriReborn.Platform.Windows;

/// <summary>
/// Extracts archive packages beyond plain zip (7z / rar / tar and the
/// compressed-tar family tgz / tbz2 / txz, plus single-file gz / bz2 / xz).
///
/// <para>
/// <see cref="System.IO.Compression.ZipFile"/> only speaks zip, so every other
/// archive fell through to "unsupported package type" even though the download
/// itself succeeded. Extraction lives here instead of in the installer so the
/// zip-slip guard is testable without standing up the whole install pipeline.
/// </para>
///
/// <para>
/// Two engines because neither covers 7z: <see cref="ReaderFactory"/> unwraps
/// compression chains (".tar.gz" yields the tar's entries, not one useless
/// inner ".tar") but refuses 7z outright — 7z is a random-access format the
/// forward-only reader API cannot stream — and SharpCompress's 7z reader fails
/// on real-world 7z files with "File does not have a stream". The tar.exe
/// Windows has shipped since Windows 10 (libarchive) reads them in seconds and
/// refuses ".." entries on its own, so 7z goes through it.
/// </para>
/// </summary>
public static class ArchiveExtractor
{
    private static readonly HashSet<string> PlainExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z",
        ".rar",
        ".tar",
        ".tgz",
        ".tbz2",
        ".txz",
        ".tlz",
        ".gz",
        ".bz2",
        ".xz",
        ".lz",
    };

    /// <summary>True when the package is an archive this extractor can unpack.</summary>
    public static bool IsArchive(string packagePath)
    {
        var extension = Path.GetExtension(packagePath);
        return PlainExtensions.Contains(extension);
    }

    /// <summary>
    /// Extracts every file entry into <paramref name="targetDirectory"/>,
    /// preserving the archive's folder structure and overwriting existing files.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// An entry points outside the target directory, or the extractor rejected
    /// the archive. Failing the whole install is deliberate: silently skipping
    /// the hostile entries would still install a package nobody vetted.
    /// </exception>
    public static void Extract(string packagePath, string targetDirectory)
    {
        var root = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(root);

        if (Path.GetExtension(packagePath).Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            Extract7z(packagePath, root);
            return;
        }

        using var stream = File.OpenRead(packagePath);
        using var reader = ReaderFactory.Open(stream);
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, entry.Key));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"压缩包内存在越界路径：'{entry.Key}'。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            reader.WriteEntryToFile(destination, new ExtractionOptions { Overwrite = true });
        }
    }

    /// <summary>
    /// 7z via the tar.exe Windows has shipped since Windows 10 17063 (libarchive).
    /// It detects the format from the bytes, refuses absolute paths and ".."
    /// entries, and reports anything it skipped on stderr with a non-zero exit.
    /// </summary>
    private static void Extract7z(string packagePath, string root)
    {
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar))
        {
            throw new InvalidOperationException(
                "这台 Windows 没有自带的 tar.exe（Windows 10 及以上才有），7z 包解不开。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = tar,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.Default,
        };
        startInfo.ArgumentList.Add("-xf");
        startInfo.ArgumentList.Add(packagePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(root);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("tar.exe 没有启动起来。");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var detail = stderr.Trim();
            throw new InvalidDataException(
                string.IsNullOrEmpty(detail) ? "7z 解压失败，tar.exe 没有给出原因。" : $"7z 解压失败：{detail}");
        }
    }
}
