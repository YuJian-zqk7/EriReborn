using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Core.Tests.TestSupport;

/// <summary>
/// A controllable platform double. Used to prove Core behaves correctly when a
/// capability is genuinely unavailable (spec 6/68) and to build the app host
/// without launching a window.
/// </summary>
public sealed class TestPlatform(
    IFileSystemService files,
    INetworkService network,
    ICredentialStore credentials,
    string platformId = "test") : IPlatformService
{
    public string PlatformId => platformId;

    public PlatformCapabilities Capabilities { get; init; } = new(true, true, true, true, credentials.IsAvailable, false, true);

    /// <summary>
    /// Settable so a test can describe a machine whose facts are unavailable —
    /// "unknown" is a real state and has to be reachable.
    /// </summary>
    public ISystemInfoService SystemInfo { get; init; } = new TestSystemInfo();

    public ISoftwareDetector Detector { get; init; } = new UnavailableDetector();

    public ISoftwareInstaller Installer { get; init; } = new UnavailableInstaller();

    public ISoftwareUpdater Updater { get; init; } = new UnavailableUpdater();

    public IProcessService Processes { get; init; } = new UnavailableProcessService();

    public IFileSystemService Files { get; } = files;

    public ICredentialStore Credentials { get; } = credentials;

    public INetworkService Network { get; } = network;

    public IWindowService Windows { get; } = new UnavailableWindowService();

    /// <summary>A platform with no file manager: opening a folder reports why, not a crash.</summary>
    public IShellService Shell { get; init; } = new UnavailableShellService();

    public IAudioService Audio { get; init; } = new NoopAudioService();

    /// <summary>
    /// Settable so a test can describe a phone with a status bar, and so the
    /// desktop default of "nothing reserved" stays the baseline.
    /// </summary>
    public ISafeAreaService SafeArea { get; init; } = new NoInsetsSafeAreaService();

    public IAppLogger Log { get; init; } = AppLog.For("Test");

    private sealed class TestSystemInfo : ISystemInfoService
    {
        public SystemInfo GetSystemInfo() => new("test", "TestOS", "1.0", "X64", "machine", "user", 1024, 4, true);
    }

    /// <summary>A machine whose memory reading is genuinely unavailable.</summary>
    public sealed class UnreadableSystemInfo : ISystemInfoService
    {
        public SystemInfo GetSystemInfo() => new("test", "TestOS", "1.0", "X64", "machine", "user", 0, 4, true);
    }

    private sealed class UnavailableDetector : ISoftwareDetector
    {
        public Task<DetectionResult> DetectAsync(SoftwareDefinition software, CancellationToken cancellationToken = default)
            => Task.FromResult(DetectionResult.Unsupported("test platform has no detector"));
    }

    private sealed class UnavailableInstaller : ISoftwareInstaller
    {
        public Task<InstallResult> InstallAsync(InstallRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new InstallResult(InstallState.PlatformUnavailable, "test platform cannot install"));
    }

    private sealed class UnavailableUpdater : ISoftwareUpdater
    {
        public Task<UpdateResult> UpdateAsync(SoftwareDefinition software, SoftwareSource source, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateResult(UpdateState.PlatformUnavailable, "test platform cannot update"));
    }

    private sealed class UnavailableProcessService : IProcessService
    {
        public bool IsSupported => false;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessResult(-1, string.Empty, "unsupported", false, false));

        public bool Exists(string fileName) => false;
    }

    private sealed class UnavailableWindowService : IWindowService
    {
        public bool IsSupported => false;

        public bool CanResize => false;

        public bool IsAttached => false;

        public void Attach(IWindowShellHost host) { }

        public void Minimize() { }

        public void ToggleMaximize() { }

        public void Restore() { }

        public void Close() { }

        public void SetTitle(string title) { }

        public void BeginDragMove() { }
    }

    private sealed class UnavailableShellService : IShellService
    {
        public string? OpenFolder(string path) => "test platform cannot open a folder";

        public string? OpenUrl(string url) => "test platform cannot open a link";
    }
}

/// <summary>In-memory credential store for tests.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool IsAvailable { get; init; } = true;

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_values.Remove(key));

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(_values.Keys.ToArray());
}

public sealed class TestNetworkService(HttpClient client) : INetworkService
{
    public HttpClient Client { get; } = client;
}
