using System.Security.Cryptography;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Engine.Download;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Downloads a real file through the aria2 engine.
///
/// aria2 is optional and may simply not be installed, in which case this reports
/// SKIP with the reason rather than pretending anything was verified.
/// </summary>
internal static class Aria2Check
{
    public static async Task<int> RunAsync(AppHost host)
    {
        var failures = 0;
        var locator = new Aria2Locator();
        var executable = locator.Find();

        if (executable is null)
        {
            Console.WriteLine("SKIP aria2 未安装，无法验证该引擎。");
            Console.WriteLine("     安装后重跑即可（例如 winget install aria2.aria2）。");
            return 0;
        }

        Console.WriteLine($"      aria2c: {executable}");

        var engine = new Aria2DownloadEngine(host.Platform.Processes, locator, AppLog.For("Smoke"));
        var route = new DownloadRoute(
            DownloadRouteKind.ProviderDirect,
            "http://127.0.0.1:0/aria2.bin",
            Note: "smoke");

        failures += Report("引擎声明可以处理直链路由", engine.CanHandle(route));

        // A selector that only knows this engine must pick it, otherwise the
        // abstraction would silently prefer something else.
        var selector = new DownloadEngineSelector(new IDownloadEngine[] { engine }, AppLog.For("Smoke"));
        failures += Report("选择器会选中 aria2 引擎", ReferenceEquals(engine, selector.Select(route)));

        var payload = new byte[300_000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var server = new LocalHttpServer();
        server.Add("/aria2.bin", payload);

        var destination = Path.Combine(host.Paths.UserDataDirectory, "aria2-smoke", "aria2.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        var realRoute = route with { Url = server.Url("/aria2.bin"), FileName = "aria2.bin" };

        var result = await engine.DownloadAsync(realRoute, new DownloadRequest
        {
            Url = realRoute.Url,
            DestinationPath = destination,
            ExpectedSha256 = expected,
            FileName = "aria2.bin",
        });

        Console.WriteLine($"      {result.State}: {result.BytesWritten} bytes");
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            Console.WriteLine($"      {result.Message}");
        }

        failures += Report("aria2 下载成功", result.State == DownloadState.Success);
        failures += Report("产物大小与源一致", result.BytesWritten == payload.Length);
        failures += Report("产物 SHA-256 与源一致", string.Equals(result.Sha256, expected, StringComparison.OrdinalIgnoreCase));

        if (File.Exists(destination))
        {
            var actual = await File.ReadAllBytesAsync(destination);
            failures += Report("落盘文件与源逐字节一致", actual.AsSpan().SequenceEqual(payload));
        }
        else
        {
            failures += Report("落盘文件存在", false);
        }

        // A wrong hash must be refused, or the engine would accept anything.
        var tampered = Path.Combine(Path.GetDirectoryName(destination)!, "aria2-bad.bin");
        var badResult = await engine.DownloadAsync(
            realRoute with { FileName = "aria2-bad.bin" },
            new DownloadRequest
            {
                Url = realRoute.Url,
                DestinationPath = tampered,
                ExpectedSha256 = new string('0', 64),
                FileName = "aria2-bad.bin",
            });

        failures += Report(
            "哈希不符时被拒绝",
            badResult.State is DownloadState.HashMismatch or DownloadState.NetworkError or DownloadState.SizeMismatch);

        return failures;
    }

    private static int Report(string what, bool passed)
    {
        Console.WriteLine(passed ? $"OK   {what}" : $"FAIL {what}");
        return passed ? 0 : 1;
    }
}
