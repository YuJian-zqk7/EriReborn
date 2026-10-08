using EriReborn.Core.Logging;
using System.IO.Compression;

namespace EriReborn.Extension;

/// <summary>
/// Scans the extensions directory, validates every manifest, resolves declared
/// dependencies, loads what it can and reports a real state per extension
/// (spec 34/36). This is exactly what the "Refresh Extensions" action runs.
/// </summary>
public sealed class ExtensionRegistry(
    ExtensionValidator validator,
    IsolatedExtensionLoader loader,
    IAppLogger log,
    Func<ExtensionHostLaunch>? locateHost = null) : IExtensionLifecycle
{
    private readonly Func<ExtensionHostLaunch> _locateHost =
        locateHost ?? (() => ExtensionHostLocator.Locate(AppContext.BaseDirectory));

    private readonly List<ExtensionStatus> _statuses = new();

    /// <summary>
    /// Raised after the status list changed: a full rescan, an enable/disable toggle or an install
    /// finishing. The extensions page is a singleton built once at shell construction, so without
    /// this it kept showing what it found at startup forever — an extension installed from the
    /// marketplace (or dropped into the folder and rescanned elsewhere) only appeared after the
    /// user manually pressed refresh, which read as a stuck "no extensions" page.
    /// </summary>
    public event Action? StatusesChanged;

    private void RaiseStatusesChanged()
    {
        try
        {
            StatusesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            log.Warn("extension.event", $"A StatusesChanged subscriber threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Completes deferred uninstalls. Returns the ids whose files are still in
    /// use, so the scan can skip them.
    /// </summary>
    private HashSet<string> CleanupPendingRemovals(string extensionsRoot)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(extensionsRoot))
        {
            return pending;
        }

        foreach (var marker in Directory.EnumerateFiles(extensionsRoot, "*.pending-delete", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(marker);
            var directory = Path.Combine(extensionsRoot, id);

            if (DirectoryRemoval.TryDelete(directory))
            {
                try
                {
                    File.Delete(marker);
                }
                catch (IOException)
                {
                    // The marker will be retried next time.
                }

                log.Info("extension.pending", $"Completed deferred removal of '{id}'.");
            }
            else
            {
                pending.Add(id);
                log.Warn("extension.pending", $"'{id}' is still mapped; removal stays deferred.");
            }
        }

        return pending;
    }

    /// <summary>Unpacks every <c>*.zip</c> in the extensions directory into a private
    /// <c>_extracted/&lt;id&gt;__&lt;revision&gt;</c> folder, so a refresh is cheap for an
    /// unchanged zip.
    ///
    /// <para>
    /// The folder name encodes the zip's own size and timestamp, and the presence of
    /// <c>manifest.json</c> in it is the whole freshness test. It must <b>not</b> compare the
    /// unpacked manifest's timestamp with the zip's: extraction preserves the archive entries'
    /// timestamps, which are routinely older than the zip file, so that comparison never
    /// passes, the folder is deleted and rebuilt on every refresh, and the delete half-succeeds
    /// once the app has mapped the extension assembly — leaving a manifest-less folder and an
    /// extension that silently disappears (spec 34/36).</para>
    ///
    /// <para>
    /// If the primary folder cannot be rebuilt because a live process still holds it, the
    /// archive is unpacked into a sibling <c>_locked</c> folder instead of being given up on.
    /// The legacy bare <c>_extracted/&lt;id&gt;</c> folder is cleaned up best-effort.</para>
    /// </summary>
    private static void ExtractExtensionZips(string extensionsRoot, IAppLogger log)
    {
        var extractedRoot = Path.Combine(extensionsRoot, "_extracted");
        try
        {
            Directory.CreateDirectory(extractedRoot);
        }
        catch (Exception ex)
        {
            log.Warn("extension.zip", $"无法创建扩展解压目录：{ex.Message}");
            return;
        }

        foreach (var zip in Directory.EnumerateFiles(extensionsRoot, "*.zip", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(zip);
            var revision = RevisionFolder(zip);
            var primary = Path.Combine(extractedRoot, revision);

            string active;
            if (File.Exists(Path.Combine(primary, "manifest.json")))
            {
                active = primary;
            }
            else if (TryExtract(zip, primary, log))
            {
                active = primary;
            }
            else
            {
                // A previous run may still hold the primary folder's assemblies, so
                // deleting it fails and leaves a manifest-less folder behind. Unpack into
                // a sibling so the extension stays visible until the lock goes away.
                var fallback = Path.Combine(extractedRoot, revision + "_locked");
                active = File.Exists(Path.Combine(fallback, "manifest.json")) || TryExtract(zip, fallback, log)
                    ? fallback
                    : primary;
            }

            // Housekeeping: the bare-id folder older builds used, and any revision other
            // than the one just settled on. Failing to remove them is expected while a live
            // process holds them and is not an error.
            TryRemoveDirectory(Path.Combine(extractedRoot, id), log);
            RemoveOlderRevisions(extractedRoot, id, Path.GetFileName(active), log);
        }
    }

    /// <summary>A folder name that changes exactly when the zip does, without reading it.</summary>
    private static string RevisionFolder(string zip)
    {
        var info = new FileInfo(zip);
        return $"{Path.GetFileNameWithoutExtension(zip)}__{info.Length}_{info.LastWriteTimeUtc.Ticks}";
    }

    private static bool TryExtract(string zip, string target, IAppLogger log)
    {
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            ZipFile.ExtractToDirectory(zip, target);
            log.Info("extension.zip", $"解压扩展包：{Path.GetFileNameWithoutExtension(zip)} -> {Path.GetFileName(target)}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn("extension.zip", $"解压扩展包 '{Path.GetFileNameWithoutExtension(zip)}' 失败：{ex.Message}");
            return false;
        }
    }

    private static void RemoveOlderRevisions(string extractedRoot, string id, string keep, IAppLogger log)
    {
        var prefix = id + "__";
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(extractedRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(name, keep, StringComparison.Ordinal))
                {
                    TryRemoveDirectory(directory, log);
                }
            }
        }
        catch (Exception ex)
        {
            log.Info("extension.zip", $"遍历旧解压目录失败：{ex.Message}");
        }
    }

    private static void TryRemoveDirectory(string directory, IAppLogger log)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // Locked by a live process: harmless, the scan ignores the leftovers.
            log.Info("extension.zip", $"暂不能清理旧解压目录 '{Path.GetFileName(directory)}'：{ex.Message}");
        }
    }

    public IReadOnlyList<ExtensionStatus> Statuses => _statuses;

    /// <summary>
    /// Forgets an extension and releases its load context. Both steps matter:
    /// while any <see cref="ExtensionStatus"/> holds the instance the assembly
    /// stays mapped and its files cannot be deleted (spec 37).
    /// </summary>
    public bool Unload(string extensionId)
    {
        foreach (var status in _statuses.Where(s => string.Equals(s.Manifest.Id, extensionId, StringComparison.Ordinal)).ToList())
        {
            StopProcess(status);
        }

        var removed = _statuses.RemoveAll(s => string.Equals(s.Manifest.Id, extensionId, StringComparison.Ordinal)) > 0;
        loader.Unload(extensionId);

        if (removed)
        {
            log.Info("extension.release", $"Released extension '{extensionId}' from the registry.");
        }

        return removed;
    }

    /// <summary>
    /// Switches one extension off without touching unrelated ones: its load
    /// context is released and its entry becomes <see cref="ExtensionLoadState.Disabled"/>
    /// (spec 34). Anything that depends on it is released too, because it can no
    /// longer run.
    /// </summary>
    public ExtensionStatus? Disable(string extensionId)
    {
        var index = _statuses.FindIndex(s => string.Equals(s.Manifest.Id, extensionId, StringComparison.Ordinal));
        if (index < 0)
        {
            log.Warn("extension.disable", $"Extension '{extensionId}' is not in the registry.");
            return null;
        }

        var manifest = _statuses[index].Manifest;
        if (_statuses[index].State == ExtensionLoadState.Disabled)
        {
            return _statuses[index];
        }

        // Release first: while the instance is held the assembly stays mapped.
        StopProcess(_statuses[index]);
        loader.Unload(extensionId);
        var disabled = new ExtensionStatus(manifest, ExtensionLoadState.Disabled, "已被用户禁用。");
        _statuses[index] = disabled;

        log.Info("extension.disable", $"Extension '{extensionId}' was disabled and its load context released.");
        ReleaseDependents(extensionId);
        RaiseStatusesChanged();
        return disabled;
    }

    /// <summary>
    /// Switches one extension back on by loading it into a fresh context.
    ///
    /// <para>
    /// Kept for callers that cannot await. Isolated extensions need the async form:
    /// starting a process and completing its handshake is genuinely asynchronous, and
    /// blocking a UI thread on it is how a slow extension becomes a frozen window.
    /// </para>
    /// </summary>
    public ExtensionStatus? Enable(string extensionId, IExtensionHost host)
        => EnableAsync(extensionId, host).GetAwaiter().GetResult();

    /// <summary>Switches one extension back on, starting a process if it asked for one.</summary>
    public async Task<ExtensionStatus?> EnableAsync(string extensionId, IExtensionHost host, CancellationToken cancellationToken = default)
    {
        var index = _statuses.FindIndex(s => string.Equals(s.Manifest.Id, extensionId, StringComparison.Ordinal));
        if (index < 0)
        {
            log.Warn("extension.enable", $"Extension '{extensionId}' is not in the registry.");
            return null;
        }

        if (_statuses[index].State != ExtensionLoadState.Disabled)
        {
            return _statuses[index];
        }

        // Refuse to enable something whose own dependencies are still missing.
        var manifest = _statuses[index].Manifest;
        var blocker = FindUnmetDependency(manifest);
        if (blocker is not null)
        {
            var blocked = new ExtensionStatus(
                manifest,
                ExtensionLoadState.DependencyMissing,
                $"依赖的扩展 '{blocker}' 无法加载。");

            _statuses[index] = blocked;
            log.Warn("extension.dependency", $"'{extensionId}' was not enabled: {blocked.Message}");
            RaiseStatusesChanged();
            return blocked;
        }

        var loaded = await LoadOneAsync(manifest, host, cancellationToken).ConfigureAwait(false);
        _statuses[index] = loaded;

        log.Info("extension.enable", $"Extension '{extensionId}' was enabled with state {loaded.State}.");
        RaiseStatusesChanged();
        return loaded;
    }

    /// <summary>
    /// Loads an extension the way its manifest asked for.
    ///
    /// <para>
    /// An extension that requested its own process is <b>never</b> quietly loaded
    /// in-process instead: that would hand back exactly the containment the author
    /// asked for and the user was shown. If the host is not shipped, the honest
    /// answer is that the extension cannot run.
    /// </para>
    /// </summary>
    private async Task<ExtensionStatus> LoadOneAsync(ExtensionManifest manifest, IExtensionHost host, CancellationToken cancellationToken = default)
    {
        if (!manifest.RunsInOwnProcess)
        {
            return loader.Load(manifest, host);
        }

        var launch = _locateHost();
        if (!launch.IsRunnable)
        {
            log.Warn("extension.isolation", $"'{manifest.Id}' 要求独立进程，但随包没有扩展宿主。");
            return new ExtensionStatus(
                manifest,
                ExtensionLoadState.LoadFailed,
                "该扩展要求独立进程运行，但随包没有扩展宿主（EriReborn.Extension.Host）。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            return new ExtensionStatus(manifest, ExtensionLoadState.Rejected, "Manifest does not name an assembly.");
        }

        var assemblyPath = Path.GetFullPath(Path.Combine(manifest.Directory, manifest.Assembly));
        if (!File.Exists(assemblyPath))
        {
            return new ExtensionStatus(manifest, ExtensionLoadState.AssemblyMissing, $"Assembly not found: {assemblyPath}");
        }

        try
        {
            var runner = await OutOfProcessExtensionRunner.StartAsync(
                launch.Executable,
                assemblyPath,
                manifest,
                log: log,
                hostArguments: launch.Arguments,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new ExtensionStatus(
                manifest,
                ExtensionLoadState.Loaded,
                $"已在独立进程中运行（{runner.ExtensionId} v{runner.ExtensionVersion}）。",
                null,
                runner);
        }
        catch (Exception ex)
        {
            log.Error("extension.isolation", $"'{manifest.Id}' 的独立进程启动失败。", ex);
            return new ExtensionStatus(manifest, ExtensionLoadState.LoadFailed, ex.Message);
        }
    }

    /// <summary>Stops an isolated extension's process, if it has one.</summary>
    private void StopProcess(ExtensionStatus? status)
    {
        if (status?.Process is { } runner)
        {
            try
            {
                runner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Warn("extension.isolation", $"释放 '{status.Manifest.Id}' 的进程时出错：{ex.Message}");
            }
        }
    }

    public string? RootDirectory { get; private set; }

    public async Task<IReadOnlyList<ExtensionStatus>> RefreshAsync(
        string extensionsRoot,
        IExtensionHost host,
        IReadOnlyCollection<string>? disabledIds = null,
        CancellationToken cancellationToken = default)
    {
        RootDirectory = extensionsRoot;

        // Forgetting a status does not stop a process: without this, refreshing
        // would leak one child per isolated extension, every time.
        foreach (var status in _statuses)
        {
            StopProcess(status);
        }

        loader.UnloadAll();
        _statuses.Clear();
        var disabled = disabledIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(disabledIds, StringComparer.Ordinal);

        // Finish any uninstall whose files were still mapped last time, and make
        // sure a pending extension is never loaded again.
        var pendingRemoval = CleanupPendingRemovals(extensionsRoot);

        if (!Directory.Exists(extensionsRoot))
        {
            log.Warn("extension.refresh", $"Extensions directory not found: {extensionsRoot}");
            RaiseStatusesChanged();
            return _statuses;
        }

        // A distributed extension is a single .zip sitting in this directory. Each is
        // unpacked into a private "_extracted/<id>" folder so the rest of the loader
        // keeps seeing unpacked folders, exactly what it already does for extensions
        // installed as folders. This is what lets one extension be one file here, which
        // is what Creative Workshop and the GitHub release ship.
        ExtractExtensionZips(extensionsRoot, log);

        var reported = new List<ExtensionStatus>();
        var discovered = new List<ExtensionManifest>();

        foreach (var manifestPath in Directory.EnumerateFiles(extensionsRoot, "manifest.json", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(manifestPath)!;

            if (pendingRemoval.Count > 0)
            {
                var firstSegment = Path.GetRelativePath(extensionsRoot, directory)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .FirstOrDefault();
                if (firstSegment is not null && pendingRemoval.Contains(firstSegment))
                {
                    log.Warn("extension.pending", $"Skipping '{firstSegment}': removal is pending.");
                    continue;
                }
            }

            ExtensionManifest? manifest;
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                manifest = ExtensionManifestParser.Parse(json, directory, manifestPath);
            }
            catch (Exception ex)
            {
                log.Error("extension.manifest", $"Failed to read manifest '{manifestPath}'.", ex);
                continue;
            }

            if (manifest is null)
            {
                log.Warn("extension.manifest", $"Manifest '{manifestPath}' has no id and was skipped.");
                continue;
            }

            discovered.Add(manifest);
        }

        // The same id can be present more than once: a stale unpacked folder that a live
        // process still holds can sit next to the current one. The newer manifest is the
        // one the shipped zip carries, so it wins; the older copy is dropped here instead
        // of being reported as a second entry with the same id.
        static DateTime ManifestTimestamp(ExtensionManifest manifest)
        {
            try
            {
                return manifest.ManifestPath is { } path ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        var valid = new List<ExtensionManifest>();
        foreach (var manifest in discovered
                     .GroupBy(m => m.Id, StringComparer.Ordinal)
                     .Select(g => g.OrderByDescending(ManifestTimestamp).First())
                     .OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var validation = validator.Validate(manifest);
            if (!validation.IsValid)
            {
                log.Warn("extension.rejected", $"Extension '{manifest.Id}' rejected: {validation.Describe()}");
                reported.Add(new ExtensionStatus(manifest, ExtensionLoadState.Rejected, validation.Describe()));
                continue;
            }

            valid.Add(manifest);

            if (disabled.Contains(manifest.Id))
            {
                // Deliberately not loaded: the user switched it off (spec 34).
                log.Info("extension.disabled", $"Extension '{manifest.Id}' is disabled and was not loaded.");
                reported.Add(new ExtensionStatus(manifest, ExtensionLoadState.Disabled, "已被用户禁用。"));
            }
        }

        // Dependencies decide the order, and which extensions cannot load at all.
        var resolution = ExtensionDependencyResolver.Resolve(valid, disabled);

        foreach (var id in resolution.LoadOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = valid.First(m => string.Equals(m.Id, id, StringComparison.Ordinal));
            reported.Add(await LoadOneAsync(manifest, host, cancellationToken).ConfigureAwait(false));
        }

        foreach (var (id, reason) in resolution.Blocked)
        {
            var manifest = valid.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
            if (manifest is null || disabled.Contains(id))
            {
                // A dependency that is simply absent, or one the user switched off.
                continue;
            }

            log.Warn("extension.dependency", $"Extension '{id}' was not loaded: {reason}");
            reported.Add(new ExtensionStatus(manifest, ExtensionLoadState.DependencyMissing, reason));
        }

        _statuses.AddRange(reported.OrderBy(s => s.Manifest.Id, StringComparer.Ordinal));

        var loadedCount = _statuses.Count(s => s.IsUsable);
        var off = _statuses.Count(s => s.State == ExtensionLoadState.Disabled);
        var unmet = _statuses.Count(s => s.State == ExtensionLoadState.DependencyMissing);
        log.Info(
            "extension.refresh",
            $"Scanned {_statuses.Count} extension(s); {loadedCount} loaded, {off} disabled, {unmet} blocked by dependencies.");
        RaiseStatusesChanged();
        return _statuses;
    }

    /// <summary>Returns the id of the first dependency that cannot be satisfied.</summary>
    private string? FindUnmetDependency(ExtensionManifest manifest)
    {
        foreach (var dependency in manifest.Dependencies.Distinct(StringComparer.Ordinal))
        {
            var status = _statuses.FirstOrDefault(s => string.Equals(s.Manifest.Id, dependency, StringComparison.Ordinal));
            if (status is null || !status.IsUsable)
            {
                return dependency;
            }
        }

        return null;
    }

    /// <summary>
    /// Releases every loaded extension that transitively depends on the one just
    /// switched off; leaving them loaded would break them at run time.
    /// </summary>
    private void ReleaseDependents(string dependencyId)
    {
        var byId = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
        foreach (var status in _statuses)
        {
            byId[status.Manifest.Id] = status.Manifest;
        }

        foreach (var candidate in _statuses.ToList())
        {
            if (candidate.State != ExtensionLoadState.Loaded)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal) { dependencyId };
            if (!DependsOn(candidate.Manifest, dependencyId, byId, seen))
            {
                continue;
            }

            loader.Unload(candidate.Manifest.Id);
            var replacement = new ExtensionStatus(
                candidate.Manifest,
                ExtensionLoadState.DependencyMissing,
                $"依赖的扩展 '{dependencyId}' 已被禁用。");

            var index = _statuses.FindIndex(s => string.Equals(s.Manifest.Id, candidate.Manifest.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _statuses[index] = replacement;
            }

            log.Warn("extension.dependency", $"'{candidate.Manifest.Id}' was released because '{dependencyId}' is disabled.");
        }
    }

    private static bool DependsOn(
        ExtensionManifest manifest,
        string targetId,
        IReadOnlyDictionary<string, ExtensionManifest> byId,
        HashSet<string> seen)
    {
        foreach (var dependency in manifest.Dependencies)
        {
            if (string.Equals(dependency, targetId, StringComparison.Ordinal))
            {
                return true;
            }

            if (!seen.Add(dependency))
            {
                continue;
            }

            if (byId.TryGetValue(dependency, out var next) && DependsOn(next, targetId, byId, seen))
            {
                return true;
            }
        }

        return false;
    }
}
