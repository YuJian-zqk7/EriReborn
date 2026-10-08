using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Batch installing is planned first and executed one item at a time. A batch
/// that runs as fast as the loop allows is indistinguishable, from the other
/// side, from a burst (spec 37/146).
/// </summary>
public sealed class SoftwareBatchTests
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

    /// <summary>造一条插件资源定义：列表只显示插件条目（SoftwareViewModel.ApplyFilter），批量计划的数据得从这里来。</summary>
    private static SoftwareDefinition PluginResource(string id, string name) => new()
    {
        Id = id,
        Name = name,
        CategoryId = "Utility",
        DirectoryName = id,
        CatalogId = "batch-test",
        IsPluginProvided = true,
        Sources = new[]
        {
            new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/" + id + ".exe" },
        },
    };

    private static SoftwareDefinition[] ThreePluginResources() => new[]
    {
        PluginResource("bt-a", "批量甲"),
        PluginResource("bt-b", "批量乙"),
        PluginResource("bt-c", "批量丙"),
    };

    [Fact]
    public async Task The_plan_holds_only_missing_items_that_can_actually_be_installed()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.PluginResources.Add(ThreePluginResources());
            var software = new SoftwareViewModel(host);

            var installable = software.Items.Where(item => item.Definition.Sources.Count > 0).Take(3).ToList();
            Assert.NotEmpty(installable);

            foreach (var item in installable)
            {
                item.Status = SoftwareStatus.Missing;
            }

            // Missing but with nowhere to download from: not installable, so not planned.
            var unreachable = software.Items.FirstOrDefault(item => item.Definition.Sources.Count == 0);
            if (unreachable is not null)
            {
                unreachable.Status = SoftwareStatus.Missing;
            }

            software.PrepareBatchInstallCommand.Execute(null);

            Assert.Equal(installable.Count, software.BatchPlan.Count);
            Assert.True(software.HasBatchPlan);
            Assert.Contains($"将依次安装 {installable.Count} 项", software.BatchSummary);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Confirming_reports_each_item_instead_of_a_single_verdict()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.PluginResources.Add(ThreePluginResources());
            var software = new SoftwareViewModel(host);

            var targets = software.Items.Where(item => item.Definition.Sources.Count > 0).Take(2).ToList();
            foreach (var item in targets)
            {
                item.Status = SoftwareStatus.Missing;
            }

            software.PrepareBatchInstallCommand.Execute(null);
            Assert.Equal(2, software.BatchPlan.Count);

            await software.ConfirmBatchInstallCommand.ExecuteAsync(null);

            // The test platform cannot install anything, so this exercises the
            // plumbing without touching the machine.
            Assert.Contains("批量安装完成", software.BatchSummary);
            Assert.Contains("共 2 项", software.BatchSummary);
            Assert.False(software.HasBatchPlan);
            Assert.False(software.BatchRunning);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Cancelling_a_plan_leaves_nothing_to_execute()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.PluginResources.Add(ThreePluginResources());
            var software = new SoftwareViewModel(host);
            var item = software.Items.First(candidate => candidate.Definition.Sources.Count > 0);
            item.Status = SoftwareStatus.Missing;

            software.PrepareBatchInstallCommand.Execute(null);
            Assert.True(software.HasBatchPlan);

            software.CancelBatchInstallCommand.Execute(null);

            Assert.False(software.HasBatchPlan);
            Assert.Empty(software.BatchPlan);
            Assert.Contains("已取消", software.BatchSummary);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task An_empty_plan_says_so_rather_than_offering_nothing()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var software = new SoftwareViewModel(host);

            // Nothing has been detected as missing yet.
            software.PrepareBatchInstallCommand.Execute(null);

            Assert.False(software.HasBatchPlan);
            Assert.Contains("没有", software.BatchSummary);
        }
        finally
        {
            Cleanup(data);
        }
    }
}
