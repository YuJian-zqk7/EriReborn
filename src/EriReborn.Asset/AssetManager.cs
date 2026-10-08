using EriReborn.Core.Logging;

namespace EriReborn.Asset;

/// <summary>
/// Single entry point for visual resources (spec 48). Missing assets are
/// reported and return null; the manager never invents a placeholder bitmap.
/// </summary>
public sealed class AssetManager(IAppLogger log)
{
    private readonly IAppLogger _log = log;
    private readonly Dictionary<string, AssetSheet> _byId = new(StringComparer.Ordinal);
    private readonly List<AssetSheet> _sheets = new();

    /// <summary>
    /// Sheets a skin brought, kept apart from the shipped ones so applying another skin can drop
    /// exactly these and nothing else.
    /// </summary>
    private readonly List<AssetSheet> _skinSheets = new();

    public string? RootDirectory { get; private set; }

    public IReadOnlyList<AssetSheet> Sheets => _sheets;

    public int MissingCount { get; private set; }

    /// <summary>
    /// Reads a manifest and makes it the registry.
    ///
    /// <para>
    /// <b>All or nothing.</b> The registry used to be cleared before the manifest
    /// was parsed, so a malformed file left the manager empty — and because this is
    /// a public method that can run again for a skin change or a reload, a second
    /// bad call would throw away a working set of assets. Nothing is replaced until
    /// the new manifest is known to be readable.
    /// </para>
    /// </summary>
    /// <returns>False when the manifest could not be read; the registry is then untouched.</returns>
    public bool LoadManifest(string json, string rootDirectory)
    {
        IReadOnlyList<AssetSheet> parsed;
        try
        {
            parsed = AssetManifestParser.Parse(json, rootDirectory);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Reported rather than thrown: every caller so far wraps this in a catch
            // that logs and carries on, which means the throw only ever bought a
            // single log line and an empty registry.
            _log.Error("asset.manifest", $"Asset manifest could not be parsed: {ex.Message}", ex);
            return false;
        }

        var byId = new Dictionary<string, AssetSheet>(StringComparer.Ordinal);
        var sheets = new List<AssetSheet>();

        foreach (var sheet in parsed)
        {
            if (byId.ContainsKey(sheet.Id))
            {
                _log.Warn("asset.duplicate", $"Duplicate asset id '{sheet.Id}' was ignored.");
                continue;
            }

            sheets.Add(sheet);
            byId[sheet.Id] = sheet;
        }

        // Swapped in one step, so the two collections can never disagree about which
        // sheets exist. The shipped library replaces everything, including any skin's own art:
        // this call has always meant "this is the registry now".
        _skinSheets.Clear();
        RootDirectory = rootDirectory;
        _byId.Clear();
        foreach (var pair in byId)
        {
            _byId[pair.Key] = pair.Value;
        }

        _sheets.Clear();
        _sheets.AddRange(sheets);

        _log.Info("asset.manifest", $"Registered {_sheets.Count} asset sheet(s) from '{rootDirectory}'.");
        return true;
    }

    /// <summary>
    /// Registers the art one skin brings with it, replacing whatever the previous skin brought.
    ///
    /// <para>
    /// Separate from <see cref="LoadManifest"/> on purpose: that one *is* the registry and clears
    /// it, which is right for the shipped library and catastrophic for a skin's own pictures —
    /// those are additions to it. This call swaps only the sheets a skin contributed, so applying
    /// a different skin cannot leave the previous skin's art behind under an id that now means
    /// something else.
    /// </para>
    /// </summary>
    /// <param name="manifestJson">The skin's own manifest, or null when it ships no art of its own.</param>
    /// <param name="rootDirectory">Directory its file paths are relative to.</param>
    public bool LoadSkinSheets(string? manifestJson, string? rootDirectory)
    {
        foreach (var sheet in _skinSheets)
        {
            _byId.Remove(sheet.Id);
            _sheets.Remove(sheet);
        }

        _skinSheets.Clear();

        if (string.IsNullOrWhiteSpace(manifestJson) || string.IsNullOrWhiteSpace(rootDirectory))
        {
            return true;
        }

        IReadOnlyList<AssetSheet> parsed;
        try
        {
            parsed = AssetManifestParser.Parse(manifestJson, rootDirectory);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _log.Error("asset.skin_manifest", $"皮肤自带的资源清单读不出来：{ex.Message}", ex);
            return false;
        }

        var added = 0;
        foreach (var sheet in parsed)
        {
            if (_byId.ContainsKey(sheet.Id))
            {
                // The shipped library keeps its ids: a skin may add pictures, not redefine what an
                // existing id means for everyone else.
                _log.Warn("asset.skin_duplicate", $"皮肤资源 '{sheet.Id}' 与已注册的 id 重名，已忽略。");
                continue;
            }

            _byId[sheet.Id] = sheet;
            _sheets.Add(sheet);
            _skinSheets.Add(sheet);
            added++;
        }

        _log.Info("asset.skin_manifest", $"皮肤自带的资源：新增 {added} 张（{rootDirectory}）。");
        return true;
    }

