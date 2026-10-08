using System.Text.Json;
using EriReborn.Core.Logging;
using EriReborn.Core.Paths;
using EriReborn.Core.Validation;
using EriReborn.Skin;

namespace EriReborn.App.Shared.Services;

/// <summary>
/// The text a user changed on one skin: what it is called, and how it speaks.
/// </summary>
/// <param name="Id">The skin this text belongs to, or the new one it brings into being.</param>
/// <param name="BaseId">The shipped skin a new id is built on; null when an existing skin is edited.</param>
/// <param name="Name">The name the skin shows.</param>
/// <param name="Persona">The persona the skin speaks with.</param>
public sealed record SkinTextOverride(string Id, string? BaseId, string Name, string? Persona);

/// <summary>
/// The skin text the workshop changed, kept beside the app's own data.
///
/// <para>
/// The skins themselves ship inside the read-only asset tree, so an edit cannot be written into
/// them. An override is a layer over them instead: it names a skin and carries only the text that
/// was changed, while everything else — colours, sizes, pictures, typography — keeps coming from
/// the pack it is layered on.
/// </para>
///
/// <para>
/// Deleting one is what puts the shipped text back, so the layer has to be removable rather than
/// baked into the manifests it produced.
/// </para>
/// </summary>
public sealed class SkinOverrideStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly IAppLogger _log;
    private readonly Dictionary<string, SkinTextOverride> _overrides = new(StringComparer.Ordinal);

    public SkinOverrideStore(AppPaths paths, IAppLogger log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _filePath = Path.Combine(paths.UserDataDirectory, "skin-overrides.json");
        _log = log;
        Load();
    }

    /// <summary>Where the overrides live, so a page can say where a change was written.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Re-reads the overrides from disk.
    ///
    /// <para>
    /// An override is written by whichever store instance the workshop holds, while the host keeps
    /// the one it built at startup. Without this, the host's copy would go on handing back the list
    /// it read when the app started, and an edit made through another instance would look as if it
    /// had never happened — including a deletion, which would leave the removed text on screen.
    /// Re-reading is what makes "the file is the truth" hold for every instance.
    /// </para>
    /// </summary>
    public void Reload()
    {
        _overrides.Clear();
        Load();
    }

    /// <summary>True when this skin carries text of the user's own.</summary>
    public bool IsOverridden(string id)
        => !string.IsNullOrWhiteSpace(id) && _overrides.ContainsKey(id);

    /// <summary>
    /// Writes one override and keeps it.
    ///
    /// <para>
    /// An id that could not be a file name is refused rather than reshaped into one: a silent
    /// rename would file the change under a name the user did not choose.
    /// </para>
    /// </summary>
    public bool Save(SkinTextOverride over)
    {
        ArgumentNullException.ThrowIfNull(over);

        var verdict = DirectoryNameValidator.ValidateName(over.Id);
        if (!verdict.IsValid)
        {
            _log.Warn("skin.override", $"皮肤 id '{over.Id}' 不能作为文件名，已拒绝。");
            return false;
        }

        _overrides[over.Id] = over;
        return Persist();
    }

    /// <summary>Drops one override, so the shipped text comes back.</summary>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_overrides.Remove(id))
        {
            return false;
        }

        return Persist();
    }

    /// <summary>
    /// The skins that carry an override, and only those: this layer's own output.
    ///
    /// <para>
    /// Returning the whole list would make a second pass derive the same skin twice, because the
    /// derived one is already in what it is handed back. Returning just what this layer produced
    /// keeps reloading idempotent, which is what a reload on every change needs.
    /// </para>
    /// </summary>
    public IReadOnlyList<SkinManifest> Apply(IReadOnlyList<SkinManifest> shipped)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        var result = new List<SkinManifest>();

        foreach (var over in _overrides.Values)
        {
            var existing = shipped.FirstOrDefault(skin => string.Equals(skin.Id, over.Id, StringComparison.Ordinal));
            if (existing is not null)
            {
                // Same id: the text is rewritten and the rest of the pack is left alone.
                result.Add(existing with { Name = over.Name, Persona = over.Persona });
                continue;
            }

            var baseSkin = over.BaseId is null
                ? null
                : shipped.FirstOrDefault(skin => string.Equals(skin.Id, over.BaseId, StringComparison.Ordinal));

            if (baseSkin is null)
            {
                _log.Warn(
                    "skin.override",
                    $"皮肤覆盖 '{over.Id}' 找不到基准皮肤 '{over.BaseId ?? "(未指定)"}'，已跳过。");
                continue;
            }

            // A new id borrows the whole base and changes only the text, so renaming a skin never
            // asks the user to author an entire one.
            result.Add(baseSkin with
            {
                Id = over.Id,
                Name = over.Name,
                Persona = over.Persona,
                BaseSkin = baseSkin.Id,
            });
        }

        return result;
    }

    /// <returns>True when the file reached the disk.</returns>
    private bool Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_overrides, WriteOptions));
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("skin.override", $"皮肤覆盖无法写入：{ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Reads the overrides back. A damaged file is reported and treated as no overrides rather
    /// than taking the skin list down with it.
    /// </summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, SkinTextOverride>>(
                File.ReadAllText(_filePath),
                ReadOptions);

            if (map is null)
            {
                return;
            }

            foreach (var (id, over) in map)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _overrides[id] = over;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("skin.override", $"皮肤覆盖无法读取，按没有覆盖处理：{ex.Message}", ex);
        }
    }
}
