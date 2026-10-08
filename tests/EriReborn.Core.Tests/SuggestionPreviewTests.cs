using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Software;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Detection suggestions used to be applied the moment they were computed, so
/// the user saw a count and never the matches. Containment matches are the
/// weakest evidence and the ones most worth inspecting, so every suggestion must
/// be shown and applied individually (spec 22).
/// </summary>
public sealed class SuggestionPreviewTests
{
    private sealed class InstalledSource : ISoftwareDetector, IInstalledSoftwareSource
    {
        public IReadOnlyList<InstalledSoftwareInfo> Installed { get; set; } = Array.Empty<InstalledSoftwareInfo>();

        public string SourceDescription => "测试用已安装程序清单";

        public Task<IReadOnlyList<InstalledSoftwareInfo>> ListInstalledAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Installed);

        public Task<DetectionResult> DetectAsync(SoftwareDefinition software, CancellationToken cancellationToken = default)
            => Task.FromResult(DetectionResult.Unknown("测试平台不执行检测。"));
    }

    private static async Task<(SoftwareViewModel ViewModel, AppHost Host, InstalledSource Source, string UserData)> BuildAsync()
    {
        var data = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: data);
        var source = new InstalledSource();

        var platform = new TestPlatform(
            new TestFileSystemService(data),
            new TestNetworkService(new HttpClient()),
            new InMemoryCredentialStore())
        {
            Detector = source,
        };

        var host = await AppHost.CreateAsync(
            paths,
            platform,
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

        return (new SoftwareViewModel(host), host, source, data);
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

    /// <summary>Two installed programs chosen to produce the two confidence levels.</summary>
    private static (string ExactId, string ContainmentId) PrepareInstalled(SoftwareViewModel viewModel, AppHost host, InstalledSource source)
    {
        var undecided = host.Catalog.Software
            .Where(s => s.Detector.IsNone && s.Name.Length >= 4)
            .OrderByDescending(s => s.Name.Length)
            .ToList();

        var exact = undecided[0];
        var containment = undecided.First(s => s.Id != exact.Id);

        source.Installed = new[]
        {
            new InstalledSoftwareInfo(exact.Name, "1.0"),
            new InstalledSoftwareInfo(containment.Name + "XtraTool", "2.0"),
        };

        return (exact.Id, containment.Id);
    }

    [Fact]
    public async Task Suggestions_are_shown_without_writing_anything()
    {
        var (viewModel, host, source, data) = await BuildAsync();
        try
        {
            PrepareInstalled(viewModel, host, source);

            Assert.Empty(viewModel.Suggestions);
            Assert.Equal(0, host.DetectionHints.Count);

            await viewModel.SuggestDetectionsCommand.ExecuteAsync(null);

            Assert.NotEmpty(viewModel.Suggestions);

            // The whole point: computing suggestions must not change detection.
            Assert.Equal(0, host.DetectionHints.Count);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_weakest_matches_are_listed_but_not_pre_selected()
    {
        var (viewModel, host, source, data) = await BuildAsync();
        try
        {
            PrepareInstalled(viewModel, host, source);
            await viewModel.SuggestDetectionsCommand.ExecuteAsync(null);

            var weak = viewModel.Suggestions.Where(s => s.Confidence == SuggestionConfidence.Containment).ToList();

            Assert.All(weak, s => Assert.False(s.IsSelected, $"{s.SoftwareName} is a containment match and must not be pre-selected"));

            // Stronger evidence is pre-selected, so the default action is useful.
            Assert.All(
                viewModel.Suggestions.Where(s => s.Confidence != SuggestionConfidence.Containment),
                s => Assert.True(s.IsSelected));
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Only_the_ticked_suggestions_are_applied()
    {
        var (viewModel, host, source, data) = await BuildAsync();
        try
        {
            PrepareInstalled(viewModel, host, source);
            await viewModel.SuggestDetectionsCommand.ExecuteAsync(null);

            var selected = viewModel.Suggestions.Where(s => s.IsSelected).ToList();
            var skipped = viewModel.Suggestions.Where(s => !s.IsSelected).ToList();

            await viewModel.ApplySelectedSuggestionsCommand.ExecuteAsync(null);

            Assert.Equal(selected.Count, host.DetectionHints.Count);

            foreach (var suggestion in selected)
            {
                Assert.NotNull(host.DetectionHints.Get(suggestion.Suggestion.SoftwareId));
            }

            foreach (var suggestion in skipped)
            {
                Assert.Null(host.DetectionHints.Get(suggestion.Suggestion.SoftwareId));
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Ticking_everything_applies_everything()
    {
        var (viewModel, host, source, data) = await BuildAsync();
        try
        {
            PrepareInstalled(viewModel, host, source);
            await viewModel.SuggestDetectionsCommand.ExecuteAsync(null);

            viewModel.SelectAllSuggestionsCommand.Execute(null);
            var all = viewModel.Suggestions.Count;

            await viewModel.ApplySelectedSuggestionsCommand.ExecuteAsync(null);

            Assert.Equal(all, host.DetectionHints.Count);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Applying_nothing_writes_nothing()
    {
        var (viewModel, host, source, data) = await BuildAsync();
        try
        {
            PrepareInstalled(viewModel, host, source);
            await viewModel.SuggestDetectionsCommand.ExecuteAsync(null);

            viewModel.SelectNoSuggestionsCommand.Execute(null);
            await viewModel.ApplySelectedSuggestionsCommand.ExecuteAsync(null);

            Assert.Equal(0, host.DetectionHints.Count);
            Assert.Contains("没有勾选", viewModel.SuggestionSummary);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_confidence_filter_hides_weaker_matches()
    {
        var (viewModel, host, source, data) = await BuildAsync();
        try
        {
            PrepareInstalled(viewModel, host, source);
            await viewModel.SuggestDetectionsCommand.ExecuteAsync(null);

            var all = viewModel.Suggestions.Count;

            // "仅精确匹配" is the strictest option.
            viewModel.SelectedSuggestionFilter = viewModel.SuggestionFilters[^1];

            Assert.All(viewModel.Suggestions, s => Assert.Equal(SuggestionConfidence.Exact, s.Confidence));
            Assert.True(viewModel.Suggestions.Count <= all);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_platform_that_cannot_enumerate_installed_software_says_so()
    {
        var (viewModel, _, _, data) = await BuildAsync();
        try
        {
            // No source registered at all.
            var plain = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
            var paths = AppPaths.Detect(userDataOverride: plain);
            var platform = new TestPlatform(
                new TestFileSystemService(plain),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore());

            var host = await AppHost.CreateAsync(
                paths,
                platform,
                new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
                AppLog.For("Test"));

            var withoutSource = new SoftwareViewModel(host);
            await withoutSource.SuggestDetectionsCommand.ExecuteAsync(null);

            Assert.Empty(withoutSource.Suggestions);
            Assert.Contains("不支持", withoutSource.SuggestionSummary);

            try
            {
                Directory.Delete(plain, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
        finally
        {
            Cleanup(data);
        }
    }
}
