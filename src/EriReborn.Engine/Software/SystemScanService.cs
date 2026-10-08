using System.Collections.Concurrent;
using System.Diagnostics;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Paths;

namespace EriReborn.Engine.Software;

public sealed record ScanProgress(int Completed, int Total, string CurrentSoftware);

/// <summary>
/// Result of one environment scan. Detected / NotDetected / Unsupported /
/// Unknown / Error stay separate so the UI never folds them into a boolean
/// (spec 11/22).
/// </summary>
public sealed record ScanResult(
    int Total,
    int Installed,
    int Missing,
    int Unsupported,
    int Unknown,
    int Failed,
    int WithoutDetector,
    TimeSpan Duration,
    IReadOnlyDictionary<string, DetectionResult> Results)
{
    public static ScanResult Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero,
        new Dictionary<string, DetectionResult>(StringComparer.Ordinal));

    public int Conclusive => Installed + Missing;

    public string Describe()
        => $"扫描 {Total} 条：已安装 {Installed}，未安装 {Missing}，不支持 {Unsupported}，未知 {Unknown}，失败 {Failed}"
           + $"（其中 {WithoutDetector} 条清单未声明检测方式，无法判定）。耗时 {Duration.TotalSeconds:0.0}s。";
}

/// <summary>
/// Scans a set of software definitions through the engine with bounded
/// concurrency (spec 72/73).
/// </summary>
public sealed class SystemScanService(SoftwareEngine engine, IAppLogger log)
{
    private readonly SoftwareEngine _engine = engine;
    private readonly IAppLogger _log = log;

    public async Task<ScanResult> ScanAsync(
        IReadOnlyList<SoftwareDefinition> items,
        EnvironmentContext? context = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int maxConcurrency = 4)
    {
        if (items.Count == 0)
        {
            return ScanResult.Empty;
        }

        // One snapshot for the whole scan; the detector caches expensive reads.
        _engine.InvalidateDetectionCache();

        var results = new ConcurrentDictionary<string, DetectionResult>(StringComparer.Ordinal);
        var completed = 0;
        var stopwatch = Stopwatch.StartNew();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, maxConcurrency),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(items, options, async (item, token) =>
        {
            DetectionResult result;
            try
            {
                result = await _engine.DetectAsync(item, context, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error("scan.detect", $"Detection threw for '{item.Id}'.", ex);
                result = DetectionResult.Error(ex.Message, "scan");
            }

            results[item.Id] = result;

            var done = Interlocked.Increment(ref completed);
            progress?.Report(new ScanProgress(done, items.Count, item.Name));
        }).ConfigureAwait(false);

        stopwatch.Stop();

        var scan = new ScanResult(
            Total: items.Count,
            Installed: results.Values.Count(r => r.Outcome == DetectionOutcome.Detected),
            Missing: results.Values.Count(r => r.Outcome == DetectionOutcome.NotDetected),
            Unsupported: results.Values.Count(r => r.Outcome == DetectionOutcome.Unsupported),
            Unknown: results.Values.Count(r => r.Outcome == DetectionOutcome.Unknown),
            Failed: results.Values.Count(r => r.Outcome == DetectionOutcome.Error),
            WithoutDetector: items.Count(i => i.Detector is null || i.Detector.IsNone),
            Duration: stopwatch.Elapsed,
            Results: results);

        _log.Info("scan.done", scan.Describe());
        return scan;
    }
}
