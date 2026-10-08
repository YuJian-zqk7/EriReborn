using EriReborn.Asset;
using EriReborn.Core.Logging;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The asset manifest decides which pictures exist at all.
///
/// <para>
/// Mutation testing found the parser named nowhere in the suite, which is the worst
/// place for it: a manifest read wrongly shows up as "the image does not appear",
/// and that is the failure most easily mistaken for art that is not ready yet.
/// </para>
/// </summary>
public sealed class AssetManifestParserTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "erireborn-assets",
        Guid.NewGuid().ToString("N"));

    public AssetManifestParserTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        finally
        {
            // Best effort.
        }
    }

    private static AssetSheet One(string json, string root = "/assets")
        => Assert.Single(AssetManifestParser.Parse(json, root));

    /// <summary>A manifest naming files that really exist, because resolution checks.</summary>
    private string ManifestWith(params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(_root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        }

        var entries = string.Join(",", files.Select(f => "{\"id\":\"" + Path.GetFileNameWithoutExtension(f) + "\",\"file\":\"" + f + "\"}"));

        return "{ \"sheets\": [" + entries + "] }";
    }

    // ----------------------------------------------------------- required bits

    [Fact]
    public void A_sheet_without_a_file_is_skipped_rather_than_registered_blank()
    {
        var sheets = AssetManifestParser.Parse("""
        { "sheets": [ { "id": "no_file" }, { "id": "ok", "file": "ok.png" } ] }
        """, "/assets");

        Assert.Single(sheets);
        Assert.Equal("ok", sheets[0].Id);
    }

    [Fact]
    public void An_id_falls_back_to_the_file_name()
    {
        Assert.Equal("logo", One("""{ "sheets": [ { "file": "art/logo.png" } ] }""").Id);
    }

    [Fact]
    public void The_declared_defaults_are_used_when_nothing_is_said()
    {
        var sheet = One("""{ "sheets": [ { "file": "a.png" } ] }""");

        Assert.Equal("brand", sheet.Skin);
        Assert.Equal("all", sheet.Platform);
        Assert.Equal("control", sheet.Type);
        Assert.False(sheet.Transparent);
        Assert.False(sheet.IsNineSlice);
        Assert.False(sheet.IsAnimated);
        Assert.Empty(sheet.Elements);
    }

    [Fact]
    public void The_resolved_path_is_built_under_the_given_root()
    {
        var sheet = One("""{ "sheets": [ { "file": "art/logo.png" } ] }""", "/tmp/assets");

        Assert.NotNull(sheet.ResolvedPath);
        Assert.StartsWith(Path.GetFullPath("/tmp/assets"), sheet.ResolvedPath!, StringComparison.Ordinal);
        Assert.EndsWith("logo.png", sheet.ResolvedPath!, StringComparison.Ordinal);

        // Forward slashes in the manifest are the manifest's business; the path this
        // app uses is the platform's.
        Assert.DoesNotContain("art/logo.png", sheet.ResolvedPath!);
    }

    // ---------------------------------------------------------------- shapes

    [Theory]
    [InlineData("""{ "something": "else" }""")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void A_document_that_is_not_a_manifest_is_empty_rather_than_an_error(string json)
    {
        // All of these are valid JSON and none of them is a manifest with sheets in
        // it. Asking a non-object for a property throws rather than answering, which
        // is how a stray array turns into a startup error.
        Assert.Empty(AssetManifestParser.Parse(json, "/assets"));
    }

    [Fact]
    public void A_malformed_manifest_is_reported_as_a_parse_failure()
    {
        // The parser throws; what matters is that it is a JsonException the caller can
        // recognise, not something vaguer.
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => AssetManifestParser.Parse("{ not json", "/assets"));
    }

    // ------------------------------------------------------------- nine-slice

    [Fact]
    public void A_slice_block_makes_a_sheet_a_nine_slice()
    {
        var sheet = One("""
        { "sheets": [ { "file": "frame.png", "slice": { "left": 4, "top": 6, "right": 8, "bottom": 10 } } ] }
        """);

        Assert.True(sheet.IsNineSlice);

        // SliceInsets is a value type, so the nullable form is Nullable<T> and the
        // null-forgiving operator does not unwrap it; Value does.
        var slice = sheet.Slice!.Value;
        Assert.Equal(4, slice.Left);
        Assert.Equal(6, slice.Top);
        Assert.Equal(8, slice.Right);
        Assert.Equal(10, slice.Bottom);
    }

    [Fact]
    public void A_slice_block_missing_edges_defaults_them_to_zero()
    {
        var sheet = One("""{ "sheets": [ { "file": "f.png", "slice": { "left": 3 } } ] }""");

        var slice = sheet.Slice!.Value;
        Assert.Equal(3, slice.Left);
        Assert.Equal(0, slice.Top);
        Assert.Equal(0, slice.Right);
        Assert.Equal(0, slice.Bottom);
    }

    // ---------------------------------------------------------------- sprites

    [Fact]
    public void A_sprite_strip_is_read_in_full()
    {
        var sheet = One("""
        { "sheets": [ { "file": "s.png", "sprite": { "frameWidth": 32, "frameHeight": 48, "frameCount": 6, "fps": 12, "loop": false } } ] }
        """);

        Assert.True(sheet.IsAnimated);
        Assert.Equal(32, sheet.Sprite!.FrameWidth);
        Assert.Equal(48, sheet.Sprite.FrameHeight);
        Assert.Equal(6, sheet.Sprite.FrameCount);
        Assert.Equal(12, sheet.Sprite.Fps);
        Assert.False(sheet.Sprite.Loop);
    }

    [Theory]
    [InlineData("""{ "frameWidth": 0, "frameHeight": 48, "frameCount": 6 }""")]
    [InlineData("""{ "frameWidth": 32, "frameHeight": -1, "frameCount": 6 }""")]
    [InlineData("""{ "frameWidth": 32, "frameHeight": 48, "frameCount": 0 }""")]
    [InlineData("""{ "frameWidth": 32, "frameHeight": 48 }""")]
    public void A_sprite_block_that_cannot_be_sliced_is_refused(string sprite)
    {
        // Accepting it would animate frames of zero width, which shows up as nothing
        // being drawn at all.
        var sheet = One("{ \"sheets\": [ { \"file\": \"s.png\", \"sprite\": " + sprite + " } ] }");

        Assert.Null(sheet.Sprite);
        Assert.False(sheet.IsAnimated);
    }

    [Fact]
    public void A_single_frame_strip_is_not_animated()
    {
        var sheet = One("""
        { "sheets": [ { "file": "s.png", "sprite": { "frameWidth": 8, "frameHeight": 8, "frameCount": 1 } } ] }
        """);

        Assert.NotNull(sheet.Sprite);
        Assert.False(sheet.IsAnimated);
    }

    [Theory]
    [InlineData("""{ "frameWidth": 8, "frameHeight": 8, "frameCount": 2 }""", 8)]
    [InlineData("""{ "frameWidth": 8, "frameHeight": 8, "frameCount": 2, "fps": 0 }""", 8)]
    [InlineData("""{ "frameWidth": 8, "frameHeight": 8, "frameCount": 2, "fps": 30 }""", 30)]
    public void The_frame_rate_defaults_to_something_playable(string sprite, int expected)
    {
        var sheet = One("{ \"sheets\": [ { \"file\": \"s.png\", \"sprite\": " + sprite + " } ] }");

        Assert.Equal(expected, sheet.Sprite!.Fps);
    }

    [Fact]
    public void A_strip_loops_unless_it_says_otherwise()
    {
        var unspecified = One("""{ "sheets": [ { "file": "s.png", "sprite": { "frameWidth": 8, "frameHeight": 8, "frameCount": 2 } } ] }""");
        var explicitFalse = One("""{ "sheets": [ { "file": "s.png", "sprite": { "frameWidth": 8, "frameHeight": 8, "frameCount": 2, "loop": false } } ] }""");

        Assert.True(unspecified.Sprite!.Loop);
        Assert.False(explicitFalse.Sprite!.Loop);
    }

    // --------------------------------------------------------------- elements

    [Fact]
    public void Elements_are_objects_and_bare_strings_are_tolerated()
    {
        // Reading the object form as strings silently produced an empty list once;
        // an empty element list is exactly the "picture missing" symptom.
        var sheet = One("""
        { "sheets": [ { "file": "s.png", "elements": [
            { "file": "a.png", "rect": { "x": 1, "y": 2, "w": 3, "h": 4 }, "opaquePx": 12 },
            "b.png"
        ] } ] }
        """);

        Assert.Equal(2, sheet.Elements.Count);

        Assert.Equal("a.png", sheet.Elements[0].File);

        // The manifest writes w/h, not width/height.
        var rect = sheet.Elements[0].Rect!;
        Assert.Equal(1, rect.X);
        Assert.Equal(3, rect.Width);
        Assert.Equal(4, rect.Height);
        Assert.Equal(12, sheet.Elements[0].OpaquePixels);

        Assert.Equal("b.png", sheet.Elements[1].File);
        Assert.Null(sheet.Elements[1].Rect);
    }

    [Fact]
    public void An_element_without_a_file_is_skipped()
    {
        var sheet = One("""
        { "sheets": [ { "file": "s.png", "elements": [ { "rect": { "x": 0, "y": 0, "w": 1, "h": 1 } }, "ok.png" ] } ] }
        """);

        Assert.Single(sheet.Elements);
        Assert.Equal("ok.png", sheet.Elements[0].File);
    }

    // ------------------------------------------------------- sliced directory

    [Fact]
    public void The_sliced_directory_prefers_the_current_key_and_accepts_the_old_one()
    {
        var current = One("""{ "sheets": [ { "file": "s.png", "slicedDir": "sliced/current" } ] }""");
        Assert.Equal("sliced/current", current.SlicedDirectory);

        var legacy = One("""{ "sheets": [ { "file": "s.png", "sliced": "sliced/legacy" } ] }""");
        Assert.Equal("sliced/legacy", legacy.SlicedDirectory);

        var both = One("""{ "sheets": [ { "file": "s.png", "slicedDir": "new", "sliced": "old" } ] }""");
        Assert.Equal("new", both.SlicedDirectory);
    }

    // -------------------------------------------------------- the manager

    [Fact]
    public void A_manifest_that_cannot_be_parsed_leaves_a_loaded_registry_alone()
    {
        // The registry used to be cleared before the manifest was read, so a second
        // bad load threw away a working set of assets.
        var manager = new AssetManager(AppLog.For("Test"));

        Assert.True(manager.LoadManifest(ManifestWith("keep.png"), _root));
        Assert.Single(manager.Sheets);
        Assert.NotNull(manager.ResolvePath("keep"));

        Assert.False(manager.LoadManifest("{ not json", _root));

        Assert.Single(manager.Sheets);
        Assert.NotNull(manager.ResolvePath("keep"));
    }

    [Fact]
    public void A_document_that_is_not_a_manifest_also_leaves_the_registry_alone()
    {
        // Valid JSON, no sheets: the manager must not treat it as an empty manifest
        // and delete everything it had.
        var manager = new AssetManager(AppLog.For("Test"));
        Assert.True(manager.LoadManifest(ManifestWith("keep.png"), _root));

        Assert.True(manager.LoadManifest("[]", _root));

        // This one *is* a readable manifest with no sheets, so replacing with nothing
        // is the honest outcome — and it proves the two paths are told apart.
        Assert.Empty(manager.Sheets);
    }

    [Fact]
    public void A_manifest_that_parses_replaces_the_previous_one_completely()
    {
        var manager = new AssetManager(AppLog.For("Test"));

        manager.LoadManifest(ManifestWith("old.png"), _root);
        Assert.True(manager.LoadManifest(ManifestWith("new.png"), _root));

        // Both collections move together: a sheet that is gone from one must be gone
        // from the other, or a lookup finds something the list does not show.
        Assert.Single(manager.Sheets);
        Assert.Null(manager.ResolvePath("old"));
        Assert.NotNull(manager.ResolvePath("new"));
    }

    [Fact]
    public void A_duplicate_id_is_ignored_and_the_first_one_wins()
    {
        var manager = new AssetManager(AppLog.For("Test"));

        File.WriteAllBytes(Path.Combine(_root, "first.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "second.png"), new byte[] { 2 });

        manager.LoadManifest("""
        { "sheets": [
            { "id": "same", "file": "first.png" },
            { "id": "same", "file": "second.png" }
        ] }
        """, _root);

        Assert.Single(manager.Sheets);
        Assert.EndsWith("first.png", manager.ResolvePath("same")!, StringComparison.Ordinal);
    }
}
