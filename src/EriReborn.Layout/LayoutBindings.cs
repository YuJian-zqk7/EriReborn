using System.Windows.Input;

namespace EriReborn.Layout;

/// <summary>
/// Supplies live data and actions to a schema layout.
///
/// A node's <c>Binding</c> names either text (<c>text:MachineName</c>), an action
/// (<c>action:scan</c>), a list (<c>items:CategoryBreakdown</c>) or a numeric
/// value (<c>value:ScanPercent</c>). The document owns the layout, the host owns
/// the data — which is what lets a page be rearranged without editing XAML, and
/// keeps values that would go stale out of the saved document.
/// </summary>
public sealed record LayoutBindings(
    Func<string, string?> Text,
    IReadOnlyDictionary<string, ICommand> Actions,
    Func<string, IReadOnlyList<LayoutBoundItem>>? Items = null,
    Func<string, double?>? Value = null)
{
    public static LayoutBindings Empty { get; } = new(
        _ => null,
        new Dictionary<string, ICommand>(StringComparer.Ordinal));

    /// <summary>
    /// Follows a node's <c>link</c> and says whether it led anywhere: a page key goes to that page, an
    /// address goes to the browser.
    ///
    /// <para>
    /// Null — the default, and what the editor passes on purpose — means links here are only what they
    /// look like: a hand cursor and a tooltip. Arranging a page and using it are different jobs, and a
    /// link that navigated while the user was positioning it would take the page they were editing away
    /// from them.
    /// </para>
    /// </summary>
    public Func<string, bool>? FollowLink { get; init; }

    /// <summary>Splits "kind:name" into its parts; null when there is no binding.</summary>
    public static (string Kind, string Name)? Parse(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            return null;
        }

        var separator = binding.IndexOf(':');
        return separator <= 0
            ? null
            : (binding[..separator].Trim(), binding[(separator + 1)..].Trim());
    }
}

/// <summary>One row produced by an <c>items:</c> binding.</summary>
public sealed record LayoutBoundItem(string Label, string Value);
