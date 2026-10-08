using System.Text.Json;
using EriReborn.Extension;

namespace EriReborn.SampleExtension;

/// <summary>
/// The out-of-process half of the sample: a real extension that answers messages
/// instead of being called. It exists so the process boundary can be exercised
/// end to end rather than described (spec 34/35).
/// </summary>
public sealed class SampleService : IExtensionService
{
    private IExtensionServiceContext? _context;

    public string Id => "sample_service";

    public string Version => "1.0.0";

    /// <summary>Set when at least one request has been handled, so the test can see the session ran.</summary>
    public int Handled { get; private set; }

    public void Start(IExtensionServiceContext context)
    {
        _context = context;
        context.Log("info", "SampleService started.");
    }

    public Task<ExtensionResponse> HandleAsync(ExtensionRequest request)
    {
        if (_context is null)
        {
            return Task.FromResult(ExtensionResponse.Failed("Start 尚未被调用。"));
        }

        Handled++;

        return Task.FromResult(request.Command switch
        {
            "greet" => ExtensionResponse.Done("Hello " + (request.Argument ?? "world")),

            // Writes go through the host, so an undeclared settings.write is refused
            // by the process that owns the permissions rather than by the caller.
            "remember" => Remember(request.Argument),

            "recall" => ExtensionResponse.Done(_context.GetSetting("note", "(none)")),

            "whoami" => ExtensionResponse.Done(
                JsonSerializer.Serialize(new
                {
                    Id,
                    Permissions = _context.Permissions.Declared.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                    Handled,
                })),

            // Never returns until the delay elapses, so the parent's timeout can be
            // exercised against something that really hangs.
            "sleep" => Sleep(request.Argument),

            "boom" => throw new InvalidOperationException("sample_service 故意失败。"),

            _ => ExtensionResponse.Failed("未知命令：" + request.Command),
        });
    }

    /// <summary>Blocks the extension's turn for the requested number of milliseconds.</summary>
    private static ExtensionResponse Sleep(string? argument)
    {
        var milliseconds = int.TryParse(argument, out var parsed) ? Math.Clamp(parsed, 0, 60_000) : 0;
        Thread.Sleep(milliseconds);
        return ExtensionResponse.Done($"slept {milliseconds}");
    }

    private ExtensionResponse Remember(string? note)
    {
        _context!.SetSetting("note", note ?? string.Empty);
        return ExtensionResponse.Done("remembered");
    }
}
