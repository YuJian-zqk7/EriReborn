using System.Diagnostics;
using System.Text;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Windows;

/// <summary>
/// Real process execution. Argument lists are passed through
/// <see cref="ProcessStartInfo.ArgumentList"/> so nothing is re-parsed by a
/// shell (spec 19: no Start-Process-style shortcut for scripts).
/// </summary>
public sealed class WindowsProcessService(IAppLogger log) : IProcessService
{
    private readonly IAppLogger _log = log;

    public bool IsSupported => true;

    public bool Exists(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (File.Exists(fileName))
        {
            return true;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return true;
                }

                foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".com" })
                {
                    if (File.Exists(candidate + extension))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // A malformed PATH entry must not abort the probe.
            }
        }

        return false;
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = request.CaptureOutput,
            RedirectStandardError = request.CaptureOutput,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _log.Info("process.run", $"{request.FileName} {string.Join(' ', request.Arguments)}");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, "Process could not be started.", false, false);
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            if (request.CaptureOutput)
            {
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            using var timeoutSource = request.Timeout is { } timeout
                ? new CancellationTokenSource(timeout)
                : null;
            using var linked = timeoutSource is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var timedOut = timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested;
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Killing may fail if the process already exited.
                }

                _log.Warn("process.timeout", $"{request.FileName} did not finish in time.");
                return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), timedOut, true);
            }

            var code = process.ExitCode;
            _log.Info("process.exit", $"{request.FileName} exited with {code}.");
            return new ProcessResult(code, stdout.ToString(), stderr.ToString(), false, true);
        }
        catch (Exception ex)
        {
            _log.Error("process.failed", $"Failed to run '{request.FileName}'.", ex);
            return new ProcessResult(-1, string.Empty, ex.Message, false, false);
        }
    }
}
