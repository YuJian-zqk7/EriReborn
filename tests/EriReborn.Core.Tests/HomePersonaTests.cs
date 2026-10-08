using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Persona;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The overview speaks through the state machine. Facts and wording are separate
/// fields, so changing voice can never change a fact (spec 4/103).
/// </summary>
public sealed class HomePersonaTests
{
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

    private static async Task<(AppHost Host, string UserData)> CreateHostWithoutMemoryAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        var host = await AppHost.CreateAsync(
            paths,
            new TestPlatform(
                new TestFileSystemService(userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore())
            {
                SystemInfo = new TestPlatform.UnreadableSystemInfo(),
            },
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
    public async Task Before_a_scan_the_fact_and_the_voice_are_separate_fields()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.Personas.Activate("Eri");
            var home = new HomeViewModel(host);

            Assert.Equal("尚未扫描软件。", home.ScanSummary);

            var expected = host.Personas.Say(PersonaState.Waiting, "尚未扫描软件。").Voice;
            Assert.Equal(expected, home.ScanCommentary);

            // The fact did not absorb any of the wording.
            Assert.DoesNotContain(expected, home.ScanSummary);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_table_value_shows_its_fact_first_and_its_comment_after()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.Personas.Activate("Eri");
            var home = new HomeViewModel(host);

            var voice = host.Personas.Say(PersonaState.Success, "已全量通过校验").Voice;

            Assert.StartsWith("已全量通过校验", home.CatalogHealth, StringComparison.Ordinal);
            Assert.Contains(voice, home.CatalogHealth);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task An_unreadable_value_is_never_spoken_of_as_missing()
    {
        var (host, data) = await CreateHostWithoutMemoryAsync();
        try
        {
            host.Personas.Activate("Eri");
            var home = new HomeViewModel(host);

            // A reading that came back zero is unknown, not absent.
            Assert.StartsWith("未知", home.MemoryText, StringComparison.Ordinal);

            foreach (var forbidden in new[] { "没装", "未安装", "缺失" })
            {
                Assert.DoesNotContain(forbidden, home.MemoryText);
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Changing_persona_changes_the_voice_and_leaves_every_fact_alone()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.Personas.Activate("Eri");
            var home = new HomeViewModel(host);

            var eriSummary = home.ScanSummary;
            var eriCatalogFact = home.CatalogHealth;
            var eriVoice = home.ScanCommentary;

            host.Personas.Activate("Tech");
            home.Refresh();

            // The wording moved; the facts did not.
            Assert.NotEqual(eriVoice, home.ScanCommentary);
            Assert.Equal(eriSummary, home.ScanSummary);
            Assert.StartsWith("已全量通过校验", home.CatalogHealth, StringComparison.Ordinal);
            Assert.NotEqual(eriCatalogFact, home.CatalogHealth);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_greeting_is_not_a_state_and_keeps_its_own_key()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.Personas.Activate("Tech");
            var home = new HomeViewModel(host);

            Assert.Equal("环境概览已就绪。", home.PersonaGreeting);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task An_unknown_active_persona_still_leaves_the_facts_readable()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            // A skin naming a persona that is not installed must not blank the page.
            host.Personas.Activate("does-not-exist");
            var home = new HomeViewModel(host);

            Assert.Equal("尚未扫描软件。", home.ScanSummary);
            Assert.StartsWith("已全量通过校验", home.CatalogHealth, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(data);
        }
    }
}
