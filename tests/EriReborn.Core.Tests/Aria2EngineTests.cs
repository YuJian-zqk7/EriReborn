using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Engine.Download;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// aria2 is a second engine reached by a completely different route. The point
/// of it is that the software engine names no engine, so this one can arrive
/// without changing anything upstream (spec 61/136/137).
/// </summary>
public sealed class Aria2EngineTests
{
    private sealed class FakeProcesses(bool supported = true) : IProcessService
    {
        public bool IsSupported { get; } = supported;

        public List<ProcessRequest> Requests { get; } = new();

        public ProcessResult Next { get; set; } = new(0, string.Empty, string.Empty, false, true);

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Next);
        }

        public bool Exists(string fileName) => false;
    }

    private static DownloadRoute Route(string url = "https://example.invalid/tool.exe", string? referer = null)
        => new(DownloadRouteKind.ProviderDirect, url, Referer: referer, FileName: "tool.exe");

    // ------------------------------------------------------------ discovery

    [Fact]
    public void No_candidate_means_aria2_is_not_installed()
    {
        var locator = new Aria2Locator(_ => false, _ => Array.Empty<string>());

        Assert.Null(locator.Find());
    }

    [Fact]
    public void The_first_existing_candidate_wins()
    {
        var locator = new Aria2Locator(
            path => path == @"D:\tools\aria2c.exe",
            _ => Array.Empty<string>(),
            () => new[] { @"C:\absent\aria2c.exe", @"D:\tools\aria2c.exe" });

        // Order matters: an explicit override is looked at before PATH.
        Assert.Equal(@"D:\tools\aria2c.exe", locator.Find());
    }

    [Fact]
    public void Candidates_are_tried_in_the_order_they_are_given()
    {
        var locator = new Aria2Locator(
            _ => true,
            _ => Array.Empty<string>(),
            () => new[] { "first", "second" });

        Assert.Equal("first", locator.Find());
    }

    [Fact]
    public void A_versioned_package_folder_is_scanned_because_the_path_cannot_be_guessed()
    {
        // A package manager keeps each version in its own folder.
        var locator = new Aria2Locator(
            path => path == @"C:\pkg\aria2-1.37.0aria2c.exe",
            _ => new[] { @"C:\pkg\aria2-1.37.0aria2c.exe" });

        Assert.Equal(@"C:\pkg\aria2-1.37.0aria2c.exe", locator.Find());
    }

    [Fact]
    public void A_scan_that_fails_does_not_stop_discovery()
    {
        var locator = new Aria2Locator(
            _ => false,
            _ => throw new UnauthorizedAccessException(),
            () => Array.Empty<string>());

        // An unreadable package folder means "not found here", not a crash.
        Assert.Null(locator.Find());
    }

    // ------------------------------------------------------------ the engine

    [Fact]
    public void The_engine_declines_when_aria2_is_absent()
    {
        var engine = new Aria2DownloadEngine(
            new FakeProcesses(),
            new Aria2Locator(_ => false, _ => Array.Empty<string>()),
            AppLog.For("Test"));

        // Declining lets the selector fall back instead of reporting a failure.
        Assert.False(engine.CanHandle(Route()));
    }

    [Fact]
    public void The_engine_declines_a_route_that_needs_a_browser_session()
    {
        var engine = new Aria2DownloadEngine(
            new FakeProcesses(),
            new Aria2Locator(_ => true, _ => Array.Empty<string>()),
            AppLog.For("Test"));

        Assert.False(engine.CanHandle(new DownloadRoute(
            DownloadRouteKind.BrowserAssisted,
            "https://example.invalid/a",
            RequiresSession: true)));

        Assert.True(engine.CanHandle(Route()));
    }

    [Fact]
    public void The_engine_declines_when_the_platform_cannot_run_processes()
    {
        var engine = new Aria2DownloadEngine(
            new FakeProcesses(supported: false),
            new Aria2Locator(_ => true, _ => Array.Empty<string>()),
            AppLog.For("Test"));

        Assert.False(engine.CanHandle(Route()));
    }

    [Fact]
    public async Task A_missing_binary_is_reported_as_an_unavailable_engine()
    {
        var engine = new Aria2DownloadEngine(
            new FakeProcesses(),
            new Aria2Locator(_ => false, _ => Array.Empty<string>()),
            AppLog.For("Test"));

        var result = await engine.DownloadAsync(Route(), new DownloadRequest
        {
            Url = "https://example.invalid/tool.exe",
            DestinationPath = Path.Combine(Path.GetTempPath(), "aria2-missing", "tool.exe"),
        });

        // Distinct from a download failure: another engine can still do the work.
        Assert.Equal(DownloadState.EngineUnavailable, result.State);
    }

    // -------------------------------------------------------- the command line

    [Fact]
    public void The_command_line_pins_the_destination_and_forbids_surprises()
    {
        var arguments = Aria2DownloadEngine.BuildArguments(
            Route(), new DownloadRequest { Url = "https://example.invalid/tool.exe", DestinationPath = "x" },
            @"C:\dest", "tool.exe");

        Assert.Contains("--dir", arguments);
        Assert.Contains(@"C:\dest", arguments);
        Assert.Contains("--allow-overwrite=true", arguments);
        Assert.Contains("--auto-file-renaming=false", arguments);

        // Pre-allocating shows progress for bytes that have not arrived.
        Assert.Contains("--file-allocation=none", arguments);
        Assert.Equal("https://example.invalid/tool.exe", arguments[^1]);
    }

    [Fact]
    public void Route_headers_and_the_referer_travel_into_the_command_line()
    {
        var route = new DownloadRoute(
            DownloadRouteKind.Resolver,
            "https://example.invalid/tool.exe",
            Referer: "https://pan.example.invalid/share");

        var arguments = Aria2DownloadEngine.BuildArguments(
            route, new DownloadRequest { Url = route.Url, DestinationPath = "x" }, "d", "tool.exe");

        Assert.Contains("Referer: https://pan.example.invalid/share", arguments);
    }

    [Fact]
    public void A_declared_hash_is_checked_by_aria2_as_well_as_by_us()
    {
        var arguments = Aria2DownloadEngine.BuildArguments(
            Route(),
            new DownloadRequest { Url = "https://example.invalid/tool.exe", DestinationPath = "x", ExpectedSha256 = "ABCDEF" },
            "d",
            "tool.exe");

        Assert.Contains("--checksum", arguments);
        Assert.Contains("sha-256=abcdef", arguments);
    }

    [Theory]
    [InlineData("tool.exe", null, "tool.exe")]
    [InlineData(null, "from-route.exe", "from-route.exe")]
    public void The_file_name_comes_from_the_request_then_the_route(string? requestName, string? routeName, string expected)
    {
        var route = new DownloadRoute(DownloadRouteKind.ProviderDirect, "https://example.invalid/a", FileName: routeName);

        Assert.Equal(expected, Aria2DownloadEngine.ResolveFileName(route, new DownloadRequest
        {
            Url = route.Url,
            DestinationPath = "x",
            FileName = requestName,
        }));
    }

    [Fact]
    public void With_no_declared_name_the_url_supplies_one()
    {
        var route = new DownloadRoute(DownloadRouteKind.ProviderDirect, "https://example.invalid/path/setup.exe");

        Assert.Equal("setup.exe", Aria2DownloadEngine.ResolveFileName(route, new DownloadRequest
        {
            Url = route.Url,
            DestinationPath = "x",
        }));
    }
}
