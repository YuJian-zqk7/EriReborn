namespace EriReborn.App.Shared.Services;

/// <summary>One entry in the left navigation, possibly nested (spec 55).</summary>
public sealed record NavigationItem(
    string Key,
    string Title,
    string? ParentKey = null,
    int Order = 0,
    string? FilterValue = null)
{
    public IReadOnlyList<NavigationItem> Children { get; init; } = Array.Empty<NavigationItem>();

    /// <summary>
    /// The interface word this entry's label is resolved from, when it has one. Carried on the item
    /// so the skin editor can offer the entry's own word for editing instead of guessing it from the
    /// key, which is not the same string for nested entries.
    /// </summary>
    public string? TextKey { get; init; }

    /// <summary>Stable editable-element id for this entry's icon.</summary>
    public string SkinIconElementId => "nav." + Key + ".icon";

    /// <summary>Stable editable-element id for this entry's label.</summary>
    public string SkinTextElementId => "nav." + Key + ".text";

    /// <summary>
    /// The library icon for this page, or null when it has none. Binding the art to
    /// the page key keeps the mapping in one place instead of in every view, and a
    /// page with no icon draws nothing rather than a placeholder.
    /// </summary>
    public string? IconId => Key switch
    {
        "home" => "icon_detect",
        "software" => "icon_download",
        "environment" => "icon_runtime",
        "cloud" => "icon_cloud",
        "ai" => "icon_ai",
        "extensions" => "icon_extension",
        "marketplace" => "mkt_symbol",
        "plugins" => "icon_plugin",
        "tutorial" => "icon_tutorial",
        "jobs" => "icon_loading",
        "workshop" => "icon_skin",
        "settings" => "icon_settings",
        "update" => "icon_update",
        "blog" => "icon_info",
        _ => null,
    };
}

/// <summary>
/// Holds the navigation model and the current page key. The skin decides how
/// it is drawn; the view models only state which page is active (spec 56).
/// </summary>
public sealed class NavigationService
{
    /// <summary>
    /// How far back the trail is kept. Android's back gesture is user-driven and
    /// can be pressed indefinitely; an unbounded stack would grow for as long as
    /// the app is open.
    /// </summary>
    public const int MaxHistory = 32;

    private readonly List<string> _history = new();

    public event EventHandler<string>? Navigated;

    public string CurrentKey { get; private set; } = "home";

    /// <summary>True when back would go somewhere rather than leave the app.</summary>
    public bool CanGoBack => _history.Count > 0;

    public int HistoryDepth => _history.Count;

    public IReadOnlyList<string> History => _history;

    public void Navigate(string key)
    {
        if (string.Equals(CurrentKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _history.Add(CurrentKey);
        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
        }

        CurrentKey = key;
        Navigated?.Invoke(this, key);
    }

    /// <summary>
    /// Walks one step back. Returns false when there is nowhere to go, so the
    /// <b>caller</b> decides what back means — on Android that is leaving the app,
    /// and only the platform can do that. Answering it here would be this class
    /// deciding an operating system's behaviour.
    /// </summary>
    public bool TryGoBack()
    {
        if (_history.Count == 0)
        {
            return false;
        }

        var previous = _history[^1];
        _history.RemoveAt(_history.Count - 1);

        CurrentKey = previous;
        Navigated?.Invoke(this, previous);
        return true;
    }

    /// <summary>Starts over at a page, discarding the trail (a fresh launch, or a sign-out).</summary>
    public void Reset(string key)
    {
        _history.Clear();

        if (string.Equals(CurrentKey, key, StringComparison.Ordinal))
        {
            return;
        }

        CurrentKey = key;
        Navigated?.Invoke(this, key);
    }
}
