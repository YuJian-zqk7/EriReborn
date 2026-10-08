using EriReborn.Platform.Windows;
using Xunit;

namespace EriReborn.Windows.Tests;

/// <summary>
/// A portable archive that wraps everything in one folder must not leave the
/// program nested below the install directory.
/// </summary>
public sealed class ArchiveLayoutTests : IDisposable
{
    private readonly string _root;

    public ArchiveLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "erireborn-win-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void A_single_wrapper_folder_is_flattened()
    {
        var target = Path.Combine(_root, "target");
        var wrapper = Path.Combine(target, "ripgrep-14.1.1-x86_64-pc-windows-msvc");
        Directory.CreateDirectory(Path.Combine(wrapper, "doc"));
        File.WriteAllText(Path.Combine(wrapper, "rg.exe"), "binary");
        File.WriteAllText(Path.Combine(wrapper, "doc", "README.md"), "docs");

        Assert.True(ArchiveLayout.FlattenSingleRoot(target));

        Assert.True(File.Exists(Path.Combine(target, "rg.exe")));
        Assert.True(File.Exists(Path.Combine(target, "doc", "README.md")));
        Assert.False(Directory.Exists(wrapper));
    }

    [Fact]
    public void Several_top_level_entries_are_left_alone()
    {
        var target = Path.Combine(_root, "target2");
        Directory.CreateDirectory(Path.Combine(target, "app"));
        File.WriteAllText(Path.Combine(target, "app", "a.txt"), "a");
        File.WriteAllText(Path.Combine(target, "setup.exe"), "s");

        Assert.False(ArchiveLayout.FlattenSingleRoot(target));

        Assert.True(Directory.Exists(Path.Combine(target, "app")));
        Assert.True(File.Exists(Path.Combine(target, "setup.exe")));
    }

    [Fact]
    public void A_single_top_level_file_is_left_alone()
    {
        var target = Path.Combine(_root, "target3");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "only.exe"), "binary");

        Assert.False(ArchiveLayout.FlattenSingleRoot(target));
        Assert.True(File.Exists(Path.Combine(target, "only.exe")));
    }

    [Fact]
    public void An_empty_directory_is_left_alone()
    {
        var target = Path.Combine(_root, "target4");
        Directory.CreateDirectory(target);

        Assert.False(ArchiveLayout.FlattenSingleRoot(target));
    }

    [Fact]
    public void A_missing_directory_is_reported_rather_than_throwing()
    {
        Assert.False(ArchiveLayout.FlattenSingleRoot(Path.Combine(_root, "nope")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
