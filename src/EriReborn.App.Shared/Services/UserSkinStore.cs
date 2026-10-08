using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EriReborn.Core.Logging;
using EriReborn.Core.Validation;
using EriReborn.Skin;

namespace EriReborn.App.Shared.Services;

/// <summary>
/// The skins the user made, as real skins.
///
/// <para>
/// A user skin is a directory under the user data folder holding its own skin.json — the same
/// shape the shipped packs use. It is discovered like any other skin, it can be edited field by
/// field, and it survives a restart. The shipped packs are never written to.
/// </para>
///
/// <para>
/// Creating one copies the skin you were looking at, so the starting point is a working skin
/// rather than an empty form: you change the text and the pictures you care about and inherit
/// everything else.
/// </para>
/// </summary>
public sealed class UserSkinStore(AppPaths paths, IAppLogger log)
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,

        // The file is meant to be opened and edited by hand, so CJK text must stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AppPaths _paths = paths;
    private readonly IAppLogger _log = log;

    /// <summary>Root of the user's own skins; each skin is a subdirectory holding a skin.json.</summary>
    public string Directory => Path.Combine(_paths.UserDataDirectory, "skins");

    public string ManifestPath(string skinId) => Path.Combine(Directory, skinId, "skin.json");

    public bool IsUserSkin(string skinId) => File.Exists(ManifestPath(skinId));

    public IReadOnlyList<string> ListIds()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return Array.Empty<string>();
        }

        return System.IO.Directory.EnumerateDirectories(Directory)
            .Select(System.IO.Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id) && File.Exists(ManifestPath(id!)))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Writes a new skin that is built on the given one.
    ///
    /// <para>
    /// The file is thin on purpose: it names the base and lists only what the user changes, so
    /// every untouched colour, size and picture keeps coming from the pack. Copying the whole
    /// manifest instead would freeze a snapshot of the base and drift from it the moment the pack
    /// is updated — and it would make "restore the default" impossible to express.
    /// </para>
    /// </summary>
    public bool CreateFrom(SkinManifest baseSkin, string newId, string newName, string? persona, out string reason)
    {
        if (!DirectoryNameValidator.ValidateName(newId).IsValid)
        {
            reason = $"id '{newId}' 不是合法名字：只能是小写 ASCII、无空格（它会成为文件夹名）。";
            return false;
        }

        if (IsUserSkin(newId))
        {
            reason = $"你已经有一套叫 '{newId}' 的皮肤了。";
            return false;
        }

        try
        {
            Write(newId, new JsonObject
            {
                ["id"] = newId,
                ["name"] = newName,
                ["baseSkin"] = baseSkin.Id,
                ["persona"] = persona,

                // Overrides live here, one key per changed slot. An empty object means "this skin
                // changes nothing yet", which is exactly what a fresh copy is.
                ["assets"] = new JsonObject(),
            });

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("skin.user", $"无法创建皮肤 {newId}", ex);
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Resolves every user skin into a complete manifest by laying it over its base skin.
    ///
    /// <para>
    /// Pure: it computes the skins that should exist, it does not register them. A user skin
    /// whose base is missing is reported rather than silently dropped, because a skin that
    /// disappears from the picker with no explanation is the worst version of this feature.
    /// </para>
    /// </summary>
    public IReadOnlyList<SkinManifest> Apply(IReadOnlyList<SkinManifest> available)
    {
        var merged = new List<SkinManifest>();

        foreach (var id in ListIds())
        {
            var path = ManifestPath(id);
            SkinManifest? file;

            try
            {
                file = SkinManifestParser.Parse(File.ReadAllText(path), path);
            }
            catch (Exception ex)
            {
                _log.Error("skin.user", $"用户皮肤 '{id}' 读不出来。", ex);
                continue;
            }

            var baseSkin = available.FirstOrDefault(skin => string.Equals(skin.Id, file.BaseSkin, StringComparison.Ordinal));
            if (baseSkin is null)
            {
                _log.Warn("skin.user", $"用户皮肤 '{file.Id}' 的基础皮肤 '{file.BaseSkin}' 不存在，已跳过。");
                continue;
            }

            merged.Add(baseSkin with
            {
                Id = file.Id,
                Name = string.IsNullOrWhiteSpace(file.Name) ? file.Id : file.Name,
                Persona = string.IsNullOrWhiteSpace(file.Persona) ? baseSkin.Persona : file.Persona,
                BaseSkin = baseSkin.Id,
                SourceFile = path,

                // The user's own art pack, when they replaced a picture. The base's relative path
                // would mean nothing from the user's folder, so it is never inherited.
                AssetManifestFile = file.AssetManifestFile,
                Assets = Overlay(baseSkin.Assets, file.Assets),
                Colors = Overlay(baseSkin.Colors, file.Colors),
                Metrics = Overlay(baseSkin.Metrics, file.Metrics),

                // The interface's words overlay exactly like colours do. Leaving this out meant a
                // skin could declare "this button says 参数" and the interface would still say 设置:
                // the file was written, read back by the editor, and then dropped on the way in.
                Texts = Overlay(baseSkin.Texts, file.Texts),

                // The pointers overlay the same way, and for the same reason: without this the editor's
                // cursor rows wrote a declaration that the merged skin never carried, so a skin could dress
                // every role it liked and the app kept the base pack's pointers (spec 112).
                Cursors = Overlay(baseSkin.Cursors, file.Cursors),
            });
        }

        return merged;
    }

    /// <summary>
    /// Lays overrides over a base map, key by key.
    ///
    /// <para>
    /// A key the user did not touch keeps the base's value. A key set to nothing is removed,
    /// which is how "restore the default" is expressed: delete the override and the base's
    /// picture or colour comes back.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> Overlay(
        IReadOnlyDictionary<string, string> baseValues,
        IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.Count == 0)
        {
            return baseValues;
        }

        var result = new Dictionary<string, string>(baseValues, StringComparer.Ordinal);
        foreach (var (key, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                result.Remove(key);
            }
            else
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Rewrites the text of one of the user's own skins.</summary>
    public bool SaveText(string skinId, string name, string? persona)
        => Mutate(skinId, json =>
        {
            json["name"] = name;
            json["persona"] = persona;
        });

    /// <summary>Points one of the user's asset slots at a sheet id, or clears it.</summary>
    public bool SetSlot(string skinId, string slot, string? assetId)
        => Mutate(skinId, json =>
        {
            var assets = json["assets"] as JsonObject ?? new JsonObject();
            json["assets"] = assets;

            if (string.IsNullOrWhiteSpace(assetId))
            {
                assets.Remove(slot);
            }
            else
            {
                assets[slot] = assetId;
            }
        });

    /// <summary>
    /// Replaces one interface word for the user's own skin, or clears it.
    ///
    /// <para>
    /// Clearing is how "恢复默认" is expressed: the key is deleted and the shipped wording comes
    /// back, exactly as it does for colours and pictures.
    /// </para>
    /// </summary>
    public bool SetText(string skinId, string key, string? value)
        => SetInSection(skinId, "texts", key, value);

    /// <summary>Replaces one colour of the user's own skin, or clears it back to the base's.</summary>
    public bool SetColor(string skinId, string key, string? value)
        => SetInSection(skinId, "colors", key, value);

    /// <summary>
    /// Writes one key of one section, or removes it. Every override the editor offers works this
    /// way, because "restore the default" is the same act for a colour, a word or a picture: delete
    /// the key and the base's value comes back.
    /// </summary>
    private bool SetInSection(string skinId, string section, string key, string? value)
        => Mutate(skinId, json =>
        {
            var map = json[section] as JsonObject ?? new JsonObject();
            json[section] = map;

            if (string.IsNullOrWhiteSpace(value))
            {
                map.Remove(key);
            }
            else
            {
                map[key] = value;
            }
        });

    /// <summary>
    /// The interface words this skin declares, read from its own file rather than from the merged
    /// view: the editor needs to know which ones the user changed, and only those have a default to
    /// go back to.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReadTextOverrides(string skinId)
        => ReadSection(skinId, "texts");

    /// <summary>
    /// The pointers this skin declares, role name → "wait" or "asset:sheet_id".
    ///
    /// <para>
    /// Read from the skin's own file, like every other override: "this skin declares a hand" and "the pack
    /// under it happens to" are different statements, and only the first one is this skin's to clear.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> ReadCursors(string skinId)
        => ReadSection(skinId, "cursors");

    /// <summary>Declares one pointer role, or clears it back to the base skin's.</summary>
    public bool SetCursor(string skinId, string roleName, string? spec)
        => SetInSection(skinId, "cursors", roleName, spec);

    /// <summary>The colours this skin replaces, read from its own file rather than the merged view.</summary>
    public IReadOnlyDictionary<string, string> ReadColorOverrides(string skinId)
        => ReadSection(skinId, "colors");

    private IReadOnlyDictionary<string, string> ReadSection(string skinId, string section)
    {
        try
        {
            var path = ManifestPath(skinId);
            if (!File.Exists(path)
                || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json
                || json[section] is not JsonObject map)
            {
                return new Dictionary<string, string>();
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in map)
            {
                result[key] = value?.GetValue<string>() ?? string.Empty;
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.Warn("skin.user", $"读不出皮肤 '{skinId}' 的 {section}：{ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Replaces any one picture the interface draws, not just the five skin slots.
    ///
    /// <para>
    /// The shipped library keeps its own ids — a skin may add pictures, not redefine what an id
    /// means for everyone else — so the user's file is declared under an id of their own and the
    /// skin's <c>assets</c> map says which name now means it. That is the same map the five slots
    /// already use, so a click on the settings icon and a click on the character slot take the same
    /// path.
    /// </para>
    /// </summary>
    public bool ReplaceAsset(string skinId, string assetId, string sourcePath, out string reason)
    {
        reason = string.Empty;

        if (!IsUserSkin(skinId))
        {
            reason = "只能替换你自己皮肤里的图片；随包皮肤是只读的。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(assetId))
        {
            reason = "这个元素没有可替换的图片名字。";
            return false;
        }

        if (!File.Exists(sourcePath))
        {
            reason = $"找不到这个文件：{sourcePath}";
            return false;
        }

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            reason = "这个文件没有扩展名，认不出是不是图片。";
            return false;
        }

        if (extension.TrimStart('.').Any(ch => !char.IsAsciiLetterOrDigit(ch)))
        {
            reason = $"认不出这个扩展名：{extension}";
            return false;
        }

        try
        {
            var assetsDirectory = Path.Combine(Directory, skinId, "assets");
            System.IO.Directory.CreateDirectory(assetsDirectory);

            // The id becomes a file name, so it is sanitised: a name like "../../system32" would
            // otherwise write outside the skin's own folder.
            var safe = new string(assetId
                .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')
                .ToArray());

            if (safe.Length == 0)
            {
                reason = "这个图片名字没法当成文件名。";
                return false;
            }

            var fileName = safe + extension.ToLowerInvariant();
            File.Copy(sourcePath, Path.Combine(assetsDirectory, fileName), overwrite: true);

            var ownId = skinId + "_" + safe;
            UpsertSheet(skinId, ownId, "assets/" + fileName);

            return Mutate(skinId, json =>
            {
                var assets = json["assets"] as JsonObject ?? new JsonObject();
                json["assets"] = assets;
                assets[assetId] = ownId;
                json["assetManifestFile"] = "asset_manifest.json";
            });
        }
        catch (Exception ex)
        {
            _log.Error("skin.user", $"无法替换图片 {assetId}", ex);
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Drops a replacement, so the picture the base skin ships comes back.
    ///
    /// <para>
    /// The copied file is left on disk: a user who replaces a picture, looks at it, and restores the
    /// default has not asked for their file to be deleted, and the skin's own sheet declaration is
    /// harmless once nothing points at it.
    /// </para>
    /// </summary>
    public bool RestoreAsset(string skinId, string assetId)
        => Mutate(skinId, json =>
        {
            if (json["assets"] is JsonObject assets)
            {
                assets.Remove(assetId);
            }
        });

    /// <summary>
    /// Throws away every override and leaves the skin as pure inheritance.
    ///
    /// <para>
    /// The pictures the user copied in stay on disk: this is "forget my changes", not "delete my
    /// files" — a skin that declares nothing simply uses the base's pictures again, and a user who
    /// resets and changes their mind has lost nothing they cannot re-pick.
    /// </para>
    /// </summary>
    public bool ResetOverrides(string skinId)
    {
        if (!IsUserSkin(skinId))
        {
            return false;
        }

        try
        {
            var existing = JsonNode.Parse(File.ReadAllText(ManifestPath(skinId))) as JsonObject ?? new JsonObject();

            var trimmed = new JsonObject
            {
                ["id"] = existing["id"]?.DeepClone() ?? skinId,
                ["name"] = existing["name"]?.DeepClone() ?? skinId,
            };

            // What a skin is, as opposed to what it changes: which pack it builds on, the voice it
            // is read in, and (when set) its theme and layout.
            foreach (var key in new[] { "baseSkin", "persona", "theme", "layout" })
            {
                if (existing[key] is { } value)
                {
                    trimmed[key] = value.DeepClone();
                }
            }

            Write(skinId, trimmed);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("skin.user", $"无法重置皮肤 {skinId}", ex);
            return false;
        }
    }

    public bool Delete(string skinId)
    {
        try
        {
            var directory = Path.Combine(Directory, skinId);
            if (!System.IO.Directory.Exists(directory))
            {
                return false;
            }

            System.IO.Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("skin.user", $"无法删除皮肤 {skinId}", ex);
            return false;
        }
    }

    /// <summary>
    /// Copies a picture the user chose into their skin and points one slot at it.
    ///
    /// <para>
    /// The file is copied in rather than referenced where it was picked from: a skin that pointed
    /// at the user's Downloads folder would break the moment they moved the original. It is then
    /// declared in the skin's own asset manifest, because the shipped library is read-only and a
    /// picture that is not in it cannot be named by an id.
    /// </para>
    /// </summary>
    public bool ImportArt(string skinId, string slot, string sourcePath, out string reason)
    {
        reason = string.Empty;

        if (!IsUserSkin(skinId))
        {
            reason = "只能替换你自己皮肤里的图片；随包皮肤是只读的。";
            return false;
        }

        if (!File.Exists(sourcePath))
        {
            reason = $"找不到这个文件：{sourcePath}";
            return false;
        }

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            reason = "这个文件没有扩展名，认不出是不是图片。";
            return false;
        }

        try
        {
            var assetsDirectory = Path.Combine(Directory, skinId, "assets");
            System.IO.Directory.CreateDirectory(assetsDirectory);

            var fileName = slot + extension.ToLowerInvariant();
            File.Copy(sourcePath, Path.Combine(assetsDirectory, fileName), overwrite: true);

            var assetId = skinId + "_" + slot;
            UpsertSheet(skinId, assetId, "assets/" + fileName);

            return Mutate(skinId, json =>
            {
                var assets = json["assets"] as JsonObject ?? new JsonObject();
                json["assets"] = assets;
                assets[slot] = assetId;

                // The skin now carries art of its own, so it has to say where that pack is.
                json["assetManifestFile"] = "asset_manifest.json";
            });
        }
        catch (Exception ex)
        {
            _log.Error("skin.user", $"无法导入图片 {sourcePath}", ex);
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Copies one of the user's own pictures into this skin and registers it as a sheet, so a pointer
    /// role can point at it as <c>asset:&lt;id&gt;</c>.
    ///
    /// <para>
    /// Kept apart from <see cref="ImportArt"/>, which also writes the id into the skin's asset slots:
    /// a cursor is not a slot, and which role points at the new picture is the caller's decision.
    /// </para>
    /// </summary>
    public bool ImportCursorArt(string skinId, string roleName, string sourcePath, out string assetId, out string reason)
    {
        assetId = string.Empty;
        reason = string.Empty;

        if (!IsUserSkin(skinId))
        {
            reason = "只能改你自己皮肤里的鼠标指针；随包皮肤是只读的。";
            return false;
        }

        if (!File.Exists(sourcePath))
        {
            reason = $"找不到这个文件：{sourcePath}";
            return false;
        }

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            reason = "这个文件没有扩展名，认不出是不是图片。";
            return false;
        }

        try
        {
            var assetsDirectory = Path.Combine(Directory, skinId, "assets");
            System.IO.Directory.CreateDirectory(assetsDirectory);

            // One file per role, so replacing the picture for "busy" never disturbs "text".
            var slot = "cursor_" + roleName;
            var fileName = slot + extension.ToLowerInvariant();
            File.Copy(sourcePath, Path.Combine(assetsDirectory, fileName), overwrite: true);

            assetId = skinId + "_" + slot;
            UpsertSheet(skinId, assetId, "assets/" + fileName);

            // The skin now carries art of its own, so it has to say where that pack is.
            Mutate(skinId, json => json["assetManifestFile"] = "asset_manifest.json");

            return true;
        }
        catch (Exception ex)
        {
            _log.Error("skin.user", $"无法导入指针图片 {sourcePath}", ex);
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// What this skin overrides, straight from its own file rather than from the merged view.
    ///
    /// <para>
    /// The difference matters to the editor: "this slot points at a shipped picture" and "this slot
    /// was changed by the user" are different statements, and only the second one has something to
    /// restore.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> ReadOverrides(string skinId)
    {
        try
        {
            var path = ManifestPath(skinId);
            if (!File.Exists(path)
                || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json
                || json["assets"] is not JsonObject assets)
            {
                return new Dictionary<string, string>();
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in assets)
            {
                result[key] = value?.GetValue<string>() ?? string.Empty;
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.Warn("skin.user", $"读不出皮肤 '{skinId}' 的覆盖：{ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// The absolute path of a picture this skin declared, or null when the id is not one of its own.
    ///
    /// <para>
    /// The app only registers the art of the skin that is in force. The workshop previews the skin
    /// it is editing, which is often not that one, so the file is found from the skin's own manifest
    /// rather than through the asset registry.
    /// </para>
    /// </summary>
    public string? ResolveArtPath(string skinId, string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(Directory, skinId);
            var path = Path.Combine(directory, "asset_manifest.json");

            if (!File.Exists(path)
                || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json
                || json["sheets"] is not JsonArray sheets)
            {
                return null;
            }

            foreach (var entry in sheets)
            {
                if (entry is JsonObject sheet
                    && string.Equals(sheet["id"]?.GetValue<string>(), assetId, StringComparison.Ordinal)
                    && sheet["file"]?.GetValue<string>() is { Length: > 0 } relative)
                {
                    var resolved = Path.GetFullPath(
                        Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));

                    return File.Exists(resolved) ? resolved : null;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.Warn("skin.user", $"读不出皮肤 '{skinId}' 的图片清单：{ex.Message}");
            return null;
        }
    }

    /// <summary>Adds or updates this skin's declaration of one sheet.</summary>
    private void UpsertSheet(string skinId, string assetId, string relativeFile)
    {
        var path = Path.Combine(Directory, skinId, "asset_manifest.json");
        var root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing
            ? existing
            : new JsonObject { ["schema"] = 2 };

        var sheet = new JsonObject
        {
            ["id"] = assetId,
            ["skin"] = skinId,
            ["platform"] = "all",
            ["type"] = "image",
            ["file"] = relativeFile,
        };

        if (root["sheets"] is not JsonArray sheets)
        {
            sheets = new JsonArray();
            root["sheets"] = sheets;
        }

        // Replaced rather than appended: the parser takes the first entry for an id, so an earlier
        // declaration of the same slot would silently win over the picture just chosen.
        for (var index = 0; index < sheets.Count; index++)
        {
            if (sheets[index] is JsonObject item
                && string.Equals(item["id"]?.GetValue<string>(), assetId, StringComparison.Ordinal))
            {
                sheets[index] = sheet;
                File.WriteAllText(path, root.ToJsonString(WriteOptions));
                return;
            }
        }

        sheets.Add(sheet);
        File.WriteAllText(path, root.ToJsonString(WriteOptions));
    }

    /// <summary>
    /// Reads, changes and writes the manifest back. Parsing the file rather than rebuilding it
    /// means a field the author added by hand survives an edit made here.
    /// </summary>
    private bool Mutate(string skinId, Action<JsonObject> change)
    {
        try
        {
            var path = ManifestPath(skinId);
            if (!File.Exists(path))
            {
                return false;
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
            {
                return false;
            }

            change(json);
            Write(skinId, json);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("skin.user", $"无法修改皮肤 {skinId}", ex);
            return false;
        }
    }

    private void Write(string skinId, JsonObject json)
    {
        var path = ManifestPath(skinId);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json.ToJsonString(WriteOptions));
    }

}
