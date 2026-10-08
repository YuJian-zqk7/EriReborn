using System.Text.RegularExpressions;
using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.Tutorial;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The tutorial ships with the app, states the facts the product depends on
/// being understood, and remembers what has been seen (spec 7-18).
/// </summary>
public sealed class TutorialTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "tutorial")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

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

    // ------------------------------------------------------------- content

    [Fact]
    public async Task The_host_loads_the_bundled_tutorial_from_the_shipped_assets()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            // This is the same path the application uses, so a wrong sub-directory
            // shows up here rather than only in a published build.
            Assert.NotEmpty(host.Tutorial.Onboarding);
            Assert.NotEmpty(host.Tutorial.Topics);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public void A_missing_tutorial_file_is_reported_rather_than_thrown()
    {
        var empty = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);

        try
        {
            var catalog = TutorialCatalogReader.Load(empty, AppLog.For("Test"));

            Assert.Empty(catalog.Onboarding);
            Assert.Empty(catalog.Topics);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task The_reference_tree_covers_the_documented_areas()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var ids = host.Tutorial.AllTopics().Select(topic => topic.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var required in new[]
                     {
                         "what-is-eri", "quick-start", "home", "software", "environment", "cloud",
                         "plugin", "extension", "plugin-vs-extension", "download-accel", "ai",
                         "skin", "persona", "workshop", "marketplace", "settings", "troubleshooting",
                     })
            {
                Assert.Contains(required, ids);
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Troubleshooting_answers_the_questions_the_patch_lists()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var ids = host.Tutorial.AllTopics().Select(topic => topic.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var required in new[]
                     {
                         "ts-download-failed", "ts-install-failed", "ts-unknown-vs-missing",
                         "ts-resume", "ts-sha", "ts-slow", "ts-login", "ts-provider-unavailable",
                         "ts-extension", "ts-skin", "ts-ai", "ts-not-tested",
                     })
            {
                Assert.Contains(required, ids);
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_cloud_topic_names_all_five_platforms_without_ranking_them()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var cloud = host.Tutorial.Find("cloud");
            Assert.NotNull(cloud);

            var text = string.Join("\n", cloud!.Body);

            foreach (var platform in new[] { "123", "百度", "夸克", "蓝奏", "迅雷" })
            {
                Assert.Contains(platform, text);
            }

            Assert.Contains("平级", text);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task No_part_of_the_tutorial_invents_a_primary_platform()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            // The rule is architectural, so the documentation must not contradict it.
            var everything = string.Join(
                "\n",
                host.Tutorial.AllTopics().SelectMany(topic => new[] { topic.Title }.Concat(topic.Body)));

            var offenders = new List<string>();

            foreach (var forbidden in new[] { "主网盘", "次网盘", "默认网盘", "首选网盘" })
            {
                foreach (Match match in Regex.Matches(everything, forbidden))
                {
                    // "没有主网盘" states the rule rather than breaking it, so a
                    // negated mention is not a violation.
                    var prefix = everything[Math.Max(0, match.Index - 8)..match.Index];
                    if (prefix.Contains("没有") || prefix.Contains("不分") || prefix.Contains("不存在"))
                    {
                        continue;
                    }

                    offenders.Add($"{forbidden}（前文：…{prefix}）");
                }
            }

            Assert.Empty(offenders);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_plugin_topic_states_that_a_plugin_is_data()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var plugin = host.Tutorial.Find("plugin");
            Assert.NotNull(plugin);

            var text = string.Join("\n", plugin!.Body);
            Assert.Contains("数据", text);
            Assert.Contains("不会执行", text);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_two_ecosystems_are_explained_as_different_things()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var comparison = host.Tutorial.Find("plugin-vs-extension");
            Assert.NotNull(comparison);

            var text = string.Join("\n", comparison!.Body);
            Assert.Contains("资源", text);
            Assert.Contains("能力", text);

            // The chain, in order, is the part people get wrong.
            Assert.Contains("Resolver", text);
            Assert.Contains("Download Engine", text);
        }
        finally
        {
            Cleanup(data);
        }
    }

    // ------------------------------------------------- first-run behaviour

    [Fact]
    public async Task The_walkthrough_is_offered_until_it_is_finished()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var tutorial = new TutorialViewModel(host);
            Assert.True(tutorial.ShowOnboarding);
            Assert.True(tutorial.StepCount > 0);
            Assert.False(tutorial.CanGoBack);
            Assert.Contains("第 1 /", tutorial.StepCounter);

            // Walking to the end completes it.
            while (tutorial.CanGoForward)
            {
                tutorial.NextCommand.Execute(null);
            }

            tutorial.NextCommand.Execute(null);
            Assert.False(tutorial.ShowOnboarding);

            // A later session must not be asked again.
            var reopened = new TutorialViewModel(host);
            Assert.False(reopened.ShowOnboarding);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Skipping_finishes_the_walkthrough_too()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var tutorial = new TutorialViewModel(host);

            tutorial.SkipCommand.Execute(null);

            Assert.False(tutorial.ShowOnboarding);
            Assert.True(host.TutorialProgress.Current.OnboardingCompleted);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_walkthrough_can_be_seen_again_on_request()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var tutorial = new TutorialViewModel(host);
            tutorial.SkipCommand.Execute(null);
            Assert.False(tutorial.ShowOnboarding);

            tutorial.RestartOnboardingCommand.Execute(null);

            Assert.True(tutorial.ShowOnboarding);
            Assert.Equal(0, tutorial.StepIndex);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Back_and_forward_move_through_the_steps()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var tutorial = new TutorialViewModel(host);
            var first = tutorial.StepTitle;

            tutorial.NextCommand.Execute(null);
            Assert.True(tutorial.CanGoBack);
            Assert.NotEqual(first, tutorial.StepTitle);

            tutorial.BackCommand.Execute(null);
            Assert.Equal(first, tutorial.StepTitle);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Opening_a_topic_shows_it_and_records_that_it_was_seen()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var tutorial = new TutorialViewModel(host);
            var item = tutorial.Topics.First(topic => topic.Id == "cloud");

            tutorial.OpenTopicCommand.Execute(item);

            Assert.Equal("Cloud Provider（网盘平台）", tutorial.SelectedTitle);
            Assert.Contains("平级", tutorial.SelectedBody);
            Assert.Contains("cloud", host.TutorialProgress.Current.SeenTopics);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_tree_is_shown_with_indentation_for_nested_topics()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var tutorial = new TutorialViewModel(host);

            var parent = tutorial.Topics.First(topic => topic.Id == "software");
            var child = tutorial.Topics.First(topic => topic.Id == "software-install");

            Assert.Equal(0, parent.Depth);
            Assert.Equal(1, child.Depth);
            Assert.Equal(string.Empty, parent.Indent);
        }
        finally
        {
            Cleanup(data);
        }
    }

    // ------------------------------------------------------ first run

    [Fact]
    public async Task The_first_run_opens_the_walkthrough_without_being_asked()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var shell = new MainViewModel(host, new NavigationService());

            // Making the user find the tutorial is not a first-run experience. The walkthrough
            // is now a dialog over the shell rather than a page the user has to navigate to.
            Assert.True(shell.PromptOpen);
            Assert.Same(shell.Tutorial.NextCommand, shell.PromptPrimaryAction);
            Assert.Same(shell.Tutorial.SkipCommand, shell.PromptTertiaryAction);
            Assert.Equal(shell.Tutorial.StepCounter, shell.PromptStep);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_later_run_starts_on_the_overview()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.TutorialProgress.MarkCompleted();

            var shell = new MainViewModel(host, new NavigationService());

            Assert.IsType<HomeViewModel>(shell.CurrentPage);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Finishing_the_walkthrough_moves_to_the_overview()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var shell = new MainViewModel(host, new NavigationService());
            Assert.True(shell.PromptOpen);

            shell.Tutorial.SkipCommand.Execute(null);

            // A finished wizard is not somewhere to leave the user standing, and the dialog it
            // lived in closes with it: the step line is what only the walkthrough sets.
            Assert.IsType<HomeViewModel>(shell.CurrentPage);
            Assert.True(host.TutorialProgress.Current.OnboardingCompleted);
            Assert.Equal(string.Empty, shell.PromptStep);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task An_explicit_start_page_is_never_hijacked_by_the_wizard()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var previous = Environment.GetEnvironmentVariable("ERIREBORN_START_PAGE");
            Environment.SetEnvironmentVariable("ERIREBORN_START_PAGE", "software");

            try
            {
                var shell = new MainViewModel(host, new NavigationService());

                // A smoke run or a developer asked for this page; the walkthrough waits.
                Assert.IsType<SoftwareViewModel>(shell.CurrentPage);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ERIREBORN_START_PAGE", previous);
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_damaged_progress_file_shows_the_tutorial_again_rather_than_hiding_it()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var path = Path.Combine(data, "tutorial-progress.json");
            Directory.CreateDirectory(data);
            await File.WriteAllTextAsync(path, "{ not json at all");

            var store = new TutorialProgressStore(path, AppLog.For("Test"));
            store.Load();

            // Erring towards showing it again is the safe direction.
            Assert.False(store.Current.OnboardingCompleted);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Progress_survives_a_restart()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var path = Path.Combine(data, "tutorial-progress.json");
            var store = new TutorialProgressStore(path, AppLog.For("Test"));

            // Opening a topic records where the user last was, so the last call
            // wins; the completion is what marks the walkthrough as done.
            store.MarkSeen("plugin");
            store.MarkCompleted("cloud");

            var reloaded = new TutorialProgressStore(path, AppLog.For("Test"));
            reloaded.Load();

            Assert.True(reloaded.Current.OnboardingCompleted);
            Assert.Equal("cloud", reloaded.Current.LastTopicId);
            Assert.Contains("plugin", reloaded.Current.SeenTopics);
        }
        finally
        {
            Cleanup(data);
        }
    }
}
