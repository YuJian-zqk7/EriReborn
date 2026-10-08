using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Which skin is in force, and who is allowed to change it.
///
/// <para>
/// Two defects lived here, and the second magnified the first. Creating a skin also made it the skin in
/// force, so the interface changed before the user had decided anything. And a user skin built on one of
/// the user's own skins was dropped when the list was rebuilt, because the bases were looked up among the
/// shipped packs only; the picker then answered "the skin in force is not here" by falling back to the
/// first pack it had. Together: create a skin, save it, and the whole interface was suddenly some other
/// skin.
/// </para>
/// </summary>
public sealed class SkinSwitchingTests
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

    private static string NewUserData()
        => Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Creates a skin on the given base and returns its id.</summary>
    private static string Create(WorkshopViewModel workshop, WorkshopSkinEntry baseSkin, string name)
    {
        workshop.SelectedSkin = baseSkin;
        workshop.BeginCreateSkinCommand.Execute(null);
        workshop.NewSkinName = name;
        workshop.CreateSkinCommand.Execute(null);

        return workshop.Skins.Single(entry => entry.Name == name).Id;
    }

    [Fact]
    public async Task Creating_a_skin_opens_it_for_editing_without_changing_the_interface()
    {
        var host = await StartAsync(NewUserData());
        var workshop = new WorkshopViewModel(host);

        var activated = new List<string>();
        workshop.ActivateSkinRequested += (_, id) => activated.Add(id);

        var id = Create(workshop, workshop.Skins.Single(entry => entry.Id == "eri_windows"), "我的皮肤");

        // Made, and open in the editor — but the interface is untouched until the user says so.
        Assert.Empty(activated);
        Assert.True(workshop.IsEditingSkin);
        Assert.False(workshop.IsCreatingSkin);
        Assert.Equal(id, workshop.SelectedSkin?.Id);
        Assert.Contains("启用", workshop.SkinVerdict, StringComparison.Ordinal);

        // Switching stays the 启用 button's job.
        workshop.ActivateSkinCommand.Execute(workshop.SelectedSkin);
        Assert.Equal(new[] { id }, activated);
    }

    [Fact]
    public async Task A_skin_built_on_the_users_own_skin_is_resolved_too()
    {
        var host = await StartAsync(NewUserData());
        var workshop = new WorkshopViewModel(host);

        var first = Create(workshop, workshop.Skins.Single(entry => entry.Id == "eri_windows"), "底稿皮肤");
        var second = Create(workshop, workshop.Skins.Single(entry => entry.Id == first), "基于底稿的皮肤");

        // Both are layers over the shipped packs, and the second one's base is the first one — which the
        // rebuild has to resolve before the second can exist at all.
        var available = host.ReloadUserSkins();

        Assert.Contains(available, manifest => manifest.Id == first);
        Assert.Contains(available, manifest => manifest.Id == second);
        Assert.Contains(host.Skins.Available, manifest => manifest.Id == second);
        Assert.Equal(first, host.Skins.Available.Single(manifest => manifest.Id == second).BaseSkin);
    }

    [Fact]
    public async Task The_skin_in_force_survives_a_reload_that_no_longer_lists_it()
    {
        var userData = NewUserData();
        var host = await StartAsync(userData);
        var shell = new MainViewModel(host, new NavigationService());

        // The shell's own workshop: it is the one whose requests the shell listens to. A second
        // WorkshopViewModel over the same host would look right and change nothing.
        var workshop = shell.Workshop;

        var id = Create(workshop, workshop.Skins.Single(entry => entry.Id == "eri_windows"), "要删掉的皮肤");
        workshop.ActivateSkinCommand.Execute(workshop.SelectedSkin);
        Assert.Equal(id, shell.ActiveSkin?.Id);

        // The skin goes away underneath the shell — a deletion, or a rebuild that could not resolve it.
        Assert.True(new UserSkinStore(host.Paths, host.Log.For("Skin")).Delete(id));
        shell.ReloadSkins();

        // Keeping what the user chose is the honest answer; picking the first pack instead is what made
        // 保存 look like it switched the interface by itself.
        Assert.Equal(id, shell.ActiveSkin?.Id);
    }

    /// <summary>
    /// What a two-way picker does when its list is rebuilt, simulated exactly: it is bound to the list,
    /// so it reacts to the change while the rebuild is running and writes its own answering selection
    /// back into the view model. The null it writes is "I have nothing selected", and it used to be the
    /// last word — the id being edited was read too late to survive it, and the fallback picked the
    /// first skin.
    /// </summary>
    [Fact]
    public async Task The_editor_keeps_its_skin_when_the_picker_answers_a_rebuild_with_null()
    {
        var host = await StartAsync(NewUserData());
        var workshop = new WorkshopViewModel(host);

        var id = Create(workshop, workshop.Skins.Single(entry => entry.Id == "eri_windows"), "换图的皮肤");

        // A picture replacement is the reported flow: 更改图片, and the editor must still be on the same
        // skin when it finishes.
        workshop.SelectElement("icon_settings", null);
        Assert.True(workshop.SelectedElement?.IsArt);

        var answered = false;
        workshop.Skins.CollectionChanged += (_, _) =>
        {
            answered = true;
            workshop.SelectedSkin = null;
        };

        workshop.ReplaceElementArt(Path.Combine(RepoRoot(), "assets", "library", "workshop", "ws_persona_editor.png"));

        Assert.True(answered, "皮肤列表这次没有被重建，测试没有模拟到真实情况。");
        Assert.Equal(id, workshop.SelectedSkin?.Id);
        Assert.True(workshop.SelectedSkinIsUserSkin);
    }

    [Fact]
    public async Task The_shell_keeps_its_skin_when_the_picker_answers_a_rebuild_with_null()
    {
        var userData = NewUserData();
        var host = await StartAsync(userData);
        var shell = new MainViewModel(host, new NavigationService());
        var workshop = shell.Workshop;

        var id = Create(workshop, workshop.Skins.Single(entry => entry.Id == "eri_windows"), "外壳要守住的皮肤");
        workshop.ActivateSkinCommand.Execute(workshop.SelectedSkin);
        Assert.Equal(id, shell.ActiveSkin?.Id);

        // The title bar's picker is two-way too, and it answers a rebuild the same way.
        var answered = false;
        shell.Skins.CollectionChanged += (_, _) =>
        {
            answered = true;
            shell.ActiveSkin = null;
        };

        shell.ReloadSkins();

        Assert.True(answered, "皮肤列表这次没有被重建，测试没有模拟到真实情况。");
        Assert.Equal(id, shell.ActiveSkin?.Id);
    }
}
