using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The workshop edits skin text through an override file, because the skins themselves ship
/// inside the read-only asset tree. This covers that layer: edit a shipped skin, derive a new
/// one, delete the override and get the shipped text back.
/// </summary>
public sealed class SkinOverrideTests
{
    private static (SkinOverrideStore Store, IReadOnlyList<SkinManifest> Shipped, string UserData) Create()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        var shipped = new[]
        {
            new SkinManifest
            {
                Id = "eri_win",
                Name = "Eri（Windows）",
                Persona = "出厂 persona",
                Layout = "desktop",
                Colors = new Dictionary<string, string> { ["SkinAccent"] = "#5B8DEF" },
            },
        };

        return (new SkinOverrideStore(paths, AppLog.For("Test")), shipped, userData);
    }

    private static void Cleanup(string userData)
    {
        try
        {
            if (Directory.Exists(userData))
            {
                Directory.Delete(userData, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public void An_edit_rewrites_the_text_without_touching_the_shipped_skin()
    {
        var (store, shipped, userData) = Create();
        try
        {
            Assert.True(store.Save(new SkinTextOverride("eri_win", "eri_win", "我的 Eri", "改过的 persona")));

            var edited = store.Apply(shipped).Single(skin => skin.Id == "eri_win");

            Assert.Equal("我的 Eri", edited.Name);
            Assert.Equal("改过的 persona", edited.Persona);

            // The parts of a skin that are not text still come from the shipped manifest.
            Assert.Equal("#5B8DEF", edited.Colors["SkinAccent"]);

            // And the shipped manifest itself is untouched: the override is a layer.
            Assert.Equal("Eri（Windows）", shipped[0].Name);
            Assert.Equal("出厂 persona", shipped[0].Persona);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void An_override_with_a_new_id_derives_a_skin_from_its_base()
    {
        var (store, shipped, userData) = Create();
        try
        {
            Assert.True(store.Save(new SkinTextOverride("my_skin", "eri_win", "我的皮肤", "我的语气")));

            var derived = store.Apply(shipped).Single(skin => skin.Id == "my_skin");

            Assert.Equal("我的皮肤", derived.Name);
            Assert.Equal("我的语气", derived.Persona);

            // Borrowed from the base, so the user is not asked to author a whole skin.
            Assert.Equal(shipped[0].Layout, derived.Layout);
            Assert.Equal("#5B8DEF", derived.Colors["SkinAccent"]);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Editing_deriving_and_deleting_move_the_live_skin_list()
    {
        // Driven through the host, not just the store: an override is registered into the skin
        // engine, so deleting the file has to take the registered manifest with it — otherwise
        // the app would keep showing the text the user just deleted.
        var root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var skinsRoot = Path.Combine(root, "assets", "skins", "eri_win");
            Directory.CreateDirectory(skinsRoot);
            await File.WriteAllTextAsync(
                Path.Combine(skinsRoot, "skin.json"),
                """{ "id": "eri_win", "name": "Eri（Windows）", "persona": "出厂 persona", "layout": "desktop" }""");

            var userData = Path.Combine(root, "userdata");
            var paths = AppPaths.Detect(assetsOverride: Path.Combine(root, "assets"), userDataOverride: userData);

            var host = await AppHost.CreateAsync(
                paths,
                new TestPlatform(
                    new TestFileSystemService(userData),
                    new TestNetworkService(new HttpClient()),
                    new InMemoryCredentialStore()),
                new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
                AppLog.For("Test"));

            var store = new SkinOverrideStore(host.Paths, AppLog.For("Test"));
            Assert.Equal("Eri（Windows）", host.Skins.Available.Single(skin => skin.Id == "eri_win").Name);

            Assert.True(store.Save(new SkinTextOverride("eri_win", "eri_win", "我的 Eri", "改过的 persona")));
            host.ReloadSkinOverrides();

            var edited = host.Skins.Available.Single(skin => skin.Id == "eri_win");
            Assert.Equal("我的 Eri", edited.Name);
            Assert.Equal("改过的 persona", edited.Persona);

            // A new id derives a skin rather than replacing one.
            Assert.True(store.Save(new SkinTextOverride("my_skin", "eri_win", "我的皮肤", "我的语气")));
            host.ReloadSkinOverrides();
            Assert.Equal("我的皮肤", host.Skins.Available.Single(skin => skin.Id == "my_skin").Name);

            Assert.True(store.Delete("eri_win"));
            host.ReloadSkinOverrides();

            var restored = host.Skins.Available.Single(skin => skin.Id == "eri_win");
            Assert.Equal("Eri（Windows）", restored.Name);
            Assert.Equal("出厂 persona", restored.Persona);

            // Only the deleted override went away; the derived skin is still there.
            Assert.Equal("我的皮肤", host.Skins.Available.Single(skin => skin.Id == "my_skin").Name);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Applying_twice_produces_the_same_skin()
    {
        var (store, shipped, userData) = Create();
        try
        {
            store.Save(new SkinTextOverride("my_skin", "eri_win", "我的皮肤", "我的语气"));

            // The reload path applies overrides on every change, so a second pass must not
            // derive a second skin or lose the edit.
            var first = store.Apply(shipped);
            var twice = shipped.Concat(first).ToList();
            var second = store.Apply(twice);

            Assert.Single(second);
            Assert.Equal("my_skin", second[0].Id);
            Assert.Equal("我的皮肤", second[0].Name);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void An_id_that_cannot_be_a_file_name_is_refused()
    {
        var (store, shipped, userData) = Create();
        try
        {
            // The id becomes a file name in the user data directory.
            Assert.False(store.Save(new SkinTextOverride("不是合法名字", null, "x", "y")));
            Assert.False(store.IsOverridden("不是合法名字"));
        }
        finally
        {
            Cleanup(userData);
        }
    }
}
