using EriReborn.Core.Domain;
using EriReborn.Engine.Software;

namespace EriReborn.App.Shared.EnvironmentBuilder;

/// <summary>
/// Turns a target EnvironmentDefinition + a current ScanResult into a one-shot
/// build plan (spec 146): every entry whose status is not already satisfied
/// becomes an install step. Unknown / no-detector outcomes are queued too, so the
/// machine ends up in a known state instead of staying ambiguous.
/// </summary>
public static class EnvironmentPlanner
{
    public static EnvironmentPlan Build(EnvironmentDefinition definition, ScanResult scan)
    {
        var steps = new List<EnvironmentPlanStep>(definition.Entries.Count);
        foreach (var entry in definition.Entries)
        {
            var status = StatusOf(scan, entry.Id);
            if (NeedsInstall(status))
            {
                steps.Add(new EnvironmentPlanStep(entry, status, ReasonFor(status)));
            }
        }

        return new EnvironmentPlan(definition, steps, DateTimeOffset.UtcNow);
    }

    private static SoftwareStatus StatusOf(ScanResult scan, string id)
        => scan.Results.TryGetValue(id, out var detection)
            ? detection.ToStatus()
            : SoftwareStatus.Unknown;

    private static bool NeedsInstall(SoftwareStatus status) => status is
        SoftwareStatus.Missing or
        SoftwareStatus.Broken or
        SoftwareStatus.Failed or
        SoftwareStatus.Unknown;

    private static string ReasonFor(SoftwareStatus status) => status switch
    {
        SoftwareStatus.Missing => "未安装",
        SoftwareStatus.Broken => "已损坏",
        SoftwareStatus.Failed => "上次安装失败",
        SoftwareStatus.Unknown => "状态未知，纳入构建",
        _ => "需要安装",
    };
}
