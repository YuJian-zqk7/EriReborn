using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using EriReborn.Skin;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Turns a <see cref="CursorRole"/> into an actual pointer, honouring the active
/// skin.
///
/// <para>
/// Views ask for a role — "something is happening", "you can drag this" — and this
/// is the single place that decides what that looks like. It is also the only
/// place that knows a skin may override a role with its own art, so no view has to
/// (spec 112).
/// </para>
/// </summary>
public static class CursorManager
{
    private static readonly Dictionary<CursorRole, StandardCursorType> RoleDefaults = new()
    {
        [CursorRole.Default] = StandardCursorType.Arrow,
        [CursorRole.Busy] = StandardCursorType.Wait,
        [CursorRole.Drag] = StandardCursorType.SizeAll,
        [CursorRole.Forbidden] = StandardCursorType.No,
        [CursorRole.ResizeHorizontal] = StandardCursorType.SizeWestEast,
        [CursorRole.ResizeVertical] = StandardCursorType.SizeNorthSouth,
        [CursorRole.ResizeCorner] = StandardCursorType.BottomRightCorner,
        [CursorRole.Link] = StandardCursorType.Hand,
        [CursorRole.Text] = StandardCursorType.Ibeam,
        [CursorRole.Crosshair] = StandardCursorType.Cross,
    };

    /// <summary>
    /// The platform pointer for each name a skin may declare.
    ///
    /// <para>
    /// Built from <see cref="CursorRoles.KnownKeywords"/> rather than written out again: the names are the
    /// skin's vocabulary and the editor offers that same list, so a second copy here is a second thing to
    /// forget when one of them changes. A name with no platform shape maps to nothing and is ignored, which
    /// leaves the role at its default.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, StandardCursorType> Keywords = BuildKeywords();

    private static Dictionary<string, StandardCursorType> BuildKeywords()
    {
        var map = new Dictionary<string, StandardCursorType>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in CursorRoles.KnownKeywords)
        {
            if (ShapeFor(name) is { } type)
            {
                map[name] = type;
            }
        }

        return map;
    }

    private static StandardCursorType? ShapeFor(string name) => name switch
    {
        "arrow" => StandardCursorType.Arrow,
        "wait" => StandardCursorType.Wait,
        "appstarting" => StandardCursorType.AppStarting,
        "hand" => StandardCursorType.Hand,
        "no" => StandardCursorType.No,
        "sizeall" => StandardCursorType.SizeAll,
        "sizewe" => StandardCursorType.SizeWestEast,
        "sizens" => StandardCursorType.SizeNorthSouth,
        "corner" => StandardCursorType.BottomRightCorner,
        "ibeam" => StandardCursorType.Ibeam,
        "cross" => StandardCursorType.Cross,
        "none" => StandardCursorType.None,
        _ => null,
    };

    private static readonly Dictionary<(string Skin, CursorRole Role), Cursor> Cache = new();

    /// <summary>The keyword names a skin may declare, for documentation and for the tests.</summary>
    public static IReadOnlyCollection<string> KnownKeywords => Keywords.Keys;

    public static StandardCursorType DefaultTypeFor(CursorRole role)
        => RoleDefaults.TryGetValue(role, out var type) ? type : StandardCursorType.Arrow;

    /// <summary>The platform pointer for a role, ignoring every skin.</summary>
    public static Cursor Standard(CursorRole role) => new(DefaultTypeFor(role));

    /// <summary>
    /// The pointer the active skin wants. A skin that declared nothing for the role
    /// gets the platform default: an unset cursor is not a reason to show nothing.
    /// </summary>
    public static Cursor Resolve(SkinManifest? skin, CursorRole role)
    {
        var key = (skin?.Id ?? string.Empty, role);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = Build(skin, role);
        Cache[key] = resolved;
        return resolved;
    }

    private static Cursor Build(SkinManifest? skin, CursorRole role)
    {
        if (skin is null)
        {
            return Standard(role);
        }

        var (declared, unknown) = SkinCursors.Resolve(skin);

        foreach (var name in unknown)
        {
            // Logged rather than dropped: a typo otherwise looks identical to a skin
            // that simply chose not to override anything.
            App.Host?.Log.Warn("skin.cursor_unknown", $"皮肤 '{skin.Id}' 声明了未知的光标角色 '{name}'。");
        }

        if (!declared.TryGetValue(role, out var spec))
        {
            return Standard(role);
        }

        if (spec.IsCustomArt)
        {
            var path = App.Host?.Assets.ResolvePath(spec.AssetId!);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try
                {
                    // Avalonia 11 has no Cursor(Stream): a .cur cannot be handed over whole,
                    // so only a bitmap is usable here and the hotspot comes from the role.
                    var custom = new Cursor(new Bitmap(path), HotspotFor(role));
                    App.Host?.Log.Info("skin.cursor_custom", $"皮肤 '{skin.Id}' 的 {CursorRoles.NameOf(role)} 用 '{spec.AssetId}'");
                    return custom;
                }
                catch (Exception)
                {
                    // Unreadable art is missing art; fall through to the default.
                }
            }
        }

        if (spec.Keyword is { Length: > 0 } keyword && Keywords.TryGetValue(keyword, out var type))
        {
            return new Cursor(type);
        }

        return Standard(role);
    }

    /// <summary>
    /// Where the tip of a custom pointer sits, so it points at what it acts on.
    ///
    /// The art is 48x48. A hotspot read off the wrong size lands the click tens of pixels
    /// away from where the user aimed: the hand's tip is its fingertip (top-left), and a
    /// resize arrow acts from its middle.
    /// </summary>
    public static PixelPoint HotspotFor(CursorRole role) => role switch
    {
        // 19,2 is the hotspot the material's hand.cur declares for itself; the fingertip is
        // not where a guess would put it.
        CursorRole.Link => new PixelPoint(19, 2),
        CursorRole.Text => new PixelPoint(24, 4),
        CursorRole.ResizeHorizontal or CursorRole.ResizeVertical or CursorRole.Drag
            => new PixelPoint(24, 24),
        CursorRole.Crosshair => new PixelPoint(24, 24),
        CursorRole.ResizeCorner => new PixelPoint(40, 40),
        _ => new PixelPoint(0, 0),
    };
}
