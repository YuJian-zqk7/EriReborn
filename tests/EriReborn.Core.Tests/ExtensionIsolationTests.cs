using EriReborn.App.Shared.Services;
using EriReborn.Core.Logging;
using EriReborn.Extension;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// An extension that asked for its own process must get one — or be told it cannot
/// run. Quietly loading it in-process would hand back exactly the containment the
/// author asked for and the user was shown (spec 34/35).
/// </summary>
public sealed class ExtensionIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

    public ExtensionIsolationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

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

    private static ExtensionHostLaunch RealHost() => new(
        "dotnet",
        new[] { Path.Combine(RepositoryRoot(), "tools", "EriReborn.Extension.Host", "bin", "Debug", "net8.0", "EriReborn.Extension.Host.dll") });

    /// <summary>Writes an extensions tree holding one manifest and the sample service assembly.</summary>
    private string WriteExtension(string id, string isolation)
    {
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "EriReborn.SampleExtension.dll"),
            Path.Combine(directory, "EriReborn.SampleExtension.dll"),
            overwrite: true);

        File.WriteAllText(Path.Combine(directory, "manifest.json"), $$"""
        {
          "id": "{{id}}",
          "name": "Isolated Sample",
          "version": "1.0.0",
          "assembly": "EriReborn.SampleExtension.dll",
          "trust": "community",
          "isolation": "{{isolation}}",
          "permissions": [ "settings.read", "settings.write" ],
          "categories": [ "Utility" ]
        }
        """);

        return directory;
    }

    private ExtensionRegistry Registry(Func<ExtensionHostLaunch> locate)
        => new(new ExtensionValidator(), new IsolatedExtensionLoader(AppLog.For("Test")), AppLog.For("Test"), locate);

    // ------------------------------------------------------------ the model

    [Fact]
    public void An_extension_can_ask_for_its_own_process()
    {
        var manifest = ExtensionManifestParser.Parse("""
        { "id": "x", "name": "X", "isolation": "process" }
        """, ".", "m.json");

        Assert.NotNull(manifest);
        Assert.Equal(ExtensionIsolation.Process, manifest!.Isolation);
        Assert.True(manifest.RunsInOwnProcess);
    }

    [Fact]
    public void Isolation_is_opt_in()
    {
        var manifest = ExtensionManifestParser.Parse("""{ "id": "x", "name": "X" }""", ".", "m.json");

        // Isolation costs a process; nobody should pay for it by accident.
        Assert.Equal(ExtensionIsolation.InProcess, manifest!.Isolation);
        Assert.False(manifest.RunsInOwnProcess);
    }

    [Fact]
    public void A_misspelled_isolation_is_rejected_rather_than_read_as_in_process()
    {
        var manifest = ExtensionManifestParser.Parse("""{ "id": "x", "name": "X", "isolation": "proces" }""", ".", "m.json");

        var validation = new ExtensionValidator().Validate(manifest!);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "extension.unknown_isolation");
    }

    [Theory]
    [InlineData("inprocess")]
    [InlineData("in-process")]
    [InlineData("InProcess")]
    public void The_spellings_of_in_process_are_accepted(string value)
    {
        var manifest = ExtensionManifestParser.Parse(
            $$"""{ "id": "x", "name": "X", "assembly": "X.dll", "isolation": "{{value}}" }""",
            ".",
            "m.json");

        var validation = new ExtensionValidator().Validate(manifest!);

        // Asserting the isolation issue specifically: a manifest can be invalid for
        // a dozen other reasons, and "IsValid == false" would pass for the wrong one.
        Assert.DoesNotContain(validation.Issues, issue => issue.Code == "extension.unknown_isolation");
        Assert.True(validation.IsValid, validation.Describe());
    }

    // ------------------------------------------------------------ the locator

    [Fact]
    public void The_host_is_found_as_an_executable_next_to_the_app()
    {
        var directory = Path.Combine(_root, "exe");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "EriReborn.Extension.Host.exe"), "stub");

        var launch = ExtensionHostLocator.Locate(directory);

        Assert.True(launch.IsRunnable);
        Assert.Empty(launch.Arguments);
    }

    [Fact]
    public void A_published_layout_runs_the_host_through_the_shared_runtime()
    {
        var directory = Path.Combine(_root, "dll");
        Directory.CreateDirectory(directory);
        var assembly = Path.Combine(directory, "EriReborn.Extension.Host.dll");
        File.WriteAllText(assembly, "stub");

        var launch = ExtensionHostLocator.Locate(directory);

        // No apphost is generated for every platform, so the launcher must not assume one.
        Assert.True(launch.IsRunnable);
        Assert.Equal("dotnet", launch.Executable);
        Assert.Equal(new[] { assembly }, launch.Arguments);
    }

    [Fact]
    public void A_missing_host_is_reported_rather_than_guessed_at()
    {
        var launch = ExtensionHostLocator.Locate(Path.Combine(_root, "empty"));

        Assert.False(launch.IsRunnable);
    }

    // ----------------------------------------------------------- the registry

    [Fact]
    public async Task Without_a_host_an_isolated_extension_fails_instead_of_running_in_process()
    {
        WriteExtension("isolated_sample", "process");
        var registry = Registry(() => new ExtensionHostLaunch(string.Empty, Array.Empty<string>()));

        var statuses = await registry.RefreshAsync(_root, new AppExtensionHost(AppLog.For("Test")));

        var status = Assert.Single(statuses);
        Assert.Equal(ExtensionLoadState.LoadFailed, status.State);

        // The whole point: it must not end up Loaded with no process behind it.
        Assert.False(status.IsUsable);
        Assert.False(status.IsIsolated);
        Assert.Contains("独立进程", status.Message);
    }

    [Fact]
    public async Task With_a_host_an_isolated_extension_really_runs_in_another_process()
    {
        Assert.True(File.Exists(RealHost().Arguments[0]), "扩展宿主未构建。");

        WriteExtension("isolated_sample", "process");
        var registry = Registry(RealHost);

        var statuses = await registry.RefreshAsync(_root, new AppExtensionHost(AppLog.For("Test")));

        var status = Assert.Single(statuses);
        Assert.True(status.IsUsable, status.Message);
        Assert.True(status.IsIsolated);

        // No in-process instance exists for an isolated extension — holding one
        // would mean the assembly was loaded here after all.
        Assert.Null(status.Instance);
        Assert.False(status.Process!.HasExited);

        await status.Process!.SendAsync(new ExtensionRequest("greet", "隔离"));
    }

    [Fact]
    public async Task Disabling_an_isolated_extension_stops_its_process()
    {
        WriteExtension("isolated_sample", "process");
        var registry = Registry(RealHost);

        await registry.RefreshAsync(_root, new AppExtensionHost(AppLog.For("Test")));
        var runner = registry.Statuses.Single().Process!;

        var disabled = registry.Disable("isolated_sample");

        Assert.NotNull(disabled);
        Assert.Equal(ExtensionLoadState.Disabled, disabled!.State);

        var deadline = Environment.TickCount64 + 5000;
        while (!runner.HasExited && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }

        // A disabled extension with a live process behind it is not disabled.
        Assert.True(runner.HasExited, "禁用后子进程仍在运行。");
    }

    [Fact]
    public async Task Refreshing_does_not_leak_a_process_per_extension()
    {
        WriteExtension("isolated_sample", "process");
        var registry = Registry(RealHost);
        var host = new AppExtensionHost(AppLog.For("Test"));

        await registry.RefreshAsync(_root, host);
        var first = registry.Statuses.Single().Process!;

        // Re-scanning must stop the previous child, not just forget it.
        await registry.RefreshAsync(_root, host);

        var deadline = Environment.TickCount64 + 5000;
        while (!first.HasExited && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(first.HasExited, "刷新后旧子进程仍在运行。");
        Assert.NotSame(first, registry.Statuses.Single().Process);
    }

    [Fact]
    public async Task An_in_process_extension_is_unaffected_by_any_of_this()
    {
        // The sample also implements IExtension, so the default path still works and
        // still produces an instance rather than a process.
        var directory = WriteExtension("in_process_sample", "inprocess");

        File.WriteAllText(Path.Combine(directory, "manifest.json"), """
        {
          "id": "in_process_sample",
          "name": "In Process Sample",
          "version": "1.0.0",
          "assembly": "EriReborn.SampleExtension.dll",
          "trust": "community",
          "permissions": [ "settings.read", "settings.write" ],
          "categories": [ "Utility" ]
        }
        """);

        var registry = Registry(() => new ExtensionHostLaunch(string.Empty, Array.Empty<string>()));
        var statuses = await registry.RefreshAsync(_root, new AppExtensionHost(AppLog.For("Test")));

        var status = Assert.Single(statuses);
        Assert.True(status.IsUsable, status.Message);
        Assert.NotNull(status.Instance);
        Assert.False(status.IsIsolated);
    }
}