    /// <summary>
    /// The nine-slice frame a skin declared, if it declared one.
    ///
    /// <para>
    /// The skin decides how its containers are drawn, and a skin that shipped no
    /// frame art simply has none — the caller falls back to a flat border rather
    /// than stretching an unrelated sprite.
    /// </para>
    /// </summary>
    public AssetSheet? FrameFor(string? skinId)
        => string.IsNullOrWhiteSpace(skinId)
            ? null
            : _sheets.FirstOrDefault(sheet =>
                sheet.IsNineSlice && string.Equals(sheet.Skin, skinId, StringComparison.OrdinalIgnoreCase));

    public AssetSheet? Resolve(string id)
        => _byId.GetValueOrDefault(id);

    public IEnumerable<AssetSheet> ForSkin(string skin)
        => _sheets.Where(s => string.Equals(s.Skin, skin, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<AssetSheet> ForPlatform(string platform)
        => _sheets.Where(s =>
            string.Equals(s.Platform, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Platform, platform, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Absolute paths of a sheet's sliced pieces, in manifest order (spec 50).
    /// Element paths in the manifest are relative to the sheet's own directory,
    /// so several layouts are attempted before giving up.
    /// </summary>
    public IReadOnlyList<string> ResolveFrames(string id)
    {
        var sheet = Resolve(id);
        if (sheet is null)
        {
            // Asking for frames of an unknown id is a missing asset too.
            MissingCount++;
            _log.Warn("asset.missing", $"Asset '{id}' is not registered in the manifest.");
            return Array.Empty<string>();
        }

        if (sheet.Elements.Count == 0)
        {
            return Array.Empty<string>();
        }

        var root = RootDirectory;
        var slicedAbsolute = root is not null && sheet.SlicedDirectory is not null
            ? Path.GetFullPath(Path.Combine(root, sheet.SlicedDirectory.Replace('/', Path.DirectorySeparatorChar)))
            : null;
        var sheetAbsolute = sheet.ResolvedPath is not null
            ? Path.GetDirectoryName(sheet.ResolvedPath)
            : null;

        var frames = new List<string>();
        foreach (var element in sheet.Elements)
        {
            var relative = element.File.Replace('/', Path.DirectorySeparatorChar);
            var candidates = new[]
            {
                root is not null ? Path.GetFullPath(Path.Combine(root, relative)) : null,
                sheetAbsolute is not null ? Path.GetFullPath(Path.Combine(sheetAbsolute, relative)) : null,
                slicedAbsolute is not null ? Path.Combine(slicedAbsolute, Path.GetFileName(relative)) : null,
            };

            var found = candidates.FirstOrDefault(c => c is not null && File.Exists(c));
            if (found is null)
            {
                _log.Warn("asset.element-missing", $"Sliced element '{element.File}' of '{id}' was not found.");
                continue;
            }

            frames.Add(found);
        }

        return frames;
    }

    /// <summary>Returns the existing absolute path, or null with a logged warning.</summary>
    public string? ResolvePath(string id)
    {
        var sheet = Resolve(id);
        if (sheet is null)
        {
            MissingCount++;
            _log.Warn("asset.missing", $"Asset '{id}' is not registered in the manifest.");
            return null;
        }

        if (sheet.ResolvedPath is null || !File.Exists(sheet.ResolvedPath))
        {
            MissingCount++;
            _log.Warn("asset.missing", $"Asset '{id}' points at a file that does not exist: {sheet.ResolvedPath}");
            return null;
        }

        return sheet.ResolvedPath;
    }
}
