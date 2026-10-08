using System;
using System.Collections.Generic;
using EriReborn.App.Shared.EnvironmentBuilder;
using EriReborn.Core.Domain;
using EriReborn.Engine.Software;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>Tests for the spec 146 software environment builder planner.</summary>
public sealed class EnvironmentPlannerTests
{
    private static SoftwareDefinition Def(string id, string category) => new()
    {
        Id = id,
        Name = id,
        CategoryId = category,
        DirectoryName = id,
    };

    private static ScanResult Scan(params (string Id, DetectionOutcome Outcome)[] rows)
    {
        var results = new Dictionary<string, DetectionResult>(StringComparer.Ordinal);
        foreach (var (id, outcome) in rows)
        {
            results[id] = new DetectionResult(outcome);
        }

        return new ScanResult(
            Total: results.Count, Installed: 0, Missing: 0, Unsupported: 0, Unknown: 0, Failed: 0,
            WithoutDetector: 0, Duration: TimeSpan.Zero, Results: results);
    }

    [Fact]
    public void Build_queues_missing_failed_unknown_and_skips_installed_unsupported()
    {
        var def = EnvironmentDefinition.FromEntries("t", new[]
        {
            Def("a", "Software"),
            Def("b", "Software"),
            Def("c", "Runtime"),
            Def("d", "System_Drivers"),
            Def("e", "System_Security"),
        });

        var scan = Scan(
            ("a", DetectionOutcome.Detected),
            ("b", DetectionOutcome.NotDetected),
            ("c", DetectionOutcome.Unsupported),
            ("d", DetectionOutcome.Error),
            ("e", DetectionOutcome.Unknown));

        var plan = EnvironmentPlanner.Build(def, scan);

        Assert.Equal(3, plan.Total);
        Assert.Contains(plan.Steps, s => s.Name == "b" && s.CurrentStatus == SoftwareStatus.Missing);
        Assert.Contains(plan.Steps, s => s.Name == "d" && s.CurrentStatus == SoftwareStatus.Failed);
        Assert.Contains(plan.Steps, s => s.Name == "e" && s.CurrentStatus == SoftwareStatus.Unknown);
        Assert.DoesNotContain(plan.Steps, s => s.Name == "a");
        Assert.DoesNotContain(plan.Steps, s => s.Name == "c");
    }

    [Fact]
    public void Build_groups_entries_into_the_four_environment_buckets()
    {
        var def = EnvironmentDefinition.FromEntries("t", new[]
        {
            Def("s1", "Software"),
            Def("r1", "Runtime"),
            Def("dr1", "System_Drivers"),
            Def("sec1", "System_Security"),
        });

        var scan = Scan(
            ("s1", DetectionOutcome.NotDetected),
            ("r1", DetectionOutcome.NotDetected),
            ("dr1", DetectionOutcome.NotDetected),
            ("sec1", DetectionOutcome.NotDetected));

        var plan = EnvironmentPlanner.Build(def, scan);

        Assert.Equal(4, plan.Total);
        Assert.Equal("Software", plan.Steps.Single(s => s.Name == "s1").Bucket);
        Assert.Equal("Runtime", plan.Steps.Single(s => s.Name == "r1").Bucket);
        Assert.Equal("Drivers", plan.Steps.Single(s => s.Name == "dr1").Bucket);
        Assert.Equal("Security", plan.Steps.Single(s => s.Name == "sec1").Bucket);
    }

    [Fact]
    public void Build_queues_entries_absent_from_the_scan_as_unknown()
    {
        var def = EnvironmentDefinition.FromEntries("t", new[] { Def("x", "Software") });
        var scan = Scan(Array.Empty<(string, DetectionOutcome)>());

        var plan = EnvironmentPlanner.Build(def, scan);

        Assert.Equal(1, plan.Total);
        Assert.Equal(SoftwareStatus.Unknown, plan.Steps[0].CurrentStatus);
    }

    [Fact]
    public void Build_reasons_describe_each_status()
    {
        var def = EnvironmentDefinition.FromEntries("t", new[]
        {
            Def("b", "Software"),
            Def("d", "Runtime"),
            Def("e", "System_Security"),
        });

        var scan = Scan(
            ("b", DetectionOutcome.NotDetected),
            ("d", DetectionOutcome.Error),
            ("e", DetectionOutcome.Unknown));

        var plan = EnvironmentPlanner.Build(def, scan);

        Assert.Equal("未安装", plan.Steps.Single(s => s.Name == "b").Reason);
        Assert.Equal("上次安装失败", plan.Steps.Single(s => s.Name == "d").Reason);
        Assert.Equal("状态未知，纳入构建", plan.Steps.Single(s => s.Name == "e").Reason);
    }
}
