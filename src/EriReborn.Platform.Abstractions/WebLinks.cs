namespace EriReborn.Platform.Abstractions;

/// <summary>
/// The one rule about what a web link is.
///
/// <para>
/// A shell service hands a string to the operating system, and the system runs whatever this machine
/// has associated with it. Restricting that to http/https — and normalising it first — is the
/// difference between opening a page and letting a stored value start a program, so the rule lives in
/// one place that every platform and every caller shares rather than being re-decided per platform
/// (spec 5/67).
/// </para>
/// </summary>
public static class WebLinks
{
    /// <summary>
    /// True when <paramref name="url"/> is an absolute http(s) address; the normalised form comes back
    /// in <paramref name="absolute"/>. Refusing here rather than passing it on keeps the refusal ours
    /// instead of the system's.
    /// </summary>
    public static bool TryNormalise(string? url, out string absolute, out string refusal)
    {
        absolute = string.Empty;
        refusal = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            refusal = "链接为空。";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            refusal = "只支持 http/https 链接：" + url.Trim();
            return false;
        }

        absolute = uri.AbsoluteUri;
        return true;
    }
}
