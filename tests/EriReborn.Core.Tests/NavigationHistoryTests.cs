using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A back trail, so "back" is the same gesture on every platform.
///
/// <para>
/// On Android the back gesture is the only way back at all, and exiting from a
/// page the user just opened reads as a crash. The trail is kept here rather than
/// in the Android activity so it can be tested without a device, and so the
/// desktop can offer the same thing.
/// </para>
/// </summary>
public sealed class NavigationHistoryTests
{
    [Fact]
    public void A_fresh_start_has_nowhere_to_go_back_to()
    {
        var navigation = new NavigationService();

        Assert.Equal("home", navigation.CurrentKey);
        Assert.False(navigation.CanGoBack);
        Assert.False(navigation.TryGoBack());
    }

    [Fact]
    public void Navigation_leaves_a_trail()
    {
        var navigation = new NavigationService();
        var visited = new List<string>();
        navigation.Navigated += (_, key) => visited.Add(key);

        navigation.Navigate("software");
        navigation.Navigate("cloud");

        Assert.Equal("cloud", navigation.CurrentKey);
        Assert.True(navigation.CanGoBack);
        Assert.Equal(2, navigation.HistoryDepth);
        Assert.Equal(new[] { "software", "cloud" }, visited);
    }

    [Fact]
    public void Back_walks_the_trail_in_reverse()
    {
        var navigation = new NavigationService();
        var visited = new List<string>();
        navigation.Navigated += (_, key) => visited.Add(key);

        navigation.Navigate("software");
        navigation.Navigate("cloud");

        Assert.True(navigation.TryGoBack());
        Assert.Equal("software", navigation.CurrentKey);

        Assert.True(navigation.TryGoBack());
        Assert.Equal("home", navigation.CurrentKey);

        // The trail is spent, and saying so is what lets Android leave the app.
        Assert.False(navigation.CanGoBack);
        Assert.False(navigation.TryGoBack());

        Assert.Equal(new[] { "software", "cloud", "software", "home" }, visited);
    }

    [Fact]
    public void Asking_for_the_page_you_are_on_changes_nothing()
    {
        var navigation = new NavigationService();
        navigation.Navigate("software");

        var before = navigation.HistoryDepth;
        navigation.Navigate("software");

        // Otherwise pressing the same sidebar item twice would need two backs.
        Assert.Equal(before, navigation.HistoryDepth);
    }

    [Fact]
    public void The_trail_is_bounded()
    {
        var navigation = new NavigationService();

        for (var index = 0; index < NavigationService.MaxHistory + 20; index++)
        {
            navigation.Navigate($"page-{index}");
        }

        // The back gesture can be pressed indefinitely; an unbounded trail would
        // grow for as long as the app stays open.
        Assert.Equal(NavigationService.MaxHistory, navigation.HistoryDepth);
    }

    [Fact]
    public void The_bound_drops_the_oldest_pages_not_the_newest()
    {
        // The off-by-one here was caught by exactly one assertion before. What a
        // bounded trail must still do is walk back through the pages the user was
        // just on, not through the oldest ones they have long left.
        var navigation = new NavigationService();

        for (var index = 0; index < NavigationService.MaxHistory + 9; index++)
        {
            navigation.Navigate($"page-{index}");
        }

        Assert.Equal(NavigationService.MaxHistory, navigation.HistoryDepth);
        Assert.Contains("page-30", navigation.History);
        Assert.DoesNotContain("home", navigation.History);

        // Every retained step is walkable, and then the trail is genuinely spent.
        for (var step = 0; step < NavigationService.MaxHistory; step++)
        {
            Assert.True(navigation.TryGoBack(), $"第 {step + 1} 步应该还能回退。");
        }

        Assert.False(navigation.TryGoBack());
    }

    [Fact]
    public void The_bound_holds_while_navigating_not_only_at_the_end()
    {
        var navigation = new NavigationService();

        for (var index = 0; index < NavigationService.MaxHistory * 3; index++)
        {
            navigation.Navigate($"page-{index}");

            // An unbounded trail would only be noticeable after the fact; this checks
            // it never exceeds the limit at any point.
            Assert.True(
                navigation.HistoryDepth <= NavigationService.MaxHistory,
                $"第 {index} 次导航后深度为 {navigation.HistoryDepth}。");
        }
    }

    [Fact]
    public void Resetting_starts_a_fresh_trail()
    {
        var navigation = new NavigationService();
        navigation.Navigate("software");
        navigation.Navigate("cloud");

        navigation.Reset("modern");

        Assert.Equal("modern", navigation.CurrentKey);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public void Resetting_to_the_current_page_is_not_a_navigation()
    {
        var navigation = new NavigationService();
        navigation.Navigate("software");

        var navigated = 0;
        navigation.Navigated += (_, _) => navigated++;

        navigation.Reset("software");

        Assert.Equal(0, navigated);
    }

    // ------------------------------------------------------ the shell's view

    private static async Task<(AppHost Host, string UserData)> CreateHostAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        var host = await AppHost.CreateAsync(
            paths,
            new TestPlatform(
                new TestFileSystemService(userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore()),
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

        return (host, userData);
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
        finally
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task The_shell_goes_back_and_the_page_follows()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.TutorialProgress.MarkCompleted();
            var navigation = new NavigationService();
            var shell = new MainViewModel(host, navigation);

            Assert.IsType<HomeViewModel>(shell.CurrentPage);

            navigation.Navigate("software");
            Assert.IsType<SoftwareViewModel>(shell.CurrentPage);

            Assert.True(shell.TryGoBack());
            Assert.IsType<HomeViewModel>(shell.CurrentPage);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_shell_reports_when_there_is_nowhere_to_go()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.TutorialProgress.MarkCompleted();
            var shell = new MainViewModel(host, new NavigationService());

            // False is the signal Android uses to leave the app.
            Assert.False(shell.TryGoBack());
            Assert.IsType<HomeViewModel>(shell.CurrentPage);
        }
        finally
        {
            Cleanup(data);
        }
    }
}
