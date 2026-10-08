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
/// The skin editor has to be able to select ordinary interface elements — a plain label, a button,
/// a border — rather than only the two controls that used to announce their own names, and it has
/// to say what it selected instead of silently doing nothing.
/// </summary>
public sealed class SkinElementEditorTests
{
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

    private static Task<AppHost> StartAsync(string userData)
        => AppHost.CreateAsync(
            AppPaths.Detect(assetsOverride: Path.Combine(RepoRoot(), "assets"), userDataOverride: userData),
            new TestPlatform(
                new TestFileSystemService(userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore()),
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

    /// <summary>An editor open on a user skin, which is where every edit actually happens.</summary>
    private static async Task<(AppHost Host, WorkshopViewModel Workshop, UserSkinStore Store, string SkinId, string UserData)> EditingAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var host = await StartAsync(userData);
        var workshop = new WorkshopViewModel(host);

        workshop.SelectedSkin = workshop.Skins.Single(entry => entry.Id == "eri_windows");
        workshop.BeginCreateSkinCommand.Execute(null);
        workshop.NewSkinName = "元素测试皮肤";
        workshop.CreateSkinCommand.Execute(null);

        var skinId = workshop.Skins.Single(entry => entry.Name == "元素测试皮肤").Id;
        return (host, workshop, new UserSkinStore(host.Paths, host.Log.For("Skin")), skinId, userData);
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

    // ------------------------------------------------------------------ the catalog

    [Fact]
    public void The_catalog_fills_in_the_kind_and_the_human_name()
    {
        var element = SkinElementCatalog.Resolve("home.character");

        Assert.Equal(SkinElementKind.Image, element.Kind);
        Assert.True(element.IsArt);
        Assert.False(string.IsNullOrWhiteSpace(element.DisplayName));
        Assert.DoesNotContain("home.character", element.DisplayName);
    }

    [Fact]
    public void A_container_only_arranges_things_and_says_so()
    {
        var element = SkinElementCatalog.Resolve("software.search");

        Assert.True(element.IsLayout);
        Assert.False(element.IsText);
        Assert.False(element.IsArt);
    }

    [Fact]
    public void A_text_slot_carries_the_key_its_word_is_stored_under()
    {
        Assert.Equal("nav.settings", SkinElementCatalog.Resolve("nav.settings").TextKey);
        Assert.Equal("common.download", SkinElementCatalog.Resolve("cloud.download_button").TextKey);
        Assert.True(SkinElementCatalog.Resolve("cloud.download_button").IsText);
    }

    [Fact]
    public void An_unknown_id_still_works_and_keeps_what_the_view_declared()
    {
        var element = SkinElementCatalog.Resolve(
            "some.page.thing",
            SkinElementKind.Icon,
            "页面上的小图标",
            assetId: "icon_info");

        Assert.Equal(SkinElementKind.Icon, element.Kind);
        Assert.Equal("页面上的小图标", element.DisplayName);
        Assert.Equal("icon_info", element.AssetId);
    }

    // --------------------------------------------------------------- the selection

    [Fact]
    public async Task Clicking_a_word_selects_that_word_and_opens_the_text_tool()
    {
        var (_, workshop, _, _, userData) = await EditingAsync();
        try
        {
            workshop.SelectElement(SkinElementCatalog.Resolve("nav.settings"));

            Assert.Equal("文本", workshop.PaletteMode);
            Assert.Equal("nav.settings", workshop.SelectedElement?.TextKey);
            Assert.False(string.IsNullOrWhiteSpace(workshop.SelectedElement?.Title));
            Assert.Equal("文字", workshop.SelectedElement?.TypeText);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Clicking_a_picture_selects_the_picture_it_draws()
    {
        var (_, workshop, _, _, userData) = await EditingAsync();
        try
        {
            workshop.SelectElement(new SkinElementDescriptor(
                "nav.settings.icon",
                SkinElementKind.Icon,
                "设置图标",
                AssetId: "icon_settings"));

            Assert.Equal("图标", workshop.PaletteMode);
            Assert.True(workshop.SelectedElement?.IsArt);
            Assert.Equal("icon_settings", workshop.SelectedElement?.AssetId);
            Assert.Equal("设置图标", workshop.SelectedElement?.Title);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Clicking_a_coloured_surface_points_at_the_colour_behind_it()
    {
        var (_, workshop, _, _, userData) = await EditingAsync();
        try
        {
            workshop.SelectElement(SkinElementCatalog.Resolve("home.character_card"));

            Assert.Equal("颜色", workshop.PaletteMode);
            Assert.Equal("surface", workshop.SelectedElement?.ColorKey);
            Assert.True(workshop.SelectedElement?.IsColor);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Clicking_a_container_explains_itself_instead_of_doing_nothing()
    {
        var (_, workshop, _, _, userData) = await EditingAsync();
        try
        {
            workshop.SelectElement(SkinElementCatalog.Resolve("software.search"));

            Assert.Null(workshop.SelectedElement);
            Assert.Contains("布局", workshop.SelectionHint);
            Assert.Contains("布局", workshop.SkinVerdict);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Clearing_the_selection_tells_the_user_what_to_do()
    {
        var (_, workshop, _, _, userData) = await EditingAsync();
        try
        {
            workshop.SelectElement(SkinElementCatalog.Resolve("nav.home"));
            workshop.ClearSelection();

            Assert.Null(workshop.SelectedElement);
            Assert.Contains("点", workshop.SelectionHint);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task An_element_with_nothing_to_change_says_so()
    {
        var (_, workshop, _, _, userData) = await EditingAsync();
        try
        {
            // A button whose word is not in the text table: there is genuinely nothing to edit.
            workshop.SelectElement(new SkinElementDescriptor("odd.button", SkinElementKind.Button, "没有文案的按钮"));

            Assert.Null(workshop.SelectedElement);
            Assert.Contains("没有可改的内容", workshop.SelectionHint);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    // ------------------------------------------------------------ the save status

    [Fact]
    public async Task Saving_a_word_reports_that_it_was_saved()
    {
        var (_, workshop, store, skinId, userData) = await EditingAsync();
        try
        {
            Assert.Equal("未修改", workshop.SaveState);

            var row = workshop.UiTextEntries.Single(entry => entry.Key == "nav.home");
            row.Value = "我的首页";
            workshop.SaveUiTextsCommand.Execute(null);

            Assert.Equal("已保存", workshop.SaveState);
            Assert.Equal("我的首页", store.ReadTextOverrides(skinId)["nav.home"]);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    // --------------------------------------------------- one save button, one status

    [Fact]
    public async Task One_save_button_writes_words_and_colours_together()
    {
        var (_, workshop, store, skinId, userData) = await EditingAsync();
        try
        {
            var word = workshop.UiTextEntries.Single(entry => entry.Key == "nav.home");
            word.Value = "我的首页";

            var colour = workshop.ColorEntries.First();
            var colourKey = colour.Key;
            colour.Value = "#123456";

            workshop.SaveAllCommand.Execute(null);

            Assert.Equal("已保存", workshop.SaveState);
            Assert.Equal("我的首页", store.ReadTextOverrides(skinId)["nav.home"]);
            Assert.Equal("#123456", store.ReadColorOverrides(skinId)[colourKey]);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Saving_with_nothing_changed_says_so_instead_of_reporting_writes()
    {
        var (_, workshop, store, skinId, userData) = await EditingAsync();
        try
        {
            // Every row starts blank, which means "keep the base's wording". A blank row is not an
            // edit, so 保存 must not claim to have written anything.
            workshop.SaveAllCommand.Execute(null);

            Assert.Equal("未修改", workshop.SaveState);
            Assert.Empty(store.ReadTextOverrides(skinId));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_persona_line_can_be_changed_without_touching_the_interface_words()
    {
        var (_, workshop, store, skinId, userData) = await EditingAsync();
        try
        {
            var line = workshop.PersonaTextEntries.First();
            line.Value = "哼～这种小事交给 Eri 就好！";

            workshop.SaveAllCommand.Execute(null);

            var texts = store.ReadTextOverrides(skinId);

            // The persona's lines and the interface's words share one file but not one key space: a
            // change to a line must not show up as a change to a label, and vice versa.
            Assert.Equal("哼～这种小事交给 Eri 就好！", texts[line.Key]);
            Assert.All(
                workshop.UiTextEntries.Where(entry => !entry.IsOverridden),
                entry => Assert.DoesNotContain(entry.Key, texts.Keys));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    // ------------------------------------------------- the preview's own sidebar

    [Fact]
    public async Task Every_navigation_entry_can_be_edited_as_a_word()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var host = await StartAsync(userData);
            var shell = new MainViewModel(host, new NavigationService());

            Assert.NotEmpty(shell.NavigationItems);
            Assert.All(shell.NavigationItems, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.TextKey));
                Assert.Contains("nav.", item.SkinTextElementId, StringComparison.Ordinal);
                Assert.Contains("nav.", item.SkinIconElementId, StringComparison.Ordinal);
            });
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void The_panel_names_the_file_the_picture_really_is()
    {
        var picked = new WorkshopPreviewElement(
            "图片（Image）", "logo", "logo", string.Empty, "logo", "logo", @"C:\somewhere\logo.png", false, null);

        Assert.Equal("logo.png", picked.CurrentFileName);

        var shipped = picked with { CurrentFile = null };
        Assert.Null(shipped.CurrentFileName);
    }

    // ----------------------------------------------------- the skin survives a restart

    [Fact]
    public async Task The_skin_that_was_switched_on_is_the_one_the_next_start_uses()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = await StartAsync(userData);
            var workshop = new WorkshopViewModel(first);
            workshop.SelectedSkin = workshop.Skins.Single(entry => entry.Id == "eri_windows");
            workshop.BeginCreateSkinCommand.Execute(null);
            workshop.NewSkinName = "重启后还该是我";
            workshop.CreateSkinCommand.Execute(null);

            var skinId = workshop.Skins.Single(entry => entry.Name == "重启后还该是我").Id;

            // What the shell does when a skin is switched on: remember it, so the next start opens
            // with the user's own skin rather than the platform default.
            first.UserConfig.SetActiveSkin(skinId);

            var second = await StartAsync(userData);

            Assert.Equal(skinId, second.Skins.Active?.Id);
        }
        finally
        {
            Cleanup(userData);
        }
    }
}
