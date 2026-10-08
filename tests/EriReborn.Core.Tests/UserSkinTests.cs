using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.Asset;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A user skin is a thin file that names a base skin and lists only what it changes, so the rest
/// keeps coming from the pack. That is what makes "restore the default" expressible and what
/// keeps a user skin from freezing a snapshot of the pack it was built on.
/// </summary>
public sealed class UserSkinTests
{
    private static SkinManifest BaseSkin() => new()
    {
        Id = "eri_win",
        Name = "Eri（Windows）",
        Persona = "出厂 persona",
        Theme = "light",
        Layout = "desktop",
        FontFamily = "Segoe UI",
        Colors = new Dictionary<string, string> { ["SkinAccent"] = "#5B8DEF" },
        Metrics = new Dictionary<string, string> { ["cornerRadius"] = "22" },
        Assets = new Dictionary<string, string> { ["logo"] = "brand_logo_horizontal", ["character"] = "eri_idle" },
    };

    private static (UserSkinStore Store, string UserData) CreateStore()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);
        return (new UserSkinStore(paths, AppLog.For("Test")), userData);
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public void A_new_skin_names_its_base_instead_of_copying_it()
    {
        var (store, userData) = CreateStore();
        try
        {
            Assert.True(store.CreateFrom(BaseSkin(), "my_skin", "我的皮肤", "我的语气", out var reason), reason);

            var file = SkinManifestParser.Parse(
                File.ReadAllText(store.ManifestPath("my_skin")),
                store.ManifestPath("my_skin"));

            Assert.Equal("my_skin", file.Id);
            Assert.Equal("我的皮肤", file.Name);
            Assert.Equal("eri_win", file.BaseSkin);

            // Thin on purpose: the pack's colours and sizes are not copied into the file.
            Assert.Empty(file.Colors);

            // Resolved over the base, it is complete.
            var merged = store.Apply(new[] { BaseSkin() }).Single(skin => skin.Id == "my_skin");

            Assert.Equal("我的皮肤", merged.Name);
            Assert.Equal("我的语气", merged.Persona);
            Assert.Equal("eri_win", merged.BaseSkin);
            Assert.Equal("#5B8DEF", merged.Colors["SkinAccent"]);
            Assert.Equal("22", merged.Metrics["cornerRadius"]);
            Assert.Equal("brand_logo_horizontal", merged.Assets["logo"]);
            Assert.Equal("light", merged.Theme);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void An_untouched_slot_keeps_the_bases_picture_and_can_be_restored()
    {
        var (store, userData) = CreateStore();
        try
        {
            store.CreateFrom(BaseSkin(), "my_skin", "我的皮肤", null, out _);

            Assert.True(store.SetSlot("my_skin", "logo", "my_logo"));

            var replaced = store.Apply(new[] { BaseSkin() }).Single();
            Assert.Equal("my_logo", replaced.Assets["logo"]);

            // Untouched slots keep coming from the pack.
            Assert.Equal("eri_idle", replaced.Assets["character"]);

            // Clearing the override is how "restore the default" is expressed.
            Assert.True(store.SetSlot("my_skin", "logo", null));

            var restored = store.Apply(new[] { BaseSkin() }).Single();
            Assert.Equal("brand_logo_horizontal", restored.Assets["logo"]);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void A_user_skin_whose_base_is_missing_is_reported_not_silently_dropped()
    {
        var (store, userData) = CreateStore();
        try
        {
            store.CreateFrom(BaseSkin(), "my_skin", "我的皮肤", null, out _);

            // Nothing to build on: resolving it would mean inventing the missing half.
            Assert.Empty(store.Apply(Array.Empty<SkinManifest>()));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_user_skin_over_a_pack_resolves_without_touching_the_pack()
    {
        var shippedRoot = Temp();
        var userData = Path.Combine(shippedRoot, "userdata");
        try
        {
            // AppPaths puts discovered packs under <assets>/skins, so the fixture has to as well.
            var shippedDir = Path.Combine(shippedRoot, "skins", "eri_win");
            Directory.CreateDirectory(shippedDir);
            await File.WriteAllTextAsync(
                Path.Combine(shippedDir, "skin.json"),
                """
                {
                  "id": "eri_win",
                  "name": "Eri（Windows）",
                  "persona": "出厂 persona",
                  "layout": "desktop",
                  "colors": { "SkinAccent": "#5B8DEF" },
                  "assets": { "logo": "brand_logo_horizontal" }
                }
                """);

            var paths = AppPaths.Detect(assetsOverride: shippedRoot, userDataOverride: userData);
            var host = await AppHost.CreateAsync(
                paths,
                new TestPlatform(
                    new TestFileSystemService(userData),
                    new TestNetworkService(new HttpClient()),
                    new InMemoryCredentialStore()),
                new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
                AppLog.For("Test"));

            // The store has to write where this host reads, or the test would pass by looking at a
            // different folder than the one under test.
            var store = new UserSkinStore(host.Paths, AppLog.For("Test"));

            var pack = host.Skins.Available.Single(skin => skin.Id == "eri_win");
            Assert.True(store.CreateFrom(pack, "my_eri", "我的 Eri", "我的语气", out var reason), reason);
            host.ReloadUserSkins();

            var mine = host.Skins.Available.Single(skin => skin.Id == "my_eri");
            Assert.Equal("我的 Eri", mine.Name);
            Assert.Equal("我的语气", mine.Persona);

            // The pack's colour and picture come along from the pack, not from a copy.
            Assert.Equal("#5B8DEF", mine.Colors["SkinAccent"]);
            Assert.Equal("brand_logo_horizontal", mine.Assets["logo"]);

            // And the pack is still exactly what it shipped as.
            var untouched = host.Skins.Available.Single(skin => skin.Id == "eri_win");
            Assert.Equal("Eri（Windows）", untouched.Name);
            Assert.Null(untouched.BaseSkin);
        }
        finally
        {
            Cleanup(shippedRoot);
        }
    }

    [Fact]
    public void Text_is_written_into_the_users_own_file()
    {
        var (store, userData) = CreateStore();
        try
        {
            store.CreateFrom(BaseSkin(), "my_skin", "我的皮肤", null, out _);

            Assert.True(store.SaveText("my_skin", "改过的名字", "改过的语气"));

            var merged = store.Apply(new[] { BaseSkin() }).Single();
            Assert.Equal("改过的名字", merged.Name);
            Assert.Equal("改过的语气", merged.Persona);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void A_shipped_skin_is_not_ours_to_edit_or_delete()
    {
        var (store, userData) = CreateStore();
        try
        {
            Assert.False(store.IsUserSkin("eri_win"));
            Assert.False(store.SaveText("eri_win", "x", "y"));
            Assert.False(store.SetSlot("eri_win", "logo", "x"));
            Assert.False(store.Delete("eri_win"));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void An_id_that_cannot_be_a_folder_is_refused()
    {
        var (store, userData) = CreateStore();
        try
        {
            Assert.False(store.CreateFrom(BaseSkin(), "不是合法名字", "x", null, out var reason));
            Assert.Contains("合法", reason);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    // ------------------------------------------------------------- pictures

    [Fact]
    public async Task Replacing_a_picture_copies_it_in_declares_it_and_resolves()
    {
        var (store, userData) = CreateStore();
        try
        {
            store.CreateFrom(BaseSkin(), "my_skin", "我的皮肤", null, out _);

            // A picture the user picked, from somewhere else entirely.
            var source = Path.Combine(userData, "picked.png");
            await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });

            Assert.True(store.ImportArt("my_skin", "logo", source, out var reason), reason);

            // Copied into the skin, so moving the original afterwards cannot break it.
            Assert.True(File.Exists(Path.Combine(userData, "skins", "my_skin", "assets", "logo.png")));

            Assert.Equal("my_skin_logo", store.ReadOverrides("my_skin")["logo"]);

            var resolved = store.Apply(new[] { BaseSkin() }).Single();

            // The resolved skin points at the new picture *and* says where its own pack lives —
            // without that second half the art would never be loaded.
            Assert.Equal("my_skin_logo", resolved.Assets["logo"]);
            Assert.Equal("asset_manifest.json", resolved.AssetManifestFile);

            // Untouched slots still come from the pack.
            Assert.Equal("eri_idle", resolved.Assets["character"]);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Restoring_a_picture_drops_the_override()
    {
        var (store, userData) = CreateStore();
        try
        {
            store.CreateFrom(BaseSkin(), "my_skin", "我的皮肤", null, out _);

            var source = Path.Combine(userData, "picked.png");
            await File.WriteAllBytesAsync(source, new byte[] { 1 });
            store.ImportArt("my_skin", "logo", source, out _);

            Assert.True(store.SetSlot("my_skin", "logo", null));

            Assert.False(store.ReadOverrides("my_skin").ContainsKey("logo"));
            Assert.Equal("brand_logo_horizontal", store.Apply(new[] { BaseSkin() }).Single().Assets["logo"]);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void A_shipped_pack_cannot_have_its_pictures_replaced()
    {
        var (store, userData) = CreateStore();
        try
        {
            var source = Path.Combine(userData, "picked.png");
            File.WriteAllBytes(source, new byte[] { 1 });

            Assert.False(store.ImportArt("eri_win", "logo", source, out var reason));
            Assert.Contains("只读", reason);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task The_preview_finds_the_users_picture_from_their_own_pack()
    {
        var (store, userData) = CreateStore();
        try
        {
            store.CreateFrom(BaseSkin(), "my_skin", "我的皮肤", null, out _);

            var source = Path.Combine(userData, "picked.png");
            await File.WriteAllBytesAsync(source, new byte[] { 1 });
            store.ImportArt("my_skin", "logo", source, out _);

            // Found from the skin's own manifest rather than the asset registry: the app registers
            // only the art of the skin in force, and the skin being edited usually is not that one —
            // so a replaced picture would have previewed as nothing.
            var path = store.ResolveArtPath("my_skin", "my_skin_logo");

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Equal("logo.png", Path.GetFileName(path));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void An_id_that_is_not_this_skins_own_resolves_to_nothing()
    {
        var (store, userData) = CreateStore();
        try
        {
            store.CreateFrom(BaseSkin(), "my_skin", "我的皮肤", null, out _);

            // A shipped id is not in the user's pack, so the page falls back to the registry for it.
            Assert.Null(store.ResolveArtPath("my_skin", "brand_logo_horizontal"));
            Assert.Null(store.ResolveArtPath("my_skin", null));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    /// <summary>
    /// The acceptance run from the skin-creator spec, as one test: build a skin from a base, change
    /// an icon, a picture and a word, then check the changes are on disk and the base skin cannot be
    /// edited at all. The preview those changes appear in is the real interface, which the app
    /// renders from exactly this data.
    /// </summary>
    [Fact]
    public async Task A_skin_built_from_a_base_keeps_its_changes_and_leaves_the_base_alone()
    {
        var (store, userData) = CreateStore();
        try
        {
            // 1. 基于底稿建一套自己的皮肤
            Assert.True(
                store.CreateFrom(BaseSkin(), "MyTestSkin", "我的测试皮肤", "Eri", out var reason),
                reason);
            Assert.True(store.IsUserSkin("MyTestSkin"));

            // 2. 它一开始什么都不覆盖 —— 是"底稿 + 覆盖"，不是把整包抄一份
            Assert.Contains("\"baseSkin\"", File.ReadAllText(store.ManifestPath("MyTestSkin")));
            Assert.Empty(store.ReadOverrides("MyTestSkin"));
            Assert.Empty(store.ReadTextOverrides("MyTestSkin"));

            // 3. 换一个图标、换一张图：同一个入口，界面里点谁就是谁
            var icon = Path.Combine(userData, "my_settings.png");
            var picture = Path.Combine(userData, "my_character.png");
            await File.WriteAllBytesAsync(icon, new byte[] { 1, 2, 3 });
            await File.WriteAllBytesAsync(picture, new byte[] { 4, 5, 6 });

            Assert.True(store.ReplaceAsset("MyTestSkin", "icon_settings", icon, out reason), reason);
            Assert.True(store.ReplaceAsset("MyTestSkin", "character", picture, out reason), reason);

            // 4. 改一段界面文字
            Assert.True(store.SetText("MyTestSkin", "nav.settings", "参数"));

            // 5. 改动落在磁盘上（重新读盘就是重启后的样子）
            var overrides = store.ReadOverrides("MyTestSkin");
            Assert.Equal("MyTestSkin_icon_settings", overrides["icon_settings"]);
            Assert.Equal("MyTestSkin_character", overrides["character"]);
            Assert.Equal("参数", store.ReadTextOverrides("MyTestSkin")["nav.settings"]);

            var resolved = store.ResolveArtPath("MyTestSkin", "MyTestSkin_icon_settings");
            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));

            // 6. 官方皮肤没有被碰过：它只读，而且有理由地拒绝
            Assert.False(store.IsUserSkin("eri_windows"));
            Assert.False(store.ReplaceAsset("eri_windows", "icon_settings", icon, out var refused));
            Assert.Contains("只读", refused);

            // 7. 恢复默认 = 丢掉这一条覆盖，底稿那张图回来
            Assert.True(store.RestoreAsset("MyTestSkin", "icon_settings"));
            Assert.False(store.ReadOverrides("MyTestSkin").ContainsKey("icon_settings"));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task An_asset_name_that_would_walk_out_of_the_skin_is_neutralised()
    {
        var (store, userData) = CreateStore();
        try
        {
            Assert.True(store.CreateFrom(BaseSkin(), "MyTestSkin", "我的测试皮肤", null, out _));

            var source = Path.Combine(userData, "x.png");
            await File.WriteAllBytesAsync(source, new byte[] { 1 });

            // The name is used as a file name, so a value like "../../evil" must not escape the
            // skin's own folder.
            Assert.True(store.ReplaceAsset("MyTestSkin", "../../evil", source, out _));

            var resolved = store.ResolveArtPath("MyTestSkin", "MyTestSkin_.._.._evil");
            Assert.NotNull(resolved);

            var skinFolder = Path.GetDirectoryName(store.ManifestPath("MyTestSkin"))!;
            Assert.StartsWith(Path.GetFullPath(skinFolder), Path.GetFullPath(resolved));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    /// <summary>
    /// A skin's words and colours have to reach the manifest the app actually applies.
    ///
    /// <para>
    /// They did not: the merge overlaid assets, colours and metrics, and dropped the words — so the
    /// editor wrote "this button says 参数", read it back happily, and the interface went on saying
    /// 设置. Testing the file alone could never catch that; this tests the merge.
    /// </para>
    /// </summary>
    [Fact]
    public void A_skins_words_and_colours_reach_the_merged_manifest()
    {
        var (store, userData) = CreateStore();
        try
        {
            var baseSkin = BaseSkin();
            Assert.True(store.CreateFrom(baseSkin, "MyTestSkin", "我的测试皮肤", null, out _));
            Assert.True(store.SetText("MyTestSkin", "nav.settings", "参数"));
            Assert.True(store.SetColor("MyTestSkin", "accent", "#123456"));

            var merged = store.Apply(new[] { baseSkin }).Single(skin => skin.Id == "MyTestSkin");

            Assert.Equal("参数", UiTexts.Resolve(merged, "nav.settings"));
            Assert.Equal("#123456", merged.Colors["accent"]);

            // What the user did not touch keeps coming from the base.
            Assert.Equal(UiTexts.Defaults["nav.home"], UiTexts.Resolve(merged, "nav.home"));
            Assert.Equal(baseSkin.Id, merged.BaseSkin);
            Assert.Equal("我的测试皮肤", merged.Name);

            // 重置 = 回到纯继承：更改没了，皮肤还在。
            Assert.True(store.ResetOverrides("MyTestSkin"));
            Assert.Empty(store.ReadOverrides("MyTestSkin"));
            Assert.Empty(store.ReadTextOverrides("MyTestSkin"));
            Assert.Empty(store.ReadColorOverrides("MyTestSkin"));

            var reset = store.Apply(new[] { baseSkin }).Single(skin => skin.Id == "MyTestSkin");
            Assert.Equal(UiTexts.Defaults["nav.settings"], UiTexts.Resolve(reset, "nav.settings"));
            Assert.Equal(baseSkin.Colors.GetValueOrDefault("accent"), reset.Colors.GetValueOrDefault("accent"));
            Assert.Equal("我的测试皮肤", reset.Name);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_pictures_size_is_read_from_its_own_header()
    {
        var (_, userData) = CreateStore();
        try
        {
            // Signature + IHDR type + 68 × 42, big-endian. Enough of a PNG to state its size.
            var png = Path.Combine(userData, "probe.png");
            await File.WriteAllBytesAsync(png, new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x44, 0x00, 0x00, 0x00, 0x2A,
            });

            var size = ImageInfo.Size(png);
            Assert.NotNull(size);
            Assert.Equal(68, size.Value.Width);
            Assert.Equal(42, size.Value.Height);
            Assert.Equal("PNG · 68 × 42", ImageInfo.Describe(png));

            // A format whose size is not stated up front reports its format and no size, rather than
            // an invented one.
            var other = Path.Combine(userData, "probe.webp");
            await File.WriteAllBytesAsync(other, new byte[24]);
            Assert.Null(ImageInfo.Size(other));
            Assert.Equal("WEBP", ImageInfo.Format(other));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void Interface_words_are_overlaid_per_skin_and_can_be_restored()
    {
        var (store, userData) = CreateStore();
        try
        {
            store.CreateFrom(BaseSkin(), "my_skin", "我的皮肤", null, out _);

            Assert.True(store.SetText("my_skin", "nav.settings", "参数"));
            var overrides = store.ReadTextOverrides("my_skin");
            Assert.Equal("参数", overrides["nav.settings"]);

            // Clearing is how "恢复默认" is expressed: the key goes away and the shipped wording
            // comes back, exactly as it does for colours and pictures.
            Assert.True(store.SetText("my_skin", "nav.settings", null));
            Assert.Empty(store.ReadTextOverrides("my_skin"));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void A_skin_is_not_required_to_carry_every_word()
    {
        // Only what the skin changes: renaming one button must not mean translating everything.
        var skin = BaseSkin() with
        {
            Texts = new Dictionary<string, string>(StringComparer.Ordinal) { ["nav.settings"] = "参数" },
        };

        Assert.Equal("参数", UiTexts.Resolve(skin, "nav.settings"));
        Assert.Equal(UiTexts.Defaults["nav.home"], UiTexts.Resolve(skin, "nav.home"));
        Assert.Equal(UiTexts.Defaults["nav.home"], UiTexts.Resolve(null, "nav.home"));
    }

    [Fact]
    public void An_unknown_key_shows_the_key_rather_than_nothing()
    {
        // A typo in a view has to be visible: a silently blank label is the one failure nobody can
        // act on.
        Assert.Equal("nav.typo", UiTexts.Resolve(null, "nav.typo"));
    }

    [Fact]
    public void A_skins_own_pictures_are_additions_and_a_skin_switch_drops_them()
    {
        var assets = new AssetManager(AppLog.For("Test"));
        var root = Temp();

        // The shipped library: it must survive a skin's art being added and removed.
        Assert.True(assets.LoadManifest("""{ "sheets": [ { "id": "shipped", "file": "shipped.png" } ] }""", root));

        Assert.True(assets.LoadSkinSheets("""{ "sheets": [ { "id": "mine", "file": "mine.png" } ] }""", root));
        Assert.NotNull(assets.Resolve("mine"));
        Assert.NotNull(assets.Resolve("shipped"));

        // Applying another skin takes the previous skin's art back out, and nothing else.
        Assert.True(assets.LoadSkinSheets(null, null));
        Assert.Null(assets.Resolve("mine"));
        Assert.NotNull(assets.Resolve("shipped"));
    }
}
