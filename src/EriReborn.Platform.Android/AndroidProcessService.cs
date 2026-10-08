using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Android;

/// <summary>
/// Android has no general process-execution model for third-party apps, so
/// this capability is reported as genuinely unavailable rather than faked
/// (spec 6/68).
/// </summary>
public sealed class AndroidProcessService : IProcessService
{
    public bool IsSupported => false;

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProcessResult(
            -1,
            string.Empty,
            "Android 不提供通用进程执行能力。",
            TimedOut: false,
            Started: false));

    public bool Exists(string fileName) => false;
}
