namespace EriReborn.Cloud.Providers;

/// <summary>
/// A Baidu share link, split into the two things the API actually wants: the
/// short url and, when the share is protected, its extraction code.
///
/// <para>
/// Baidu accepts the code embedded in the link three different ways and users
/// paste whichever one they were given, so all three are read here rather than
/// asking the user to take their own link apart.
/// </para>
/// </summary>
public sealed record BaiduShareReference(string ShortUrl, string? Password)
{
    public const string Host = "https://pan.baidu.com";

    public bool NeedsPassword => !string.IsNullOrWhiteSpace(Password);

    /// <summary>The page the short url points at.</summary>
    public string PageUrl => $"{Host}/s/{ShortUrl}";

    /// <summary>
    /// Reads a share reference out of a pasted link or code.
    ///
    /// <para>
    /// Returns null rather than guessing: a link that cannot be understood must
    /// produce a clear "this is not a share link" instead of a request to the wrong
    /// page whose error nobody can interpret.
    /// </para>
    /// </summary>
    public static BaiduShareReference? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        // "链接 提取码" and "链接:提取码" are both common pastes.
        string? trailingCode = null;
        var space = text.IndexOf(' ');
        if (space > 0)
        {
            trailingCode = text[(space + 1)..].Trim();
            text = text[..space].Trim();
        }

        string? password = null;

        // The code can be a query parameter or a fragment.
        var hash = text.IndexOf('#');
        if (hash >= 0)
        {
            var fragment = text[(hash + 1)..].Trim();
            text = text[..hash];
            if (fragment.Length > 0)
            {
                password = fragment;
            }
        }

        var query = text.IndexOf('?');
        if (query >= 0)
        {
            var queryText = text[(query + 1)..];
            text = text[..query];

            foreach (var pair in queryText.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                if (pair[..equals].Equals("pwd", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Uri.UnescapeDataString(pair[(equals + 1)..]).Trim();
                    if (candidate.Length > 0)
                    {
                        password = candidate;
                    }
                }
            }
        }

        // Strip the scheme/host when present, so a full URL and a bare code take
        // the same path from here on.
        var marker = text.IndexOf("/s/", StringComparison.OrdinalIgnoreCase);

        // A full url without "/s/" is not a share link. Falling through to the bare
        // branch would read "https" out of it as the short url — which is what
        // treating any pasted url as a share produces.
        if (marker < 0 && text.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var shortUrl = marker >= 0
            ? text[(marker + 3)..]
            : text.TrimStart('/');

        shortUrl = shortUrl.Trim().Trim('/');

        // The last common paste form: "surl:code", with no scheme in sight.
        var colon = shortUrl.IndexOf(':');
        if (colon > 0)
        {
            password ??= Normalize(shortUrl[(colon + 1)..]);
            shortUrl = shortUrl[..colon];
        }

        if (shortUrl.Length == 0 || shortUrl.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        password ??= Normalize(trailingCode);

        return new BaiduShareReference(shortUrl, password);
    }

    /// <summary>A one-character code is not a code; anything longer is taken as given.</summary>
    private static string? Normalize(string? code)
    {
        var trimmed = code?.Trim().TrimStart(':').Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    public override string ToString()
        => NeedsPassword ? $"{ShortUrl} (提取码 {Password})" : ShortUrl;
}
