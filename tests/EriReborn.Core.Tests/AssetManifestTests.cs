using EriReborn.Asset;
using EriReborn.Core.Logging;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The shipped manifest is the contract for sliced assets and sprite animation
/// (spec 50/51). These tests read the real file, so a key rename or a shape
/// change that the parser ignores fails here.
/// </summary>
public sealed class AssetManifestTests
{
    private static string AssetsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "asset_manifest.json");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "assets");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("assets/asset_manifest.json was not found above the test directory.");
    }

    private static AssetManager Load()
    {
        var root = AssetsRoot();
        var manager = new AssetManager(AppLog.For("Test"));
        manager.LoadManifest(File.ReadAllText(Path.Combine(root, "asset_manifest.json")), root);
        return manager;
    }

    [Fact]
    public void The_manifest_registers_every_sheet()
    {
        var manager = Load();

        // Compared against what the manifest itself declares, not a fixed number:
        // the intent is that nothing is dropped, and a literal here would have to
        // be edited every time art is added — which is how a real drop gets waved
        // through as "just the count changing".
        var declared = System.Text.Json.JsonDocument
            .Parse(File.ReadAllText(Path.Combine(AssetsRoot(), "asset_manifest.json")))
            .RootElement.GetProperty("sheets")
            .GetArrayLength();

        Assert.Equal(declared, manager.Sheets.Count);
        Assert.NotNull(manager.Resolve("erireborn_launcher_icon"));
    }

    [Fact]
    public void A_declared_slice_is_read_as_a_nine_slice_frame()
    {
        var manager = Load();
        var frame = manager.Resolve("android_style_frame");

        Assert.NotNull(frame);
        Assert.True(frame!.IsNineSlice);

        // Insets were chosen by looking at the art: rounded corners plus the
        // shadow under the panel.
        Assert.Equal(new SliceInsets(16, 16, 16, 28), frame.Slice);
    }

    [Fact]
    public void The_declared_frame_is_found_by_skin()
    {
        var manager = Load();

        var frame = manager.FrameFor("android_style");

        Assert.NotNull(frame);
        Assert.Equal(new SliceInsets(16, 16, 16, 28), frame!.Slice);
    }

    [Fact]
    public void A_skin_without_frame_art_reports_no_frame_rather_than_borrowing_one()
    {
        var manager = Load();

        // Borrowing another skin's art would put android corners on a Windows panel.
        // The Eri skins now ship a frame of their own, so the no-frame cases are the
        // skins that deliberately draw flat (Tech keeps a hairline) plus the nonsense
        // ids, which must never resolve to somebody else's art.
        var eriWindows = manager.FrameFor("eri_windows");

        Assert.NotNull(eriWindows);
        Assert.NotEqual("android_style_frame", eriWindows!.Id);
        Assert.True(File.Exists(manager.ResolvePath(eriWindows.Id)), "Eri 外框文件不存在。");

        Assert.Null(manager.FrameFor("tech_windows"));
        Assert.Null(manager.FrameFor(null));
        Assert.Null(manager.FrameFor(""));
        Assert.Null(manager.FrameFor("no-such-skin"));
    }

    [Fact]
    public void A_frame_is_a_real_file_that_exists()
    {
        var manager = Load();
        var frame = manager.FrameFor("android_style");

        Assert.NotNull(frame);

        var path = manager.ResolvePath(frame!.Id);
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path), $"九宫格边框文件不存在：{path}");

        var sheet = manager.Resolve(frame.Id);
        Assert.True(sheet!.IsNineSlice);
    }

    [Fact]
    public void A_sheet_without_a_slice_is_not_a_frame()
    {
        var manager = Load();

        // Guessing insets from the pixels would turn any sprite into a stretched border.
        Assert.False(manager.Resolve("erireborn_launcher_icon")!.IsNineSlice);
    }

    [Fact]
    public void Sliced_directories_are_read_from_the_slicedDir_key()
    {
        var sheet = Load().Resolve("eri_windows_character");

        Assert.NotNull(sheet);
        Assert.Equal("skins/eri_windows/sliced", sheet!.SlicedDirectory);
        Assert.Equal("character", sheet.Type);
    }

    [Fact]
    public void Elements_are_parsed_as_objects_with_rectangles()
    {
        var sheet = Load().Resolve("eri_windows_character")!;

        // Reading "elements" as a string array used to yield zero entries.
        Assert.Equal(6, sheet.Elements.Count);

        var first = sheet.Elements[0];
        Assert.Equal("sliced/eri_windows_character_01.png", first.File);
        Assert.NotNull(first.Rect);
        Assert.Equal(294, first.Rect!.Width);
        Assert.Equal(464, first.Rect.Height);
        Assert.True(first.OpaquePixels > 0);
    }

    [Fact]
    public void Sprite_metadata_is_parsed()
    {
        var sheet = Load().Resolve("eri_windows_character")!;

        Assert.True(sheet.IsAnimated);
        Assert.NotNull(sheet.Sprite);
        Assert.Equal(294, sheet.Sprite!.FrameWidth);
        Assert.Equal(464, sheet.Sprite.FrameHeight);
        Assert.Equal(6, sheet.Sprite.FrameCount);
        // Six distinct poses at 8 fps read as a twitch; the loop was deliberately slowed.
        Assert.Equal(3, sheet.Sprite.Fps);
        Assert.True(sheet.Sprite.Loop);
    }

    [Fact]
    public void Frames_resolve_to_files_that_exist_on_disk()
    {
        var manager = Load();

        var frames = manager.ResolveFrames("eri_windows_character");

        Assert.Equal(6, frames.Count);
        Assert.All(frames, f => Assert.True(File.Exists(f), f));
        Assert.EndsWith("eri_windows_character_06.png", frames[5]);
    }

    [Fact]
    public void A_sheet_without_slices_or_sprite_reports_none()
    {
        var manager = Load();
        var sheet = manager.Resolve("erireborn_launcher_icon")!;

        Assert.Null(sheet.SlicedDirectory);
        Assert.Empty(sheet.Elements);
        Assert.Null(sheet.Sprite);
        Assert.False(sheet.IsAnimated);
        Assert.Empty(manager.ResolveFrames("erireborn_launcher_icon"));
        Assert.NotNull(manager.ResolvePath("erireborn_launcher_icon"));
    }

    [Fact]
    public void Unknown_ids_resolve_to_nothing_instead_of_throwing()
    {
        var manager = Load();

        Assert.Null(manager.Resolve("not_a_real_asset"));
        Assert.Null(manager.ResolvePath("not_a_real_asset"));
        Assert.Empty(manager.ResolveFrames("not_a_real_asset"));
        Assert.Equal(2, manager.MissingCount);
    }
}
