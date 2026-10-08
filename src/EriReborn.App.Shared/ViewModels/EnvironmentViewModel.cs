using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.EnvironmentBuilder;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Engine.Software;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// The runtime / driver / security parts of the environment (spec 9).
///
/// This page used to list catalog metadata (category ids, subcategory names)
/// without detecting anything, so it said nothing about the actual machine.
/// It now runs the same detector as the rest of the product over each group and
/// reports what is really present.
/// </summary>
public sealed partial class EnvironmentViewModel : ViewModelBase, IPageActions
{
    /// <summary>One line under the page title in the shell's top bar.</summary>
    public string Subtitle => Summary;

    /// <inheritdoc />
    public IReadOnlyList<PageAction> GetPageActions() => new[]
    {
        new PageAction("扫描环境", ScanEnvironmentCommand, Primary: true),
    };

    private static readonly (string Title, string CategoryId)[] Groups =
    {
        ("运行时", "Runtime"),
        ("驱动", "System_Drivers"),
        ("安全", "System_Security"),
        ("系统基础", "System"),
    };

    private readonly AppHost _host;

    /// <summary>Last full-environment scan, kept so the planner can build a plan from it.</summary>
    private ScanResult? _lastScan;

    public EnvironmentViewModel(AppHost host)
    {
        _host = host;
        Title = "环境";

        foreach (var (title, categoryId) in Groups)
        {
            var definitions = host.Catalog.InCategory(categoryId).ToList();
            Sections.Add(new EnvironmentSection(title, categoryId, definitions));
        }

        SelectedSection = Sections[0];

        // The target environment the builder works toward (spec 146): the union of
        // the Software / Runtime / Drivers / Security catalog entries. Users form
        // this instead of installing one piece at a time.
        var envEntries = new List<SoftwareDefinition>();
        foreach (var category in new[] { "Software", "Runtime", "System_Drivers", "System_Security" })
        {
            envEntries.AddRange(host.Catalog.InCategory(category));
        }

        Target = EnvironmentDefinition.FromEntries("推荐运行环境", envEntries);
    }

    public ObservableCollection<EnvironmentSection> Sections { get; } = new();

    [ObservableProperty]
    private EnvironmentSection? _selectedSection;

    [ObservableProperty]
    private bool _scanRunning;

    /// <summary>The target environment the builder works toward (spec 146).</summary>
    public EnvironmentDefinition Target { get; private set; } = EnvironmentDefinition.FromEntries(string.Empty, Array.Empty<SoftwareDefinition>());

    /// <summary>Steps queued by the last build-plan generation.</summary>
    public ObservableCollection<EnvironmentPlanStep> Plan { get; } = new();

    [ObservableProperty]
    private string _planSummary = "尚未生成构建计划。点「生成构建计划」根据当前扫描结果产出待安装项。";

    [ObservableProperty]
    private bool _planRunning;

    /// <summary>True once at least one build step is queued.</summary>
    public bool HasPlan => Plan.Count > 0;

    /// <summary>Overall result across every group.</summary>
    [ObservableProperty]
    private string _summary = "尚未扫描环境。";

    /// <summary>Scans every group and reports one combined line.</summary>
    [RelayCommand]
    private async Task ScanEnvironmentAsync()
    {
        if (ScanRunning)
        {
            return;
        }

        ScanRunning = true;
        try
        {
            Summary = "正在扫描环境…";

            var all = Sections.SelectMany(s => s.Definitions).ToList();
            if (all.Count == 0)
            {
                Summary = "环境分类中没有可扫描的条目。";
                return;
            }

            var result = await _host.ScanService.ScanAsync(all, _host.Environment).ConfigureAwait(true);
            _lastScan = result;

            foreach (var section in Sections)
            {
                section.Apply(result);
            }

            Summary = result.Describe();
        }
        catch (Exception ex)
        {
            Summary = $"扫描失败：{ex.Message}";
        }
        finally
        {
            ScanRunning = false;
        }
    }

    /// <summary>Builds a one-shot plan from the target definition + the last scan (spec 146).</summary>
    [RelayCommand]
    private void GeneratePlan()
    {
        if (_lastScan is null)
        {
            PlanSummary = "请先点「扫描环境」，再生成构建计划。";
            return;
        }

        var plan = EnvironmentPlanner.Build(Target, _lastScan);
        Plan.Clear();
        foreach (var step in plan.Steps)
        {
            Plan.Add(step);
        }

        PlanSummary = plan.Total == 0
            ? "构建计划为空：当前环境已满足目标定义，无需安装。"
            : $"构建计划共 {plan.Total} 项待安装（软件 {plan.ByBucket("Software").Count} / 运行时 {plan.ByBucket("Runtime").Count} / 驱动 {plan.ByBucket("Drivers").Count} / 安全 {plan.ByBucket("Security").Count}）。点「执行计划」一次性构建。";
        OnPropertyChanged(nameof(HasPlan));
    }

