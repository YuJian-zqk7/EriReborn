using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The settings page's contact entries, and the rule that decides what a link even is.
///
/// <para>
/// Two things are worth pinning here. Every entry the page shows must be an address that can really be
/// opened — the list is built from data, so a half-filled entry would render as a button that does
/// nothing, which is worse than no button. And a link only counts as a link when it is http/https:
/// the string handed to the operating system is opened with whatever this machine associates with it,
/// so the refusal has to happen before that call, not after.
/// </para>
/// </summary>
public sealed class SettingsSupportTests
{
    /// <summary>A shell that records what it was asked to open, so a test can see the real address.</summary>
    private sealed class RecordingShell : IShellService
    {
        public string? LastUrl { get; private set; }

        /// <summary>What the platform reports back; null means it worked.</summary>
        public string? Result { get; set; }

        public string? OpenFolder(string path) => null;

        public string? OpenUrl(string url)
        {
            LastUrl = url;
            return Result;
        }
    }

    private static async Task<AppHost> CreateHostAsync(string userData, IShellService shell)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "assets", "skins")))
        {
            directory = directory.Parent;
        }

        var assets = Path.Combine(directory!.FullName, "assets");

        var platform = new TestPlatform(
            new TestFileSystemService(userData),
            new TestNetworkService(new HttpClient()),
            new InMemoryCredentialStore())
        {
            Shell = shell,
        };

        return await AppHost.CreateAsync(
            AppPaths.Detect(assetsOverride: assets, userDataOverride: userData),
            platform,
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));
    }

    private static string NewUserData()
        => Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Every_entry_the_page_offers_is_an_address_that_can_be_opened()
    {
        var userData = NewUserData();

        try
        {
            var settings = new SettingsViewModel(await CreateHostAsync(userData, new RecordingShell()));

            // A list built from data can be half-filled without anyone noticing: the button still
            // renders. A label without a usable address is exactly that case.
            Assert.NotEmpty(settings.SupportLinks);
            Assert.All(settings.SupportLinks, link =>
            {
                Assert.False(string.IsNullOrWhiteSpace(link.Label));
                Assert.False(string.IsNullOrWhiteSpace(link.Detail));
                Assert.True(WebLinks.TryNormalise(link.Url, out _, out var refusal), refusal);
            });
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Opening_an_entry_hands_its_own_address_to_the_platform()
    {
        var userData = NewUserData();

        try
        {
            var shell = new RecordingShell();
            var settings = new SettingsViewModel(await CreateHostAsync(userData, shell));
            var link = settings.SupportLinks.First();

            settings.OpenSupportLinkCommand.Execute(link);

            Assert.Equal(link.Url, shell.LastUrl);
            Assert.Contains(link.Label, settings.LinkStatus);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_platform_that_cannot_open_a_link_says_so_and_keeps_the_address()
    {
        var userData = NewUserData();

        try
        {
            var shell = new RecordingShell { Result = "这个平台没有浏览器。" };
            var settings = new SettingsViewModel(await CreateHostAsync(userData, shell));

            settings.OpenSupportLinkCommand.Execute(settings.SupportLinks.First());

            // Reported, never silent — and the address is still on the page, because on a machine with
            // no browser copying it by hand is the only thing that still works.
            Assert.Contains("失败", settings.LinkStatus);
            Assert.Contains("这个平台没有浏览器。", settings.LinkStatus);
            Assert.NotEmpty(settings.SupportLinks);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void Only_http_and_https_addresses_are_accepted()
    {
        Assert.True(WebLinks.TryNormalise("https://space.bilibili.com/689572905", out var https, out _));
        Assert.Equal("https://space.bilibili.com/689572905", https);

        Assert.True(WebLinks.TryNormalise("  http://example.test/a  ", out var http, out _));
        Assert.Equal("http://example.test/a", http);

        // Anything else would be handed to the shell, which runs whatever this machine associates with
        // the scheme. None of these are web links, so none of them are opened.
        Assert.False(WebLinks.TryNormalise("file:///C:/Windows/System32/calc.exe", out _, out var schemeRefusal));
        Assert.Contains("http/https", schemeRefusal);

        Assert.False(WebLinks.TryNormalise("javascript:alert(1)", out _, out _));
        Assert.False(WebLinks.TryNormalise("cmd.exe", out _, out _));

        // A bare host name is not an absolute address, so it is refused rather than guessed at.
        Assert.False(WebLinks.TryNormalise("space.bilibili.com/689572905", out _, out _));

        Assert.False(WebLinks.TryNormalise(null, out _, out var emptyRefusal));
        Assert.Equal("链接为空。", emptyRefusal);

        Assert.False(WebLinks.TryNormalise("   ", out _, out _));
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
            // Best effort: a leftover temp directory must not fail a test.
        }
    }
}
