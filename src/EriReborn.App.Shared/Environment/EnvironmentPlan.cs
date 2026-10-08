using EriReborn.Core.Domain;

namespace EriReborn.App.Shared.EnvironmentBuilder;

/// <summary>One install action inside a build plan.</summary>
public sealed record EnvironmentPlanStep(
    SoftwareDefinition Definition,
    SoftwareStatus CurrentStatus,
    string Reason)
{
    public string Bucket => EnvironmentDefinition.BucketFor(Definition.CategoryId);
    public string Name => Definition.Name;
}

/// <summary>A one-shot build plan: every entry not yet satisfied (spec 146).</summary>
public sealed record EnvironmentPlan(
    EnvironmentDefinition Definition,
    IReadOnlyList<EnvironmentPlanStep> Steps,
    DateTimeOffset GeneratedAt)
{
    public int Total => Steps.Count;

    public IReadOnlyList<EnvironmentPlanStep> ByBucket(string bucket) =>
        Steps.Where(s => s.Bucket == bucket).ToList();
}
