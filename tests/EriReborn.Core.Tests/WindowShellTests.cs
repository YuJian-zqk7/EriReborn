using System.Text.RegularExpressions;
using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The shell's custom title bar is drawn by the app, so every button on it is a
/// binding. Avalonia resolves bindings at run time: a name that does not exist
/// produces a dead button and no error, which is exactly how a minimise button
/// quietly stopped working once (spec 114).
/// </summary>
public sealed class WindowShellTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "EriReborn.UI.Avalonia")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private static string ShellXaml(string fileName)
        => File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "EriReborn.UI.Avalonia", "Views", fileName));

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

    // ------------------------------------------------------ binding integrity

    [Fact]
    public void Every_command_the_title_bar_binds_actually_exists()
    {
        var xaml = ShellXaml("MainWindow.axaml");

        var bound = Regex
            .Matches(xaml, @"\{Binding ([A-Za-z]+Command)\}")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(bound);

        var available = typeof(MainViewModel)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = bound.Where(name => !available.Contains(name)).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_property_the_title_bar_binds_exists_on_the_shell_view_model()
    {
        var xaml = ShellXaml("MainWindow.axaml");

        var bound = Regex
            .Matches(xaml, @"\{Binding ([A-Za-z][A-Za-z0-9]*)\}")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var available = typeof(MainViewModel)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = bound.Where(name => !available.Contains(name)).ToList();

        Assert.Empty(missing);
    }

    // ------------------------------------------------------- the maximise glyph

    [Fact]
    public async Task The_maximise_button_offers_restore_only_while_maximised()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var shell = new MainViewModel(host, new NavigationService());

            Assert.False(shell.WindowMaximized);
            Assert.Equal("▢", shell.MaximizeGlyph);
            Assert.Equal("最大化", shell.MaximizeTooltip);

            shell.WindowMaximized = true;

            // Showing "maximise" on a maximised window is the usual tell that the
            // title bar is not watching the real state.
            Assert.Equal("❐", shell.MaximizeGlyph);
            Assert.Equal("还原", shell.MaximizeTooltip);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_glyph_change_is_announced_so_the_button_updates()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var shell = new MainViewModel(host, new NavigationService());
            var changed = new List<string>();
            shell.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

            shell.WindowMaximized = true;

            Assert.Contains(nameof(MainViewModel.MaximizeGlyph), changed);
            Assert.Contains(nameof(MainViewModel.MaximizeTooltip), changed);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public void The_title_bar_handles_a_double_click_as_a_window_gesture()
    {
        // A title bar without double-click-to-maximise reads as broken; the
        // handler has to be attached for that to work at all.
        var xaml = ShellXaml("MainWindow.axaml");

        Assert.Contains("PointerPressed=\"OnTitleBarPressed\"", xaml);
    }
}
