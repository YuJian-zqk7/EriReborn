namespace EriReborn.Cloud;

/// <summary>
/// Stable identifiers for the first-stage cloud platforms. All five are peers
/// (spec 26/76); nothing here encodes a priority order.
/// </summary>
public static class CloudProviderIds
{
    public const string Pan123 = "123";
    public const string Baidu = "baidu";
    public const string Quark = "quark";
    public const string Lanzou = "lanzou";
    public const string Xunlei = "xunlei";

    /// <summary>Every first-stage provider, in display order only.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Pan123, Baidu, Quark, Lanzou, Xunlei };

    /// <summary>Maps a legacy v2 source kind onto a v3 provider id.</summary>
    public static string? FromLegacySourceKind(string? kind) => (kind ?? string.Empty).ToLowerInvariant() switch
    {
        "pan123" or "123" => Pan123,
        "baidu" or "baidupan" => Baidu,
        "quark" => Quark,
        "lanzou" or "lanzl" => Lanzou,
        "xunlei" => Xunlei,
        _ => null,
    };

    /// <summary>
    /// The platform a share link belongs to, or null when the link names none of the five.
    ///
    /// <para>
    /// Only the host is matched, and only by suffix: the share hosts of 蓝奏云 have changed more
    /// than once, while inventing a platform from a path or a query would be asserting a fact
    /// about a link nobody has resolved yet. A link that matches nothing returns null and the
    /// caller says so, rather than picking a platform on the user's behalf.
    /// </para>
    /// </summary>
    public static string? FromShareUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();

        if (HostIs(host, "123pan.com") || HostIs(host, "123pan.cn") || HostIs(host, "123684.com"))
        {
            return Pan123;
        }

        if (HostIs(host, "baidu.com"))
        {
            return Baidu;
        }

        if (HostIs(host, "quark.cn"))
        {
            return Quark;
        }

        if (HostIs(host, "lanzou.com") || HostIs(host, "lanzoui.com") || HostIs(host, "lanzoux.com")
            || HostIs(host, "lanzouw.com") || HostIs(host, "woozooo.com"))
        {
            return Lanzou;
        }

        if (HostIs(host, "xunlei.com"))
        {
            return Xunlei;
        }

        return null;
    }

    private static bool HostIs(string host, string suffix)
        => string.Equals(host, suffix, StringComparison.Ordinal)
           || host.EndsWith("." + suffix, StringComparison.Ordinal);
}
