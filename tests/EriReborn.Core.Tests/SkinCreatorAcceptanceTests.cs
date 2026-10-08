using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The skin creator's acceptance run, in the order a user performs it: build a skin on top of a
/// shipped one, change a picture, a slot and a word, restart, and find every change still there —
/// with the shipped pack untouched.
///
/// <para>
/// This is the flow rather than a unit of it on purpose. The individual writes are covered
/// elsewhere (see <c>UserSkinTests</c>); what is not covered anywhere else is that the whole path a
/// user walks actually ends where it promises to.
/// </para>
/// </summary>
public sealed class SkinCreatorAcceptanceTests
{
    private const string SkinName = "我的测试皮肤";
    private const string BaseId = "eri_windows";

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "skins")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private static string AssetsRoot() => Path.Combine(RepoRoot(), "assets");

    private static string ShippedSkinFile() => Path.Combine(AssetsRoot(), "skins", BaseId, "skin.json");

    private static string ShippedSkinFolder() => Path.Combine(AssetsRoot(), "skins", BaseId);

    /// <summary>A real picture on disk, the way the file picker hands one over.</summary>
    private static string PickedPicture()
        => Path.Combine(AssetsRoot(), "library", "workshop", "ws_persona_editor.png");

    private static Task<AppHost> StartAsync(string userData)
    {
        var paths = AppPaths.Detect(assetsOverride: AssetsRoot(), userDataOverride: userData);

        return AppHost.CreateAsync(
            paths,
            new TestPlatform(
                new TestFileSystemService(userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore()),
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));
    }

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
    public async Task A_skin_built_on_a_shipped_one_keeps_its_changes_across_a_restart()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var shippedBefore = File.ReadAllText(ShippedSkinFile());
            var shippedFilesBefore = Directory.GetFiles(ShippedSkinFolder()).OrderBy(path => path).ToArray();

            // ---- 1. 当前皮肤 → 新建皮肤：底稿默认就是正在用的那套 ----
            var first = await StartAsync(userData);
            var workshop = new WorkshopViewModel(first);

            // Making a skin must not change the interface: it opens it for editing and stops there.
            // Switching is what 启用 does (step 10 below), and it is the user's decision to make. This
            // expectation used to be the opposite — creating the copy also put it in force — which is
            // why creating a skin visibly changed the whole interface before anything was edited.
            string? requested = null;
            workshop.ActivateSkinRequested += (_, id) => requested = id;

            var shipped = workshop.Skins.Single(entry => entry.Id == BaseId);
            workshop.SelectedSkin = shipped;

            workshop.BeginCreateSkinCommand.Execute(null);
            Assert.Equal(BaseId, workshop.CreateBaseSkin?.Id);

            workshop.NewSkinName = SkinName;
            workshop.CreateSkinCommand.Execute(null);

            var created = workshop.Skins.Single(entry => entry.Name == SkinName);
            var skinId = created.Id;

            Assert.True(workshop.IsEditingSkin);
            Assert.Null(requested);

            // Base skin + overrides, not a copy of the pack: one file, and no art yet.
            var stored = new UserSkinStore(first.Paths, first.Log.For("Skin"));
            Assert.True(stored.IsUserSkin(skinId));

            var skinFolder = Path.GetDirectoryName(stored.ManifestPath(skinId))!;
            Assert.Equal(
                new[] { "skin.json" },
                Directory.GetFiles(skinFolder).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray());

            // ---- 2. 换一个图标（界面上的图片按它自己的名字记下来） ----
            workshop.SelectElement("icon_settings", null);
            Assert.True(workshop.SelectedElement?.IsArt);

            workshop.ReplaceElementArt(PickedPicture());

            Assert.True(stored.ReadOverrides(skinId).ContainsKey("icon_settings"));

            // ---- 3. 换一张皮肤槽位图（立绘） ----
            workshop.ReplaceSlotArt(SkinAssets.Character, PickedPicture());

            var overrides = stored.ReadOverrides(skinId);
            Assert.True(overrides.ContainsKey(SkinAssets.Character));

            // The picture is copied into the skin's own pack, not left pointing at where it was
            // picked from: a skin that referenced Downloads would break when the user tidied up.
            var art = Directory.GetFiles(Path.Combine(skinFolder, "assets"));
            Assert.NotEmpty(art);

            // ---- 4. 改一段界面文字 ----
            var row = workshop.UiTextEntries.Single(entry => entry.Key == "nav.home");
            row.Value = "我的首页";
            workshop.SaveUiTextsCommand.Execute(null);

            Assert.Equal("我的首页", stored.ReadTextOverrides(skinId)["nav.home"]);

            // ---- 5. 实时预览：改动立刻体现在当前皮肤上 ----
            var edited = first.Skins.Available.Single(skin => skin.Id == skinId);
            await first.Skins.ApplyAsync(edited);

            Assert.Equal("我的首页", UiTexts.Resolve(first.Skins.Active, "nav.home"));

            // ---- 6-8. 保存、关掉程序、重新启动 ----
            var second = await StartAsync(userData);

            // ---- 9/11. 皮肤还在，改动还在 ----
            var reopened = new WorkshopViewModel(second);
            var restored = reopened.Skins.Single(entry => entry.Id == skinId);
            Assert.Equal(SkinName, restored.Name);

            var reread = new UserSkinStore(second.Paths, second.Log.For("Skin"));
            Assert.Contains("icon_settings", reread.ReadOverrides(skinId).Keys);
            Assert.Contains(SkinAssets.Character, reread.ReadOverrides(skinId).Keys);
            Assert.Equal("我的首页", reread.ReadTextOverrides(skinId)["nav.home"]);

            // ---- 10. 启用 ----
            string? activated = null;
            reopened.ActivateSkinRequested += (_, id) => activated = id;
            reopened.ActivateSkinCommand.Execute(restored);

            Assert.Equal(skinId, activated);

            // The skin in force is the user's: it names its base, and its word is the user's word.
            var merged = second.Skins.Available.Single(skin => skin.Id == skinId);
            Assert.Equal(BaseId, merged.BaseSkin);
            Assert.Equal("我的首页", UiTexts.Resolve(merged, "nav.home"));

            // ---- 12. 官方 Eri Windows 没有被改动 ----
            Assert.Equal(shippedBefore, File.ReadAllText(ShippedSkinFile()));
            Assert.Equal(
                shippedFilesBefore,
                Directory.GetFiles(ShippedSkinFolder()).OrderBy(path => path).ToArray());
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_shipped_skin_refuses_to_be_edited_and_points_at_the_way_forward()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var host = await StartAsync(userData);
            var workshop = new WorkshopViewModel(host);

            var shipped = workshop.Skins.Single(entry => entry.Id == BaseId);
            workshop.EditSkinCommand.Execute(shipped);

            // Not an error: the honest answer, plus the step that unblocks it. And the editor stays
            // closed, because an editor that opened on a read-only skin would offer edits that
            // cannot be saved.
            Assert.False(workshop.IsEditingSkin);
            Assert.Contains("官方", workshop.SkinVerdict);
            Assert.Contains("新建皮肤", workshop.SkinVerdict);

            // Nothing editable is even offered for it, so the read-only claim is visible.
            Assert.False(workshop.SelectedSkinIsUserSkin);
            Assert.Empty(workshop.AssetSlots);
            Assert.Empty(workshop.UiTextEntries);
        }
        finally
        {
            Cleanup(userData);
        }
    }
}
