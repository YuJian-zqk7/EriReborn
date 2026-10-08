using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Engine.Software;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Proposes detection overrides for catalog entries that declare none, using the
/// real installed-software list, then proves the proposals actually work by
/// rescanning. The user's own overrides are restored afterwards.
/// </summary>
internal static class SuggestionCheck
{
    public static async Task<int> RunAsync(AppHost host)
    {
        var failures = 0;

        if (host.Platform.Detector is not IInstalledSoftwareSource source)
        {
            Console.WriteLine("SKIP installed-software enumeration is not supported on this platform.");
            return 0;
        }

        // Everything is restored at the end, so the user's state is untouched.
        var saved = host.DetectionHints.All.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        try
        {
            var installed = await source.ListInstalledAsync();
            var undecidable = host.Catalog.Software.Where(s => s.Detector is null || s.Detector.IsNone).ToList();

            Console.WriteLine($"      source: {source.SourceDescription}");
            Console.WriteLine($"      installed programs: {installed.Count}");
            Console.WriteLine($"      catalog entries without a detector: {undecidable.Count}");

            var suggestions = DetectionSuggester.Suggest(undecidable, installed);
            Console.WriteLine($"      suggestions: {suggestions.Count}");

            foreach (var group in suggestions.GroupBy(s => s.Confidence).OrderBy(g => g.Key))
            {
                Console.WriteLine($"        {group.Key,-12} {group.Count()}");
            }

            Console.WriteLine("      examples:");
            foreach (var suggestion in suggestions.Take(6))
            {
                Console.WriteLine($"        {suggestion.SoftwareId,-22} <- {suggestion.InstalledName,-34} {suggestion.ArpPattern}");
            }

            failures += Report("the matcher proposes at least one override", suggestions.Count > 0);

            // The proposals must be conclusive, not merely plausible.
            var before = await host.ScanService.ScanAsync(host.Catalog.Software, host.Environment);
            host.DetectionHints.SetMany(suggestions.ToDictionary(s => s.SoftwareId, s => s.ToHint()));
            var after = await host.ScanService.ScanAsync(host.Catalog.Software, host.Environment);

            Console.WriteLine($"      unknown before: {before.Unknown} -> after: {after.Unknown}");
            failures += Report("applying the suggestions decides more entries", after.Unknown < before.Unknown);
            failures += Report("no entry became a failure", after.Failed == 0 && before.Failed == 0);
            failures += Report("nothing became 'not installed' by accident", after.Missing <= before.Missing);
            failures += Report("the scan total is unchanged", after.Total == before.Total);

            var newlyDecided = before.Unknown - after.Unknown;
            Console.WriteLine($"      newly decided by user-supplied patterns: {newlyDecided}");
        }
        finally
        {
            // Restore exactly what was there before this run.
            var current = host.DetectionHints.All.Keys.ToList();
            foreach (var id in current)
            {
                host.DetectionHints.Remove(id);
            }

            host.DetectionHints.SetMany(saved);
        }

        return failures;
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
