namespace EriReborn.Platform.Abstractions;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    bool CaptureOutput = true);

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Started);

/// <summary>Platform process execution. Core never touches Process directly (spec 5).</summary>
public interface IProcessService
{
    bool IsSupported { get; }

    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);

    bool Exists(string fileName);
}
