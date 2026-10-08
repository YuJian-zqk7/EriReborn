using EriReborn.Core.Logging;
using EriReborn.Extension;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The process boundary, exercised end to end: a real child process, a real
/// extension assembly, real messages.
///
/// <para>
/// The claim being tested is containment, not security. A hung extension is timed
/// out and killed; a crashed request is a failed request, not a failed session; an
/// extension without a declared capability does not get it. That the child is still
/// an ordinary .NET process is a documented limit, not something these tests
/// pretend away.
/// </para>
/// </summary>
public sealed class OutOfProcessExtensionTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "EriReborn.Extension")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private static string HostExecutable() => Path.Combine(
        RepositoryRoot(), "tools", "EriReborn.Extension.Host", "bin", "Debug", "net8.0", "EriReborn.Extension.Host.exe");

    private static string SampleAssembly() => Path.Combine(AppContext.BaseDirectory, "EriReborn.SampleExtension.dll");

    private static ExtensionManifest Manifest(params string[] permissions) => new()
    {
        Id = "sample_service",
        Name = "Sample Service",
        Version = "1.0.0",
        Assembly = "EriReborn.SampleExtension.dll",
        Directory = ".",
        Permissions = permissions,
    };

    private static async Task<OutOfProcessExtensionRunner> StartAsync(params string[] permissions)
    {
        Assert.True(File.Exists(HostExecutable()), $"扩展宿主未构建：{HostExecutable()}");
        Assert.True(File.Exists(SampleAssembly()), $"示例扩展未构建：{SampleAssembly()}");

        return await OutOfProcessExtensionRunner.StartAsync(
            HostExecutable(),
            SampleAssembly(),
            Manifest(permissions),
            log: AppLog.For("Test"));
    }

    private static async Task<bool> WaitForExitAsync(OutOfProcessExtensionRunner runner, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (runner.HasExited)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return runner.HasExited;
    }

    [Fact]
    public async Task An_extension_runs_in_its_own_process_and_answers()
    {
        await using var runner = await StartAsync();

        Assert.Equal("sample_service", runner.ExtensionId);
        Assert.Equal("1.0.0", runner.ExtensionVersion);
        Assert.False(runner.HasExited);

        var response = await runner.SendAsync(new ExtensionRequest("greet", "Eri"));

        Assert.True(response.Ok, response.Error);
        Assert.Equal("Hello Eri", response.Result);
    }

    [Fact]
    public async Task The_extension_can_see_what_the_manifest_declared()
    {
        await using var runner = await StartAsync(ExtensionPermissions.SettingsRead);

        var response = await runner.SendAsync(new ExtensionRequest("whoami"));

        Assert.True(response.Ok, response.Error);
        Assert.Contains("sample_service", response.Result);
        Assert.Contains("settings.read", response.Result);
    }

    [Fact]
    public async Task A_declared_capability_works_across_the_boundary()
    {
        await using var runner = await StartAsync(
            ExtensionPermissions.SettingsRead,
            ExtensionPermissions.SettingsWrite);

        Assert.True((await runner.SendAsync(new ExtensionRequest("remember", "跨进程写入"))).Ok);

        var recall = await runner.SendAsync(new ExtensionRequest("recall"));
        Assert.True(recall.Ok, recall.Error);
        Assert.Equal("跨进程写入", recall.Result);
    }

    [Fact]
    public async Task An_undeclared_capability_is_refused_inside_the_child()
    {
        // No settings permission declared: the host in the child refuses, so the
        // value never becomes readable.
        await using var runner = await StartAsync();

        await runner.SendAsync(new ExtensionRequest("remember", "不该被记住"));

        var recall = await runner.SendAsync(new ExtensionRequest("recall"));
        Assert.True(recall.Ok, recall.Error);
        Assert.Equal("(none)", recall.Result);
    }

    [Fact]
    public async Task Settings_written_in_the_child_can_be_collected_by_the_parent()
    {
        await using var runner = await StartAsync(
            ExtensionPermissions.SettingsRead,
            ExtensionPermissions.SettingsWrite);

        await runner.SendAsync(new ExtensionRequest("remember", "留着"));

        var settings = await runner.DumpSettingsAsync();

        // Otherwise a write is lost the moment the process ends.
        Assert.Equal("留着", settings["note"]);
    }

    [Fact]
    public async Task A_failing_request_does_not_end_the_session()
    {
        await using var runner = await StartAsync();

        var failed = await runner.SendAsync(new ExtensionRequest("boom"));

        Assert.False(failed.Ok);
        Assert.Contains("故意失败", failed.Error);

        // A crash in the extension must not take the session down with it.
        Assert.False(runner.HasExited);

        var after = await runner.SendAsync(new ExtensionRequest("greet", "仍然在"));
        Assert.True(after.Ok, after.Error);
        Assert.Equal("Hello 仍然在", after.Result);
    }

    [Fact]
    public async Task An_unknown_command_is_reported_rather_than_ignored()
    {
        await using var runner = await StartAsync();

        var response = await runner.SendAsync(new ExtensionRequest("no-such-command"));

        Assert.False(response.Ok);
        Assert.Contains("未知命令", response.Error);
    }

    [Fact]
    public async Task A_hung_extension_is_timed_out_and_killed()
    {
        var runner = await StartAsync();
        try
        {
            // The extension blocks its turn for five seconds; the parent is told to
            // wait a fraction of that.
            var response = await runner.SendAsync(
                new ExtensionRequest("sleep", "5000"),
                timeout: TimeSpan.FromMilliseconds(400));

            Assert.False(response.Ok);
            Assert.Contains("期限内", response.Error);

            // Containment means the hung child is gone, not merely ignored.
            Assert.True(await WaitForExitAsync(runner), "超时后子进程仍在运行。");
        }
        finally
        {
            await runner.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disposing_stops_the_child_process()
    {
        var runner = await StartAsync();
        Assert.False(runner.HasExited);

        await runner.DisposeAsync();

        Assert.True(await WaitForExitAsync(runner), "释放后子进程仍在运行。");
    }

    [Fact]
    public async Task Requests_are_answered_in_turn()
    {
        await using var runner = await StartAsync();

        // Concurrent callers must not interleave on a one-turn-at-a-time wire.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(index => runner.SendAsync(new ExtensionRequest("greet", index.ToString()))));

        for (var index = 0; index < responses.Length; index++)
        {
            Assert.True(responses[index].Ok, responses[index].Error);
        }

        Assert.Equal(
            Enumerable.Range(0, 8).Select(index => $"Hello {index}").OrderBy(text => text),
            responses.Select(response => response.Result!).OrderBy(text => text));
    }
}
