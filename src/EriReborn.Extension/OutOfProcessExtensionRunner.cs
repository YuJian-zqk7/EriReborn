using System.Diagnostics;
using System.Text;
using EriReborn.Core.Logging;

namespace EriReborn.Extension;

/// <summary>
/// Runs one extension in a child process and talks to it over the line protocol.
///
/// <para>
/// The reason to pay for a process is containment: an extension that hangs is
/// timed out, and one that crashes is a dead child rather than a dead application.
/// It is not a security sandbox — the child is a normal .NET process — and nothing
/// here claims otherwise.
/// </para>
/// </summary>
public sealed class OutOfProcessExtensionRunner : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private readonly IAppLogger _log;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Task _drainErrors;
    private int _sequence;
    private bool _disposed;

    private OutOfProcessExtensionRunner(Process process, IAppLogger log, string extensionId, string extensionVersion)
    {
        _process = process;
        _input = process.StandardInput;
        _output = process.StandardOutput;
        _log = log;
        ExtensionId = extensionId;
        ExtensionVersion = extensionVersion;
        _drainErrors = Task.Run(DrainErrorsAsync);
    }

    public string ExtensionId { get; }

    public string ExtensionVersion { get; }

    /// <summary>Safe after disposal, so a caller checking "is it gone?" never throws.</summary>
    public bool HasExited
    {
        get
        {
            if (_disposed)
            {
                return true;
            }

            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // The process object has been released; it is certainly not running.
                return true;
            }
        }
    }

    /// <summary>Starts the host, performs the handshake, and returns a live runner.</summary>
    public static async Task<OutOfProcessExtensionRunner> StartAsync(
        string hostExecutable,
        string assemblyPath,
        ExtensionManifest manifest,
        IReadOnlyDictionary<string, string>? settings = null,
        TimeSpan? handshakeTimeout = null,
        IAppLogger? log = null,
        IReadOnlyList<string>? hostArguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(manifest);

        var logger = log ?? AppLog.For("ExtensionHost");

        var startInfo = new ProcessStartInfo(hostExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        // Arguments that come before the assembly path, so a host can also be
        // launched as "dotnet EriReborn.Extension.Host.dll <assembly>". Shipping an
        // apphost for every platform is not something this project does, and the
        // launcher should not assume one exists.
        foreach (var argument in hostArguments ?? Array.Empty<string>())
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(assemblyPath);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动扩展宿主进程。");

        try
        {
            await process.StandardInput.WriteLineAsync(
                ExtensionProtocol.Write(ExtensionMessage.Hello(
                    manifest.Id,
                    manifest.Version ?? "0.0.0",
                    manifest.Permissions,
                    settings ?? new Dictionary<string, string>()))).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            // The handshake is "read until ready", not "read one line": an
            // extension usually logs something in its Start, and that log arrives
            // before ready. Expecting ready first made every real extension fail to
            // start.
            var deadline = DateTimeOffset.UtcNow + (handshakeTimeout ?? TimeSpan.FromSeconds(20));
            ExtensionMessage? greeting = null;

            while (greeting is null)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException("扩展宿主没有在期限内完成握手。");
                }

                var (handshakeTimedOut, line) = await ReadLineWithTimeoutAsync(process, remaining, cancellationToken).ConfigureAwait(false);
                if (handshakeTimedOut)
                {
                    throw new InvalidOperationException("扩展宿主没有在期限内完成握手。");
                }

                if (line is null)
                {
                    throw new InvalidOperationException("扩展宿主在握手期间结束了输出。");
                }

                if (!ExtensionProtocol.TryRead(line, out var message, out var error))
                {
                    throw new InvalidOperationException("扩展宿主握手失败：" + error);
                }

                switch (message!.Kind)
                {
                    case ExtensionMessage.LogKind:
                        logger.Write(
                            message.Level?.ToLowerInvariant() switch
                            {
                                "error" => LogLevel.Error,
                                "warn" or "warning" => LogLevel.Warn,
                                _ => LogLevel.Info,
                            },
                            "extension.process",
                            $"[{manifest.Id}] {message.Message}");
                        continue;

                    case ExtensionMessage.ErrorKind:
                        throw new InvalidOperationException("扩展宿主报告失败：" + message.Error);

                    case ExtensionMessage.ReadyKind:
                        greeting = message;
                        break;

                    default:
                        throw new InvalidOperationException($"扩展宿主握手时期望 ready，收到 '{message.Kind}'。");
                }
            }

            logger.Info("extension.process", $"扩展 '{greeting.Id}' v{greeting.Version} 已在独立进程中启动。");
            return new OutOfProcessExtensionRunner(
                process,
                logger,
                greeting.Id ?? manifest.Id,
                greeting.Version ?? manifest.Version ?? "0.0.0");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>Sends one request. Requests are serialised: the protocol is one turn at a time.</summary>
    public async Task<ExtensionResponse> SendAsync(
        ExtensionRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process.HasExited)
            {
                return ExtensionResponse.Failed("扩展进程已经退出。");
            }

            var id = request.Id ?? $"req-{++_sequence:D4}";
            var outgoing = request with { Id = id };

            await _input.WriteLineAsync(ExtensionProtocol.Write(ExtensionMessage.Request(outgoing))).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    // A hung extension must not become a hung application.
                    _log.Warn("extension.timeout", $"扩展 '{ExtensionId}' 未在期限内响应 '{request.Command}'，进程已终止。");
                    TryKill(_process);
                    return ExtensionResponse.Failed("扩展没有在期限内响应，进程已终止。");
                }

                var (timedOut, line) = await ReadLineWithTimeoutAsync(_process, remaining, cancellationToken).ConfigureAwait(false);
                if (timedOut)
                {
                    // A hung extension must not become a hung application.
                    _log.Warn("extension.timeout", $"扩展 '{ExtensionId}' 未在期限内响应 '{request.Command}'，进程已终止。");
                    TryKill(_process);
                    return ExtensionResponse.Failed("扩展没有在期限内响应，进程已终止。");
                }

                if (line is null)
                {
                    return ExtensionResponse.Failed("扩展进程结束了输出。");
                }

                if (!ExtensionProtocol.TryRead(line, out var message, out var error))
                {
                    _log.Warn("extension.protocol", $"扩展 '{ExtensionId}' 发出了无法解析的一行：{error}");
                    continue;
                }

                switch (message!.Kind)
                {
                    case ExtensionMessage.LogKind:
                        ForwardLog(message);
                        continue;

                    case ExtensionMessage.ResponseKind when string.Equals(message.Id, id, StringComparison.Ordinal):
                        return new ExtensionResponse(message.Ok, message.Result, message.Error);

                    case ExtensionMessage.ResponseKind:
                        // A response to something else means the turns have crossed.
                        _log.Warn("extension.protocol", $"扩展 '{ExtensionId}' 响应了意外的请求 id '{message.Id}'。");
                        continue;

                    case ExtensionMessage.ErrorKind:
                        return ExtensionResponse.Failed(message.Error ?? "扩展报告了错误。");

                    default:
                        continue;
                }
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>Asks the child for the settings it wrote, so a write survives the process.</summary>
    public async Task<IReadOnlyDictionary<string, string>> DumpSettingsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new ExtensionRequest(ServiceCommands.DumpSettings), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok || string.IsNullOrWhiteSpace(response.Result))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(response.Result)
                ?? new Dictionary<string, string>();
        }
        catch (System.Text.Json.JsonException)
        {
            _log.Warn("extension.protocol", $"扩展 '{ExtensionId}' 返回的设置不是合法 JSON。");
            return new Dictionary<string, string>();
        }
    }

    private void ForwardLog(ExtensionMessage message)
        => _log.Write(
            message.Level?.ToLowerInvariant() switch
            {
                "error" => LogLevel.Error,
                "warn" or "warning" => LogLevel.Warn,
                "debug" => LogLevel.Debug,
                "trace" => LogLevel.Trace,
                _ => LogLevel.Info,
            },
            "extension.process",
            $"[{ExtensionId}] {message.Message}");

    private async Task DrainErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _log.Warn("extension.stderr", $"[{ExtensionId}] {line}");
                }
            }
        }
        catch (Exception)
        {
            // The pipe closes when the child dies; that is not an error worth reporting.
        }
    }

    /// <summary>
    /// Reads one line, distinguishing "the child said nothing in time" from "the
    /// child closed its output". Both used to be a null, and reporting a timeout as
    /// a closed pipe sends the reader looking in the wrong place.
    /// </summary>
    private static async Task<(bool TimedOut, string? Line)> ReadLineWithTimeoutAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var read = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
        var finished = await Task.WhenAny(read, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

        return finished == read ? (false, await read.ConfigureAwait(false)) : (true, null);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // Ask it to stop, then make sure it did.
            await SendAsync(new ExtensionRequest(ServiceCommands.Shutdown), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A child that will not answer is killed below regardless.
        }

        TryKill(_process);

        try
        {
            await _drainErrors.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort.
        }

        _process.Dispose();
        _turn.Dispose();
    }
}