    /// <summary>Installs every queued step through the existing pipeline (spec 146).</summary>
    [RelayCommand(CanExecute = nameof(CanRunPlan))]
    private async Task ExecutePlanAsync()
    {
        if (Plan.Count == 0 || PlanRunning)
        {
            return;
        }

        PlanRunning = true;
        try
        {
            var succeeded = 0;
            foreach (var step in Plan.ToList())
            {
                try
                {
                    var availability = await _host.SourceAvailability
                        .AssessAsync(step.Definition, CancellationToken.None).ConfigureAwait(true);
                    var chosen = SourceAvailabilityService.Pick(availability)
                        ?? throw new InvalidOperationException($"「{step.Name}」没有可用来源。");

                    var outcome = await _host.SoftwareEngine
                        .InstallAsync(step.Definition, _host.Environment, chosen.Source, null, CancellationToken.None)
                        .ConfigureAwait(true);

                    if (outcome.State is InstallState.Succeeded or InstallState.AlreadyInstalled)
                    {
                        Plan.Remove(step);
                        succeeded++;
                    }
                    else
                    {
                        PlanSummary = $"「{step.Name}」安装未成功：{outcome.State}。其余步骤继续。";
                    }
                }
                catch (Exception ex)
                {
                    PlanSummary = $"安装「{step.Name}」出错：{ex.Message}";
                }
            }

            PlanSummary = $"构建完成：成功 {succeeded} 项，剩余 {Plan.Count} 项。点「复检」重新扫描确认。";
        }
        finally
        {
            PlanRunning = false;
            OnPropertyChanged(nameof(HasPlan));
        }
    }

    private bool CanRunPlan() => Plan.Count > 0 && !PlanRunning;

    /// <summary>Re-scans the environment and regenerates the plan from the fresh result.</summary>
    [RelayCommand]
    private async Task RecheckAsync()
    {
        await ScanEnvironmentAsync().ConfigureAwait(true);
        GeneratePlan();
    }
}

/// <summary>One detectable group (runtime, drivers, security, system basics).</summary>
public sealed partial class EnvironmentSection : ObservableObject
{
    public EnvironmentSection(string title, string categoryId, IReadOnlyList<SoftwareDefinition> definitions)
    {
        Title = title;
        CategoryId = categoryId;
        Definitions = definitions;
    }

    public string Title { get; }

    public string CategoryId { get; }

    /// <summary>
    /// The section's icon. Keyed on the catalog category rather than the title, so a
    /// translated title cannot silently lose the art.
    /// </summary>
    public string IconId => CategoryId switch
    {
        "Runtime" => "icon_runtime",
        "System_Drivers" => "icon_driver",
        "System_Security" => "icon_security",
        _ => "icon_info",
    };

    public IReadOnlyList<SoftwareDefinition> Definitions { get; }

    public int Total => Definitions.Count;

    public ObservableCollection<EnvironmentItem> Results { get; } = new();

    [ObservableProperty]
    private int _installed;

    [ObservableProperty]
    private int _missing;

    [ObservableProperty]
    private int _unsupported;

    [ObservableProperty]
    private int _unknown;

    [ObservableProperty]
    private string _summary = "尚未扫描。";

    public void Apply(ScanResult result)
    {
        Results.Clear();

        foreach (var definition in Definitions)
        {
            if (!result.Results.TryGetValue(definition.Id, out var detection))
            {
                continue;
            }

            Results.Add(new EnvironmentItem(
                definition.Name,
                detection.ToStatus(),
                detection.Detail ?? detection.Source ?? string.Empty));
        }

        // One scan covers every group, so each section reports its own slice.
        Installed = Results.Count(r => r.Status == SoftwareStatus.Installed);
        Missing = Results.Count(r => r.Status == SoftwareStatus.Missing);
        Unsupported = Results.Count(r => r.Status == SoftwareStatus.Unsupported);
        Unknown = Results.Count(r => r.Status == SoftwareStatus.Unknown);

        // An empty group is a catalog coverage gap, not a scan failure, and it
        // is reported as such: the shipped catalog has no driver or security
        // entries at all.
        Summary = Results.Count == 0
            ? $"{Title}：软件目录中还没有这一类的条目（目录覆盖不足，并非扫描失败）。"
            : $"{Title}：已安装 {Installed}，未安装 {Missing}，不支持 {Unsupported}，未知 {Unknown}。";
    }
}

public sealed record EnvironmentItem(string Name, SoftwareStatus Status, string Detail)
{
    /// <summary>The row's state icon, from the shared state set.</summary>
    public string StatusIconId => Status switch
    {
        SoftwareStatus.Installed => "icon_success",
        SoftwareStatus.Missing => "icon_warning",
        SoftwareStatus.Broken or SoftwareStatus.Failed => "icon_error",
        SoftwareStatus.Unsupported => "icon_unsupported",
        _ => "icon_unknown",
    };

    public string StatusText => Status switch
    {
        SoftwareStatus.Installed => "已安装",
        SoftwareStatus.Missing => "未安装",
        SoftwareStatus.Unsupported => "不支持",
        SoftwareStatus.Unknown => "未知",
        SoftwareStatus.Broken => "异常",
        SoftwareStatus.Failed => "失败",
        _ => Status.ToString(),
    };
}
