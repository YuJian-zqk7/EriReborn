using System.Globalization;
using System.Text;

namespace EriReborn.Engine.Ai;

/// <summary>
/// What the application already knows about this machine, in the plainest possible
/// shape.
///
/// <para>
/// Deliberately flat strings rather than the domain types: building the digest is
/// then testable without a machine, and the digest cannot accidentally carry a
/// field nobody meant to publish.
/// </para>
/// </summary>
public sealed record AiEnvironmentFacts
{
    public string? OperatingSystem { get; init; }

    public string? Architecture { get; init; }

    public string? Processor { get; init; }

    public long? MemoryBytes { get; init; }

    public long? FreeDiskBytes { get; init; }

    /// <summary>The display adapters the machine reports, by the driver's own name.</summary>
    public IReadOnlyList<string> Graphics { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The mounted volumes that could be written to, already worded for a reader
    /// ("C:\ fixed，共 476 GB，可用 132 GB"). Worded here rather than in the digest so the two cannot
    /// disagree about what a volume is called.
    /// </summary>
    public IReadOnlyList<string> Storage { get; init; } = Array.Empty<string>();

    /// <summary>Software the scan found, as "name version".</summary>
    public IReadOnlyList<string> Installed { get; init; } = Array.Empty<string>();

    /// <summary>Catalog entries the scan looked for and did not find.</summary>
    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Facts the platform could not report at all. Carried explicitly because
    /// "we could not read this" and "this is not present" are different answers,
    /// and an analysis that confuses them is worse than no analysis.
    /// </summary>
    public IReadOnlyList<string> Unavailable { get; init; } = Array.Empty<string>();

    /// <summary>Entries the scan could not decide about, as "name (reason)".</summary>
    public IReadOnlyList<string> Unknown { get; init; } = Array.Empty<string>();
}

public sealed record AiDigestLimits
{
    public static readonly AiDigestLimits Default = new();

    /// <summary>Per list. A prompt with hundreds of entries costs more and reads worse.</summary>
    public int MaxPerList { get; init; } = 40;
}

/// <summary>The prompt text plus what had to be left out, so nothing is silently dropped.</summary>
public sealed record AiEnvironmentDigest(string Text, int FactCount, int OmittedCount)
{
    public bool IsTruncated => OmittedCount > 0;
}

/// <summary>
/// Turns the machine's facts into a bounded text summary for an AI endpoint.
///
/// <para>
/// <b>No identifying detail goes in.</b> No user name, no machine name, no install
/// paths, no serial numbers: the analysis needs to know what is installed and what
/// the machine is, not who owns it.
/// </para>
///
/// <para>
/// Nothing is silently dropped. Lists are capped, and both the count left out and
/// the facts the platform could not report are stated in the text, because an
/// analysis built on a truncated list it does not know about is confidently wrong.
/// </para>
/// </summary>
public static class AiEnvironmentDigestBuilder
{
    public static AiEnvironmentDigest Build(AiEnvironmentFacts facts, AiDigestLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var max = Math.Max(1, (limits ?? AiDigestLimits.Default).MaxPerList);
        var text = new StringBuilder();
        var factCount = 0;
        var omitted = 0;

        text.AppendLine("以下是一台电脑的环境事实。请据此分析，并在最后给出可执行的建议。");
        text.AppendLine();

        factCount += Line(text, "操作系统", facts.OperatingSystem, ref omitted);
        factCount += Line(text, "架构", facts.Architecture, ref omitted);
        factCount += Line(text, "处理器", facts.Processor, ref omitted);
        factCount += Line(text, "内存", FormatBytes(facts.MemoryBytes), ref omitted);
        factCount += Line(text, "可用磁盘", FormatBytes(facts.FreeDiskBytes), ref omitted);

        // Both lists rather than lines: a machine can have an integrated and a discrete adapter, and
        // more volumes than fit in one sentence. Empty lists print nothing, so a caller that has no
        // answer does not leave a heading with nothing under it.
        omitted += Section(text, "显卡", facts.Graphics, max);
        omitted += Section(text, "存储卷", facts.Storage, max);

        omitted += Section(text, "已安装", facts.Installed, max);
        omitted += Section(text, "目录中未检测到", facts.Missing, max);

        if (facts.Unknown.Count > 0)
        {
            omitted += Section(text, "无法判定", facts.Unknown, max);
        }

        if (facts.Unavailable.Count > 0)
        {
            // Spelled out rather than left blank: a missing line reads as "fine",
            // and the analysis would then reason about a machine that is not this one.
            text.AppendLine();
            text.AppendLine("以下事实本机无法读取（不代表不存在）：");
            foreach (var item in facts.Unavailable.Take(max))
            {
                text.AppendLine("- " + item);
            }

            omitted += Math.Max(0, facts.Unavailable.Count - max);
        }

        if (omitted > 0)
        {
            text.AppendLine();
            text.AppendLine($"（为控制长度，以上列表省略了 {omitted} 项；结论请据此保留余地。）");
        }

        factCount += facts.Installed.Count + facts.Missing.Count + facts.Unknown.Count + facts.Unavailable.Count
            + facts.Graphics.Count + facts.Storage.Count;

        return new AiEnvironmentDigest(text.ToString().TrimEnd(), factCount, omitted);
    }

    private static int Line(StringBuilder text, string label, string? value, ref int omitted)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        text.AppendLine($"{label}：{value}");
        return 1;
    }

    private static int Section(StringBuilder text, string title, IReadOnlyList<string> items, int max)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        text.AppendLine();
        text.AppendLine($"{title}（{items.Count} 项）：");

        foreach (var item in items.Take(max))
        {
            text.AppendLine("- " + item);
        }

        return Math.Max(0, items.Count - max);
    }

    /// <summary>
    /// Human units, because "17179869184" tells a reader nothing. Public because the facts are worded
    /// where they are collected, and both places have to say "476 GB" the same way.
    /// </summary>
    public static string? FormatBytes(long? bytes)
    {
        if (bytes is not { } value || value <= 0)
        {
            return null;
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var size = (double)value;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return size.ToString(unit >= 3 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
