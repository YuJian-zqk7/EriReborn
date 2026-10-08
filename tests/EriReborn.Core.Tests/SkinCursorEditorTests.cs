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
/// The pointer is part of a skin, and the editor has to be able to write it.
///
/// <para>
/// A skin could already declare <c>cursors</c> and the resolver already honoured it — but nothing wrote the
/// declaration, so the one part of a skin the user meets on every single action was the one part the editor
/// could not touch (spec 112). These hold the two halves together: the editor offers the roles with values
/// the resolver knows, and saving writes a declaration the resolver then reads back.
/// </para>
/// </summary>
public sealed class SkinCursorEditorTests
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

    private static async Task<(AppHost Host, WorkshopViewModel Workshop, UserSkinStore Store, string SkinId)> EditingAsync()
    {
        var userData = NewUserData();
        var host = await StartAsync(userData);
        var workshop = new WorkshopViewModel(host);

        workshop.SelectedSkin = workshop.Skins.Single(entry => entry.Id == "eri_windows");
        workshop.BeginCreateSkinCommand.Execute(null);
        workshop.NewSkinName = "光标测试皮肤";
        workshop.CreateSkinCommand.Execute(null);

        var skinId = workshop.Skins.Single(entry => entry.Name == "光标测试皮肤").Id;
        return (host, workshop, new UserSkinStore(host.Paths, host.Log.For("Skin")), skinId);
    }

    [Fact]
    public async Task Every_pointer_role_is_offered_with_values_the_resolver_knows()
    {
        var (_, workshop, _, _) = await EditingAsync();

        Assert.Equal(CursorRoles.All.Count, workshop.CursorEntries.Count);
        Assert.Equal(
            CursorRoles.All.Select(CursorRoles.NameOf),
            workshop.CursorEntries.Select(row => row.RoleName));

        var busy = workshop.CursorEntries.Single(row => row.RoleName == CursorRoles.BusyName);

        // Empty means "follow the base skin"; the shapes come from the skin layer's one list; the pictures
        // are real sheet ids, so a saved value is never something the resolver would ignore.
        Assert.Contains(string.Empty, busy.Choices);
        foreach (var keyword in CursorRoles.KnownKeywords)
        {
            Assert.Contains(keyword, busy.Choices);
        }

        Assert.Contains(busy.Choices, choice => choice.StartsWith(CursorSpec.AssetPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saving_a_pointer_declaration_writes_it_and_the_skin_resolves_it()
    {
        var (host, workshop, store, skinId) = await EditingAsync();

        var row = workshop.CursorEntries.Single(entry => entry.RoleName == CursorRoles.BusyName);
        row.Spec = "wait";
        workshop.SaveAllCommand.Execute(null);

        Assert.Equal("wait", store.ReadCursors(skinId)[CursorRoles.BusyName]);

        var manifest = host.Skins.Available.Single(skin => skin.Id == skinId);
        var (declared, unknown) = SkinCursors.Resolve(manifest);

        Assert.Empty(unknown);
        Assert.Equal("wait", declared[CursorRole.Busy].Keyword);
    }

    [Fact]
    public async Task Clearing_a_pointer_removes_the_declaration_rather_than_writing_an_empty_one()
    {
        var (_, workshop, store, skinId) = await EditingAsync();

        workshop.CursorEntries.Single(entry => entry.RoleName == CursorRoles.LinkName).Spec = "hand";
        workshop.SaveAllCommand.Execute(null);
        Assert.True(store.ReadCursors(skinId).ContainsKey(CursorRoles.LinkName));

        // The rows are rebuilt from the file on save, so the row is looked up again rather than reused.
        workshop.CursorEntries.Single(entry => entry.RoleName == CursorRoles.LinkName).Spec = string.Empty;
        workshop.SaveAllCommand.Execute(null);

        // Empty is not "declare nothing as its value": it means follow the base skin, so the declaration has
        // to disappear — otherwise the role would be pinned to nothing and never fall back.
        Assert.False(store.ReadCursors(skinId).ContainsKey(CursorRoles.LinkName));
    }
}
