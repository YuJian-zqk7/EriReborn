using System.Reflection;
using System.Runtime.Loader;
using EriReborn.Extension;

namespace EriReborn.Extension.Host;

/// <summary>
/// Runs one extension in its own process.
///
/// <para>
/// Usage: <c>EriReborn.Extension.Host &lt;assembly&gt;</c>. The parent writes a
/// hello line describing the manifest, this process loads the assembly, answers
/// requests and exits — so a hung or crashing extension costs the parent a dead
/// child, not a dead application.
/// </para>
///
/// <para>
/// This process is a normal .NET process. It provides isolation from the
/// application, not from the machine.
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // stdout is the wire: nothing else may write to it, or the framing breaks.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var wire = Console.Out;
        var autoFlush = Console.Out;
        Console.SetOut(Console.Error);

        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            Write(wire, ExtensionMessage.Failure("没有指定扩展程序集。"));
            return 2;
        }

        var assemblyPath = Path.GetFullPath(args[0]);
        if (!File.Exists(assemblyPath))
        {
            Write(wire, ExtensionMessage.Failure("找不到程序集：" + assemblyPath));
            return 2;
        }

        var helloLine = await Console.In.ReadLineAsync().ConfigureAwait(false);
        if (!ExtensionProtocol.TryRead(helloLine, out var hello, out var helloError)
            || hello!.Kind != ExtensionMessage.HelloKind)
        {
            Write(wire, ExtensionMessage.Failure("握手失败：" + (helloError ?? "第一条消息必须是 hello。")));
            return 2;
        }

        var permissions = new ExtensionPermissions(hello.Permissions);
        var settings = new Dictionary<string, string>(hello.Settings ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var context = new ServiceContext(permissions, settings, wire);

        IExtensionService? service;
        try
        {
            var loadContext = new AssemblyLoadContext("extension-host", isCollectible: false);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            var implementation = assembly.GetTypes()
                .Where(type => typeof(IExtensionService).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                .FirstOrDefault();

            if (implementation is null)
            {
                Write(wire, ExtensionMessage.Failure("程序集没有实现 IExtensionService。"));
                return 3;
            }

            service = Activator.CreateInstance(implementation) as IExtensionService;
            if (service is null)
            {
                Write(wire, ExtensionMessage.Failure("无法构造 IExtensionService 实现。"));
                return 3;
            }

            service.Start(context);
        }
        catch (Exception ex)
        {
            Write(wire, ExtensionMessage.Failure($"{ex.GetType().Name}: {ex.Message}"));
            return 3;
        }

        Write(wire, ExtensionMessage.Ready(service.Id, service.Version));

        while (true)
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                // The parent closed the pipe: that is the only shutdown signal needed.
                break;
            }

            if (!ExtensionProtocol.TryRead(line, out var message, out var error) || message!.Kind != ExtensionMessage.RequestKind)
            {
                Write(wire, ExtensionMessage.Failure(error ?? "期望 request 消息。"));
                continue;
            }

            var request = new ExtensionRequest(message.Command ?? string.Empty, message.Argument, message.Id);

            // Runtime commands belong to this process, not to the extension: asking
            // an extension to implement "shutdown" would make its own contract
            // depend on the runtime that happens to host it.
            if (request.Command == ServiceCommands.DumpSettings)
            {
                Write(wire, ExtensionMessage.Response(request, ExtensionResponse.Done(
                    System.Text.Json.JsonSerializer.Serialize(context.Settings))));
                continue;
            }

            if (request.Command == ServiceCommands.Shutdown)
            {
                Write(wire, ExtensionMessage.Response(request, ExtensionResponse.Done()));
                break;
            }

            ExtensionResponse response;
            try
            {
                response = await service.HandleAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failing extension fails its request, not the session.
                response = ExtensionResponse.Failed($"{ex.GetType().Name}: {ex.Message}");
            }

            Write(wire, ExtensionMessage.Response(request, response));
        }

        autoFlush.Flush();
        return 0;
    }

    /// <summary>Writes one protocol line and flushes, or the parent waits for it.</summary>
    private static void Write(TextWriter wire, ExtensionMessage message)
    {
        wire.WriteLine(ExtensionProtocol.Write(message));
        wire.Flush();
    }

    /// <summary>
    /// The child's own host. It offers exactly what the manifest declared and
    /// nothing else — there is no filesystem, process or registry surface here to
    /// call, which is the point.
    /// </summary>
    private sealed class ServiceContext(
        ExtensionPermissions permissions,
        Dictionary<string, string> settings,
        TextWriter wire) : IExtensionServiceContext
    {
        public ExtensionPermissions Permissions { get; } = permissions;

        public Dictionary<string, string> Settings { get; } = settings;

        public string? GetSetting(string key, string? fallback = null)
        {
            if (!Permissions.Allows(ExtensionPermissions.SettingsRead))
            {
                Log("warn", $"没有声明 '{ExtensionPermissions.SettingsRead}'，读取 '{key}' 被拒绝。");
                return fallback;
            }

            return Settings.TryGetValue(key, out var value) ? value : fallback;
        }

        public void SetSetting(string key, string value)
        {
            if (!Permissions.Allows(ExtensionPermissions.SettingsWrite))
            {
                Log("warn", $"没有声明 '{ExtensionPermissions.SettingsWrite}'，写入 '{key}' 被拒绝。");
                return;
            }

            Settings[key] = value;
        }

        public void Log(string level, string message)
        {
            // Logs travel on the same wire as everything else, so ordering is kept.
            Write(wire, ExtensionMessage.Logged(level, message));
        }
    }
}


