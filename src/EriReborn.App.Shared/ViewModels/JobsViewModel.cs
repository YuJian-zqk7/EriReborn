using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Jobs;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>One job, as the list shows it.</summary>
public sealed class JobItemViewModel
{
    public JobItemViewModel(Job job)
    {
        Job = job;
    }

    public Job Job { get; }

    public string Title => Job.Title;

    public string Kind => Job.Kind;

    public string StateText => Job.State switch
    {
        JobState.Queued => "排队中",
        JobState.Running => "进行中",
        JobState.Completed => "已完成",
        JobState.Failed => "失败",
        JobState.Cancelled => "已取消",
        _ => Job.State.ToString(),
    };

    /// <summary>
    /// An indeterminate job says so instead of showing a bar that looks finished. A line the worker
    /// wrote — a download's size, speed and remaining time — is preferred over the bare counter,
    /// because it is what the person actually wants to know.
    /// </summary>
    public string ProgressText => Job.Progress.Message is { Length: > 0 } message
        ? message
        : Job.Progress.Total <= 0
            ? "进度未知"
            : Job.Progress.ToString();

    /// <summary>A real 0..1 fraction for the progress bar; zero when the size is not known yet.</summary>
    public double Fraction => Job.Progress.Fraction ?? 0;

    /// <summary>True when there is a real fraction to draw; otherwise the bar stays indeterminate.</summary>
    public bool HasFraction => Job.Progress.Fraction is not null;

    /// <summary>True when this job is a real download with transfer numbers behind it.</summary>
    public bool HasTransferDetail => Job.Progress.Telemetry is not null;

    /// <summary>The speed the downloader measured, in units a person reads.</summary>
    public string? SpeedText
        => Job.Progress.Telemetry is { BytesPerSecond: > 0 } telemetry
            ? FormatSize((long)telemetry.BytesPerSecond) + "/s"
            : null;

    /// <summary>What is left, from the downloader's own estimate rather than a second guess.</summary>
    public string? EtaText
        => Job.Progress.Telemetry?.Eta is { } eta && eta > TimeSpan.Zero ? "剩余 " + FormatEta(eta) : null;

    /// <summary>What the checks said once the bytes were in: the hash comparison, a size check.</summary>
    public string? VerdictText => Job.Progress.Verdict;

    /// <summary>True once there is a check result to show.</summary>
    public bool HasVerdict => !string.IsNullOrWhiteSpace(Job.Progress.Verdict);

    /// <summary>The transfer readout in one line: speed and what is left.</summary>
    public string TransferText => string.Join(
        " · ",
        new[] { SpeedText, EtaText }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// The technical numbers, folded away behind 「查看详细信息」. They are kept rather than dropped,
    /// so a problem can still be diagnosed without putting raw counters in everyone's way.
    /// </summary>
    public string? AdvancedDetail => Job.Progress.Telemetry is { } telemetry
        ? $"已接收 {telemetry.BytesReceived} 字节 / 共 {(telemetry.TotalBytes is { } total ? total.ToString() : "未知")} 字节；"
            + $"速度 {telemetry.BytesPerSecond:0} B/s；任务类型 {Job.Kind}"
        : null;

    public bool CanCancel => !Job.IsFinished;

    public string Detail => Job.Error is { Length: > 0 } error
        ? $"{StateText}：{error}"
        : StateText;

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30)
        {
            return (bytes / (double)(1L << 30)).ToString("0.#") + " GB";
        }

        if (bytes >= 1L << 20)
        {
            return (bytes / (double)(1L << 20)).ToString("0.#") + " MB";
        }

        return bytes >= 1024 ? (bytes / 1024.0).ToString("0.#") + " KB" : bytes + " B";
    }

    private static string FormatEta(TimeSpan eta)
        => eta.TotalHours >= 1
            ? $"{(int)eta.TotalHours} 小时 {eta.Minutes} 分"
            : eta.TotalMinutes >= 1
                ? $"{(int)eta.TotalMinutes} 分 {eta.Seconds} 秒"
                : eta.Seconds + " 秒";

    public override string ToString() => $"{Title} — {Detail}";
}

/// <summary>
/// The job list. Nothing here is invented: it reads the manager's jobs, which
/// always carry a real state (spec 10).
/// </summary>
public sealed partial class JobsViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private readonly SynchronizationContext? _context;

    public JobsViewModel(AppHost host)
    {
        _host = host;
        Title = "任务";

        // Captured so an update raised from a worker thread is applied where the
        // bindings live. Outside a UI there is no context and it runs inline,
        // which is what makes this testable.
        _context = SynchronizationContext.Current;

        host.Jobs.Changed += (_, _) => Post(Refresh);
        Refresh();
    }

    public ObservableCollection<JobItemViewModel> Jobs { get; } = new();

    [ObservableProperty]
    private string _summary = string.Empty;

    public bool HasJobs => Jobs.Count > 0;

    public int RunningCount => _host.Jobs.RunningCount;

    [RelayCommand]
    public void Refresh()
    {
        var snapshot = _host.Jobs.Jobs;

        Jobs.Clear();
        foreach (var job in snapshot)
        {
            Jobs.Add(new JobItemViewModel(job));
        }

        RunningCountText = _host.Jobs.RunningCount;
        Summary = snapshot.Count == 0
            ? "目前没有任务。长操作（批量安装、扫描、下载）会出现在这里，并且可以取消。"
            : $"共 {snapshot.Count} 个任务，其中 {_host.Jobs.RunningCount} 个进行中。";

        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(RunningCount));
    }

    [ObservableProperty]
    private int _runningCountText;

    [RelayCommand]
    private void Cancel(JobItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // A job that already finished is not an error; the list just will not change.
        _host.Jobs.Cancel(item.Job.Id);
        Refresh();
    }

    [RelayCommand]
    private void CancelAll()
    {
        foreach (var job in _host.Jobs.Jobs.Where(job => !job.IsFinished).ToList())
        {
            _host.Jobs.Cancel(job.Id);
        }

        Refresh();
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
            return;
        }

        _context.Post(_ => action(), null);
    }
}
