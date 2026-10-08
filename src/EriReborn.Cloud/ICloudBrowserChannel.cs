namespace EriReborn.Cloud;

/// <summary>
/// Optional bridge to the embedded browser. Some share pages are JavaScript challenges (蓝奏) and
/// some APIs demand a captcha token that only a real page can mint (迅雷); both can be read once a
/// genuine page has run. This is that hook. When no browser is attached the providers say so
/// plainly instead of guessing at a response they never saw.
/// </summary>
public interface ICloudBrowserChannel
{
    /// <summary>True when a real browser is attached and able to render pages.</summary>
    bool IsAvailable { get; }

    /// <summary>Loads the URL in the embedded browser, lets its scripts run, and returns the resulting HTML.</summary>
    Task<string?> FetchRenderedHtmlAsync(string url, CancellationToken cancellationToken = default);
}
