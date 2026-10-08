using System.Text.Json;
using System.Text.Json.Serialization;

namespace EriReborn.Extension;

/// <summary>
/// The contract an extension implements to run in <b>another process</b>.
///
/// <para>
/// <see cref="IExtension"/> is unchanged and stays the in-process contract. A
/// separate process cannot be reached by method calls, so isolation needs a
/// message surface — and inventing one inside the old interface would have meant
/// rewriting every existing extension for a capability most of them do not need.
/// </para>
///
/// <para>
/// <b>What this buys, exactly.</b> The extension's memory, threads and crashes are
/// no longer the application's. A hang can be timed out; a crash is a dead child
/// rather than a dead app. It is <b>not</b> a sandbox: the child is an ordinary
/// .NET process and an extension that ignores this contract can still touch the
/// machine. Restricting that needs a restricted token, which is a separate piece
/// of work and is not pretended here.
/// </para>
/// </summary>
public interface IExtensionService
{
    string Id { get; }

    string Version { get; }

    /// <summary>Called once, before any request, with what the manifest declared.</summary>
    void Start(IExtensionServiceContext context);

    Task<ExtensionResponse> HandleAsync(ExtensionRequest request);
}

/// <summary>What an out-of-process extension may reach. Deliberately tiny.</summary>
public interface IExtensionServiceContext
{
    /// <summary>What the manifest declared — everything else is denied.</summary>
    ExtensionPermissions Permissions { get; }

    string? GetSetting(string key, string? fallback = null);

    void SetSetting(string key, string value);

    void Log(string level, string message);
}

/// <summary>
/// Commands the runtime itself owns. They live here rather than in the child so
/// both sides agree on the names; an extension must not reuse them.
/// </summary>
public static class ServiceCommands
{
    public const string Shutdown = "service.shutdown";

    /// <summary>Returns the settings the extension wrote, so the parent can persist them.</summary>
    public const string DumpSettings = "service.settings";
}

public sealed record ExtensionRequest(string Command, string? Argument = null, string? Id = null);

public sealed record ExtensionResponse(bool Ok, string? Result = null, string? Error = null)
{
    public static ExtensionResponse Done(string? result = null) => new(true, result);

    public static ExtensionResponse Failed(string error) => new(false, null, error);
}

/// <summary>
/// One line of the wire protocol. A single record with a kind discriminator keeps
/// the shape in one place, so a field added for one direction is not silently lost
/// in the other.
/// </summary>
public sealed record ExtensionMessage
{
    public const string HelloKind = "hello";
    public const string ReadyKind = "ready";
    public const string RequestKind = "request";
    public const string ResponseKind = "response";
    public const string LogKind = "log";
    public const string ErrorKind = "error";

    public required string Kind { get; init; }

    public string? Id { get; init; }

    public string? Version { get; init; }

    public IReadOnlyList<string>? Permissions { get; init; }

    public IReadOnlyDictionary<string, string>? Settings { get; init; }

    public string? Command { get; init; }

    public string? Argument { get; init; }

    public bool Ok { get; init; }

    public string? Result { get; init; }

    public string? Error { get; init; }

    public string? Level { get; init; }

    public string? Message { get; init; }

    public static ExtensionMessage Hello(string id, string version, IEnumerable<string> permissions, IReadOnlyDictionary<string, string> settings)
        => new()
        {
            Kind = HelloKind,
            Id = id,
            Version = version,
            Permissions = permissions.ToList(),
            Settings = settings,
        };

    public static ExtensionMessage Ready(string id, string version)
        => new() { Kind = ReadyKind, Id = id, Version = version };

    public static ExtensionMessage Request(ExtensionRequest request)
        => new() { Kind = RequestKind, Id = request.Id, Command = request.Command, Argument = request.Argument };

    public static ExtensionMessage Response(ExtensionRequest request, ExtensionResponse response)
        => new()
        {
            Kind = ResponseKind,
            Id = request.Id,
            Ok = response.Ok,
            Result = response.Result,
            Error = response.Error,
        };

    public static ExtensionMessage Logged(string level, string message)
        => new() { Kind = LogKind, Level = level, Message = message };

    public static ExtensionMessage Failure(string error)
        => new() { Kind = ErrorKind, Error = error };
}

/// <summary>
/// Reads and writes protocol lines.
///
/// <para>
/// Pure on purpose. Everything a child and a parent disagree about shows up here
/// first, and a line protocol is exactly the kind of thing that works until the
/// first message containing a newline.
/// </para>
/// </summary>
public static class ExtensionProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,

        // A wire format is read by people as well as parsers: camelCase is what
        // anyone writing a line by hand will reach for, and a protocol that only
        // accepts its own writer's casing is a trap.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serialises one message onto a single line.</summary>
    public static string Write(ExtensionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // A newline inside a value would split one message into two lines, and the
        // reader would see a truncated JSON object. The serializer escapes it, but
        // the check documents that this is the invariant the framing depends on.
        var line = JsonSerializer.Serialize(message, Options);
        if (line.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("协议消息不得包含换行。");
        }

        return line;
    }

    /// <summary>Parses one line. A bad line is refused with a reason, never guessed at.</summary>
    public static bool TryRead(string? line, out ExtensionMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "空行不是消息。";
            return false;
        }

        ExtensionMessage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ExtensionMessage>(line, Options);
        }
        catch (JsonException ex)
        {
            // The exception type matters when diagnosing a version mismatch: a
            // missing member and a malformed line look identical otherwise.
            error = $"无法解析协议消息（{ex.GetType().Name}）：{ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "协议消息是 null。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Kind))
        {
            error = "协议消息缺少 kind。";
            return false;
        }

        switch (parsed.Kind)
        {
            case ExtensionMessage.HelloKind:
            case ExtensionMessage.ReadyKind:
            case ExtensionMessage.RequestKind:
            case ExtensionMessage.ResponseKind:
            case ExtensionMessage.LogKind:
            case ExtensionMessage.ErrorKind:
                message = parsed;
                return true;

            default:
                // An unknown kind is a version mismatch, and treating it as a no-op
                // would hide it until something timed out.
                error = $"未知的协议消息类型 '{parsed.Kind}'。";
                return false;
        }
    }
}
