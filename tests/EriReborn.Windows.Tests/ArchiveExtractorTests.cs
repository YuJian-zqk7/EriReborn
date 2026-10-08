using EriReborn.Platform.Windows;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using Xunit;

namespace EriReborn.Windows.Tests;

/// <summary>
/// The install pipeline used to reject every archive that was not a zip with
/// "不支持的安装包类型", so a .7z that downloaded fine could never install.
/// These tests pin the extraction path for the newly supported formats and the
/// zip-slip guard that comes with accepting arbitrary archives.
/// </summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "erireborn-archive-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("package.7z")]
    [InlineData("package.rar")]
    [InlineData("package.tar")]
    [InlineData("package.tar.gz")]
    [InlineData("package.tgz")]
    [InlineData("package.tar.bz2")]
    [InlineData("package.tar.xz")]
    [InlineData("single.gz")]
    [InlineData("single.bz2")]
    [InlineData("single.xz")]
    [InlineData("PACKAGE.7Z")]
    public void The_new_formats_are_recognized_as_archives(string fileName)
    {
        Assert.True(ArchiveExtractor.IsArchive(fileName));
    }

    [Theory]
    [InlineData("installer.exe")]
    [InlineData("installer.msi")]
    [InlineData("notes.txt")]
    [InlineData("noextension")]
    public void Non_archives_are_not_claimed(string fileName)
    {
        Assert.False(ArchiveExtractor.IsArchive(fileName));
    }

    [Fact]
    public void A_compressed_tar_extracts_its_files_not_the_inner_tar()
    {
        // ".tar.gz" must unwrap both layers. Treating it as a plain gzip would
        // leave a single useless .tar file in the target directory instead of
        // the program the user asked for.
        var archivePath = Path.Combine(_root, "pkg.tar.gz");
        Directory.CreateDirectory(_root);
        WriteTarGz(archivePath, ("app/run.exe", new byte[] { 1, 2, 3 }), ("app/readme.txt", new byte[] { 4, 5 }));

        var target = Path.Combine(_root, "out");
        Directory.CreateDirectory(target);
        ArchiveExtractor.Extract(archivePath, target);

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(target, "app", "run.exe")));
        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(Path.Combine(target, "app", "readme.txt")));
    }

    [Fact]
    public void An_entry_pointing_outside_the_target_directory_is_refused()
    {
        var archivePath = Path.Combine(_root, "evil.tar");
        Directory.CreateDirectory(_root);
        WriteTar(archivePath, ("../escape.txt", new byte[] { 9 }));

        var target = Path.Combine(_root, "out");
        Directory.CreateDirectory(target);

        Assert.Throws<InvalidDataException>(() => ArchiveExtractor.Extract(archivePath, target));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public void A_7z_package_is_extracted_by_the_system_tar()
    {
        // SharpCompress cannot author 7z (read-only format there), so a tar wearing
        // a .7z name exercises the whole dispatch: the system tar sniffs content,
        // not extensions — which is also how the real 7z from the share was proven.
        var archivePath = Path.Combine(_root, "pkg.7z");
        Directory.CreateDirectory(_root);
        WriteTar(archivePath, ("bin/tool.exe", new byte[] { 1, 2, 3 }));

        var target = Path.Combine(_root, "out");
        ArchiveExtractor.Extract(archivePath, target);

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(target, "bin", "tool.exe")));
    }

    [Fact]
    public void A_7z_entry_pointing_outside_the_target_directory_is_refused()
    {
        // bsdtar refuses ".." entries and exits non-zero; that must surface as a
        // failed install, not as a silently half-written directory.
        var archivePath = Path.Combine(_root, "evil.7z");
        Directory.CreateDirectory(_root);
        WriteTar(archivePath, ("../escape.txt", new byte[] { 9 }));

        var target = Path.Combine(_root, "out");

        Assert.Throws<InvalidDataException>(() => ArchiveExtractor.Extract(archivePath, target));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    private static void WriteTarGz(string path, params (string Key, byte[] Content)[] entries)
    {
        using var archive = TarArchive.Create();
        foreach (var (key, content) in entries)
        {
            archive.AddEntry(key, new MemoryStream(content), closeStream: true, content.Length);
        }

        using var file = File.Create(path);
        archive.SaveTo(file, new WriterOptions(CompressionType.GZip));
    }

    private static void WriteTar(string path, params (string Key, byte[] Content)[] entries)
    {
        using var archive = TarArchive.Create();
        foreach (var (key, content) in entries)
        {
            archive.AddEntry(key, new MemoryStream(content), closeStream: true, content.Length);
        }

        using var file = File.Create(path);
        archive.SaveTo(file, new WriterOptions(CompressionType.None));
    }
}
