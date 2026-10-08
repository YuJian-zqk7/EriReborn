using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// What a layout link does when it is clicked.
///
/// <para>
/// A node may carry a <c>link</c>, and until now that bought it a hand cursor and a tooltip and nothing
/// else: the property panel accepted the address, the document stored it, the pointer promised a jump,
/// and clicking did nothing. The jump goes through the shell's own navigation, so this is the half that
/// can be checked without a screen — the renderer's click wiring is what calls into it.
/// </para>
/// </summary>
public sealed class LayoutLinkTests
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

    [Fact]
    public async Task A_link_goes_to_the_page_it_names()
    {
        var host = await StartAsync(NewUserData());
        var shell = new MainViewModel(host, new NavigationService());

        // The bare key, which is what the property panel shows the user.
        Assert.True(shell.NavigateTo("software"));
        Assert.Same(shell.Software, shell.CurrentPage);

        // And the dotted forms, because these are typed by hand and the tables they come from are not
        // something the person writing a link should have to know.
        Assert.True(shell.NavigateTo("page.settings"));
        Assert.Same(shell.Settings, shell.CurrentPage);

        Assert.True(shell.NavigateTo("nav.software"));
        Assert.Same(shell.Software, shell.CurrentPage);
    }

    [Fact]
    public async Task A_link_to_no_page_moves_nothing()
    {
        var host = await StartAsync(NewUserData());
        var shell = new MainViewModel(host, new NavigationService());

        Assert.True(shell.NavigateTo("software"));
        var page = shell.CurrentPage;

        // A wrong word in a link is not a navigation to somewhere else: the page stays where it is, and
        // the caller is told so it can treat the link as something other than a page (an address).
        Assert.False(shell.NavigateTo("没有这个页面"));
        Assert.Same(page, shell.CurrentPage);
    }
}
