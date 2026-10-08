namespace EriReborn.Skin;

/// <summary>
/// What the pointer is telling the user, independent of any UI framework.
///
/// <para>
/// Views ask for a role, never for a shape: "something is happening" rather than
/// "the wait cursor". That is what lets a skin decide how its own busy pointer
/// looks without every view knowing about it (spec 112).
/// </para>
/// </summary>
public enum CursorRole
{
    Default,
    Busy,
    Drag,
    Forbidden,
    ResizeHorizontal,
    ResizeVertical,
    ResizeCorner,
    Link,
    Text,
    Crosshair,
}

/// <summary>Names for the roles, and the only place they are spelled.</summary>
public static class CursorRoles
{
    public const string DefaultName = "default";
    public const string BusyName = "busy";
    public const string DragName = "drag";
    public const string ForbiddenName = "forbidden";
    public const string ResizeHorizontalName = "resizeHorizontal";
    public const string ResizeVerticalName = "resizeVertical";
    public const string ResizeCornerName = "resizeCorner";
    public const string LinkName = "link";
    public const string TextName = "text";
    public const string CrosshairName = "crosshair";

    public static readonly IReadOnlyList<CursorRole> All = new[]
    {
        CursorRole.Default, CursorRole.Busy, CursorRole.Drag, CursorRole.Forbidden,
        CursorRole.ResizeHorizontal, CursorRole.ResizeVertical, CursorRole.ResizeCorner,
        CursorRole.Link, CursorRole.Text, CursorRole.Crosshair,
    };

    /// <summary>
    /// The standard shapes a skin may name instead of pointing at its own art.
    ///
    /// <para>
    /// The names live here, in the skin layer, because they are what a <c>skin.json</c> is allowed to say —
    /// and because the editor has to offer exactly the same list. Which platform pointer each one maps to is
    /// the UI layer's business (see <c>CursorManager</c>), so this is the single list of names and no second
    /// copy exists to drift away from it.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> KnownKeywords = new[]
    {
        "arrow", "wait", "appstarting", "hand", "no", "sizeall",
        "sizewe", "sizens", "corner", "ibeam", "cross", "none",
    };

    public static string NameOf(CursorRole role) => role switch
    {
        CursorRole.Default => DefaultName,
        CursorRole.Busy => BusyName,
        CursorRole.Drag => DragName,
        CursorRole.Forbidden => ForbiddenName,
        CursorRole.ResizeHorizontal => ResizeHorizontalName,
        CursorRole.ResizeVertical => ResizeVerticalName,
        CursorRole.ResizeCorner => ResizeCornerName,
        CursorRole.Link => LinkName,
        CursorRole.Text => TextName,
        CursorRole.Crosshair => CrosshairName,
        _ => DefaultName,
    };

    /// <summary>Case-insensitive, because skin authors should not have to guess.</summary>
    public static bool TryParse(string? name, out CursorRole role)
    {
        role = CursorRole.Default;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();

        foreach (var candidate in All)
        {
            if (string.Equals(NameOf(candidate), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        return false;
    }
}

/// <summary>What a skin declared for one role.</summary>
public sealed record CursorSpec(CursorRole Role, string? Keyword, string? AssetId)
{
    /// <summary>Marks a declaration that points at art rather than a standard shape.</summary>
    public const string AssetPrefix = "asset:";

    public bool IsCustomArt => AssetId is { Length: > 0 };

    /// <summary>
    /// Reads one declaration. An empty value is not a declaration — it is the
    /// absence of one, and returning a spec for it would override the default with
    /// nothing.
    /// </summary>
    public static CursorSpec? Parse(CursorRole role, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var assetId = trimmed[AssetPrefix.Length..].Trim();
            return assetId.Length == 0 ? null : new CursorSpec(role, null, assetId);
        }

        return new CursorSpec(role, trimmed.ToLowerInvariant(), null);
    }
}

/// <summary>Reads the cursor section of a skin.</summary>
public static class SkinCursors
{
    /// <summary>
    /// The roles a skin declared, plus any role name it invented.
    ///
    /// <para>
    /// Unknown names are reported rather than dropped: a typo like
    /// <c>resizecorner</c> would otherwise look exactly like a skin that simply
    /// chose not to override anything.
    /// </para>
    /// </summary>
    public static (IReadOnlyDictionary<CursorRole, CursorSpec> Declared, IReadOnlyList<string> Unknown) Resolve(SkinManifest skin)
    {
        ArgumentNullException.ThrowIfNull(skin);

        var declared = new Dictionary<CursorRole, CursorSpec>();
        var unknown = new List<string>();

        foreach (var (name, value) in skin.Cursors)
        {
            if (!CursorRoles.TryParse(name, out var role))
            {
                unknown.Add(name);
                continue;
            }

            if (CursorSpec.Parse(role, value) is { } spec)
            {
                declared[role] = spec;
            }
        }

        return (declared, unknown);
    }
}
