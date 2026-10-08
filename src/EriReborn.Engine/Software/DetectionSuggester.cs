using System.Text;
using System.Text.RegularExpressions;
using EriReborn.Core.Domain;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Engine.Software;

/// <summary>How closely a catalog name matched an installed program.</summary>
public enum SuggestionConfidence
{
    /// <summary>Names are identical apart from case.</summary>
    Exact,

    /// <summary>Names are identical once punctuation and spacing are removed.</summary>
    Normalized,

    /// <summary>One normalised name contains the other.</summary>
    Containment,
}

/// <summary>
/// A proposed detection override: "this catalog entry is that installed
/// program". Suggestions are derived from real evidence and always require the
/// user's confirmation before they are stored.
/// </summary>
public sealed record DetectionSuggestion(
    string SoftwareId,
    string SoftwareName,
    string InstalledName,
    string? Version,
    string ArpPattern,
    SuggestionConfidence Confidence,
    string Reason)
{
    public DetectionHint ToHint() => new() { ArpPattern = ArpPattern };
}

/// <summary>
/// Proposes detection overrides for catalog entries that declare none, by
/// matching them against what is actually installed (spec 22).
///
/// A loose matcher would be worse than no matcher, so containment requires a
/// minimum length and ties are resolved deterministically.
/// </summary>
public static class DetectionSuggester
{
    /// <summary>Shortest normalised name allowed to take part in containment.</summary>
    public const int MinimumContainmentLength = 4;

    /// <summary>Shortest catalog name allowed to become a prefix pattern.</summary>
    public const int MinimumPrefixLength = 3;

    public static IReadOnlyList<DetectionSuggestion> Suggest(
        IReadOnlyCollection<SoftwareDefinition> candidates,
        IReadOnlyCollection<InstalledSoftwareInfo> installed)
    {
        var pool = installed
            .Select(info => (Info: info, Normalized: Normalize(info.Name)))
            .Where(entry => entry.Normalized.Length > 0)
            .OrderBy(entry => entry.Info.Name, StringComparer.Ordinal)
            .ToList();

        var suggestions = new List<DetectionSuggestion>();

        foreach (var software in candidates.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            InstalledSoftwareInfo? best = null;
            var bestConfidence = SuggestionConfidence.Containment;

            foreach (var entry in pool)
            {
                var confidence = Classify(software.Name, entry.Info.Name, entry.Normalized);
                if (confidence is null)
                {
                    continue;
                }

                if (best is null || IsBetter(confidence.Value, entry.Info.Name, bestConfidence, best.Name))
                {
                    best = entry.Info;
                    bestConfidence = confidence.Value;
                }
            }

            if (best is null)
            {
                continue;
            }

            var (pattern, reason) = BuildPattern(software.Name, best.Name, bestConfidence);
            suggestions.Add(new DetectionSuggestion(
                software.Id,
                software.Name,
                best.Name,
                best.Version,
                pattern,
                bestConfidence,
                reason));
        }

        return ResolveAmbiguity(suggestions);
    }

    /// <summary>
    /// One installed program may be claimed by at most one catalog entry. When
    /// several claim it, the strongest match wins; a tie at the same confidence
    /// is genuinely ambiguous and both are dropped.
    ///
    /// Without this, "AIMP2" was matched by containment against the installed
    /// "AIMP" and both entries pointed at the same program.
    /// </summary>
    private static IReadOnlyList<DetectionSuggestion> ResolveAmbiguity(IReadOnlyList<DetectionSuggestion> suggestions)
    {
        var kept = new List<DetectionSuggestion>();

        foreach (var group in suggestions.GroupBy(s => s.InstalledName, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(s => s.Confidence)
                .ThenBy(s => s.SoftwareId, StringComparer.Ordinal)
                .ToList();

            if (ordered.Count == 1)
            {
                kept.Add(ordered[0]);
                continue;
            }

            // Strongest confidence claims it; a tie means we cannot tell them apart.
            if (ordered[0].Confidence == ordered[1].Confidence)
            {
                continue;
            }

            kept.Add(ordered[0]);
        }

        return kept.OrderBy(s => s.SoftwareId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Lowercase and strip everything that is not a letter or digit.</summary>
    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static SuggestionConfidence? Classify(string catalogName, string installedName, string installedNormalized)
    {
        if (string.Equals(catalogName, installedName, StringComparison.OrdinalIgnoreCase))
        {
            return SuggestionConfidence.Exact;
        }

        var catalogNormalized = Normalize(catalogName);
        if (catalogNormalized.Length == 0)
        {
            return null;
        }

        if (string.Equals(catalogNormalized, installedNormalized, StringComparison.Ordinal))
        {
            return SuggestionConfidence.Normalized;
        }

        if (Math.Min(catalogNormalized.Length, installedNormalized.Length) < MinimumContainmentLength)
        {
            return null;
        }

        return installedNormalized.Contains(catalogNormalized, StringComparison.Ordinal)
               || catalogNormalized.Contains(installedNormalized, StringComparison.Ordinal)
            ? SuggestionConfidence.Containment
            : null;
    }

    /// <summary>
    /// Builds an anchored regex. A prefix pattern is preferred when the installed
    /// name merely extends the catalog name, because vendors append version
    /// numbers that change on every update.
    /// </summary>
    private static (string Pattern, string Reason) BuildPattern(
        string catalogName,
        string installedName,
        SuggestionConfidence confidence)
    {
        var describes = confidence switch
        {
            SuggestionConfidence.Exact => "名称完全一致",
            SuggestionConfidence.Normalized => "去掉标点与空格后一致",
            _ => "去标点后一方包含另一方",
        };

        if (catalogName.Length >= MinimumPrefixLength
            && installedName.StartsWith(catalogName, StringComparison.OrdinalIgnoreCase))
        {
            return ($"^{Regex.Escape(catalogName)}", $"{describes}，且已安装名称以目录名称开头，使用前缀匹配以容忍版本号变化。");
        }

        return ($"^{Regex.Escape(installedName)}$", $"{describes}，使用精确匹配。");
    }

    private static bool IsBetter(
        SuggestionConfidence candidateConfidence,
        string candidateName,
        SuggestionConfidence currentConfidence,
        string currentName)
    {
        if (candidateConfidence != currentConfidence)
        {
            // The enum is ordered best-first.
            return candidateConfidence < currentConfidence;
        }

        return string.CompareOrdinal(candidateName, currentName) < 0;
    }
}
