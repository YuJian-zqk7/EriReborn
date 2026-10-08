using EriReborn.Core.Logging;

namespace EriReborn.Platform.Android;

/// <summary>
/// Android packages assets inside the APK, where they are not ordinary files.
/// On first run (or when the bundled data version changes) they are extracted
/// into the app sandbox so the rest of the application can keep using plain
/// paths (spec 17/18).
/// </summary>
public sealed class AndroidAssetProvisioner(IAppLogger log)
{
    private const string StampFileName = ".assets-version";
    private readonly IAppLogger _log = log;

    /// <summary>
    /// Extracts the packaged assets unless the ones already on disk came from this
    /// exact build.
    ///
    /// The version is derived from the packaged data rather than passed in. A
    /// hand-maintained constant is a step someone has to remember, and the time it
    /// gets forgotten the app silently runs on a stale catalog — which is worse
    /// than a crash, because everything still appears to work.
    /// </summary>
    public async Task<string> EnsureAssetsAsync(CancellationToken cancellationToken = default)
    {
        var context = global::Android.App.Application.Context;
        var target = Path.Combine(context.FilesDir!.AbsolutePath, "assets");
        var stamp = Path.Combine(target, StampFileName);
        var dataVersion = ComputeBundledVersion(context);

        if (File.Exists(stamp) && await File.ReadAllTextAsync(stamp, cancellationToken).ConfigureAwait(false) == dataVersion)
        {
            _log.Info("assets.provision", "Bundled assets already extracted for this version.");
            return target;
        }

        _log.Info("assets.provision", $"Extracting bundled assets into '{target}'.");

        // Extract into a clean directory: copying over an older, larger set would
        // leave orphaned files that no longer exist in this build.
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);

        // AssetManager is already rooted at the APK's assets/ directory, so the
        // traversal starts from the empty path, not from "assets".
        var copied = await CopyTreeAsync(context.Assets!, string.Empty, target, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(stamp, dataVersion, cancellationToken).ConfigureAwait(false);
        _log.Info("assets.provision", $"Extracted {copied} asset file(s).");
        return target;
    }

    /// <summary>
    /// App version plus the digests of the two files that change whenever the
    /// bundled data changes: the catalog signature covers every catalog file, and
    /// the asset manifest covers the art. Together they make the stamp exact.
    /// </summary>
    private string ComputeBundledVersion(global::Android.Content.Context context)
    {
        var parts = new List<string> { $"app={ReadVersionName(context)}" };

        foreach (var name in DigestTargets(context))
        {
            parts.Add($"{name}={DigestAsset(context, name)}");
        }

        return string.Join(";", parts);
    }

    private string ReadVersionName(global::Android.Content.Context context)
    {
        try
        {
            var info = context.PackageManager?.GetPackageInfo(
                context.PackageName!,
                (global::Android.Content.PM.PackageInfoFlags)0);

            // VersionName rather than LongVersionCode: the latter needs API 28 and
            // this application supports 24.
            return info?.VersionName ?? "?";
        }
        catch (Exception ex)
        {
            _log.Warn("assets.provision", $"无法读取应用版本：{ex.Message}");
            return "?";
        }
    }

    /// <summary>
    /// The bundled files whose content identifies the data set. The catalog
    /// signature covers every catalog file; the asset manifest covers the art;
    /// the persona and skin manifests cover the rest of the packaged data.
    /// </summary>
    private static IEnumerable<string> DigestTargets(global::Android.Content.Context context)
    {
        yield return "catalog/catalog.sig";
        yield return "asset_manifest.json";

        // The library index moves whenever art is re-imported, so the bundled-data
        // stamp has to see it or a device keeps a stale asset cache.
        yield return "library/index.json";

        foreach (var persona in context.Assets?.List("personas") ?? Array.Empty<string>())
        {
            if (persona.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"personas/{persona}";
            }
        }

        foreach (var skin in context.Assets?.List("skins") ?? Array.Empty<string>())
        {
            yield return $"skins/{skin}/skin.json";
        }
    }

    private static string DigestAsset(global::Android.Content.Context context, string name)
    {
        try
        {
            using var stream = context.Assets!.Open(name);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream))[..16];
        }
        catch (Exception)
        {
            // Not bundled in this build; the remaining parts still identify it.
            return "-";
        }
    }

    private async Task<int> CopyTreeAsync(global::Android.Content.Res.AssetManager assets, string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        var children = assets.List(sourcePath) ?? Array.Empty<string>();
        if (children.Length == 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childSource = sourcePath.Length == 0 ? child : $"{sourcePath}/{child}";
            var childTarget = Path.Combine(targetPath, child);
            var grandChildren = assets.List(childSource) ?? Array.Empty<string>();

            if (grandChildren.Length > 0)
            {
                Directory.CreateDirectory(childTarget);
                count += await CopyTreeAsync(assets, childSource, childTarget, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(childTarget)!);
            await using var input = assets.Open(childSource);
            await using var output = File.Create(childTarget);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            count++;
        }

        return count;
    }
}
