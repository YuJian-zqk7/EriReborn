using System.Text.Json;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Cloud.Providers;

/// <summary>
/// 百度网盘. A peer of every other provider (spec 76), implemented against the
/// documented xpan OpenAPI.
///
/// Directory listing uses <c>/rest/2.0/xpan/file?method=list</c>. The endpoint
/// and its error envelope were confirmed against the live service (an
/// unauthenticated call really returns <c>{"errno":-6,"request_id":...}</c>),
/// which is why failures are classified from <c>errno</c> rather than HTTP
/// status alone.
///
/// Share-link resolution still requires the verify/randsk handshake and is not
/// claimed here.
/// </summary>
public sealed class ProviderBaidu : CloudProviderBase
{
    public const string DefaultApiBase = "https://pan.baidu.com";

    private readonly string _apiBase;

    public ProviderBaidu(INetworkService network, ICredentialStore credentials, string? apiBase = null)
        : base(network, credentials)
    {
        _apiBase = (apiBase ?? DefaultApiBase).TrimEnd('/');
    }

    public override string Id => CloudProviderIds.Baidu;

    public override string DisplayName => "百度网盘";

    public override bool RequiresAuthentication => true;

    public override CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

    public override string? DocumentationUrl => "https://pan.baidu.com/union/doc/";

    public override string? LimitationNote =>
        "目录读取依赖 OAuth 访问令牌。分享链接解析按官方流程实现（页面 → verify 换 sekey → list → filemetas），"
        + "但**未用真实分享链接与提取码验证过**：它可能失败，失败时会指出是哪一步，不会报假的成功。"
        + "分享文件夹没有可直接打包下载的通道（网页端打包有 300MB 上限且需要登录转存），请展开文件夹选择其中的文件。";

    public override async Task<CloudListResult> ListChildrenAsync(
        string folderId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var state = GetAuthState(credential);
        if (state == CloudAuthState.Expired)
        {
            return CloudListResult.Fail(CloudErrorKind.AuthRequired, "百度网盘访问令牌已过期。");
        }

        if (state != CloudAuthState.Authenticated)
        {
            return CloudListResult.Fail(CloudErrorKind.AuthRequired, "百度网盘需要 OAuth 访问令牌。");
        }

        var directory = string.IsNullOrWhiteSpace(folderId) ? "/" : folderId;
        var url = $"{_apiBase}/rest/2.0/xpan/file"
            + "?method=list"
            + "&order=name"
            + "&limit=1000"
            + $"&dir={Uri.EscapeDataString(directory)}"
            + $"&access_token={Uri.EscapeDataString(credential!.Token!)}";

        try
        {
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url),
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return CloudListResult.Fail(CloudErrorKind.AuthRequired, "百度网盘访问令牌无效。");
            }

            if ((int)response.StatusCode == 429)
            {
                return CloudListResult.Fail(CloudErrorKind.RateLimited, "百度网盘请求过于频繁。");
            }

            // The xpan API reports application errors inside a 200 body, so the
            // payload is parsed even on an apparently successful response.
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = ParseFileList(json);

            if (!parsed.Success && parsed.Error == CloudErrorKind.None && !response.IsSuccessStatusCode)
            {
                return CloudListResult.Fail(
                    CloudErrorKind.ProviderError,
                    $"百度网盘返回 HTTP {(int)response.StatusCode}。");
            }

            return parsed;
        }
        catch (HttpRequestException ex)
        {
            return CloudListResult.Fail(CloudErrorKind.Network, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return CloudListResult.Fail(CloudErrorKind.Network, ex.Message);
        }
    }

    /// <summary>
    /// Resolves a share link into a direct download URL.
    ///
    /// <para>
    /// The order is fixed by the service, and that order is <b>verified</b>:
    /// calling <c>/share/list</c> without a <c>sekey</c> really does answer
    /// <c>errno 9019 "need verify"</c>, so verifying first is not a guess.
    /// </para>
    ///
    /// <para>
    /// <b>What is not verified</b>: this whole handshake has never been run against
    /// a real share link and a real extraction code, because none were available
    /// here. It is written to the public flow and it fails loudly at whichever step
    /// it cannot get past — it never reports a success it did not receive.
    /// </para>
    /// </summary>
    public override async Task<CloudResolveResult> ResolveAsync(
        string shareUrl,
        string? fileName,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var reference = BaiduShareReference.Parse(shareUrl);
        if (reference is null)
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "这不是一个可识别的百度网盘分享链接。");
        }

        if (reference.NeedsPassword && GetAuthState(credential) == CloudAuthState.NotRequired)
        {
            // Nothing to check here: an extraction code is not an account. Kept as a
            // reminder that the code travels with the caller, not with the token.
        }

        // A session, because the verify handshake only holds together with the
        // cookies the page set. The shared client has cookies off by design.
        using var session = Network.CreateSession();

        string page;
        try
        {
            using var pageResponse = await session.GetAsync(reference.PageUrl, cancellationToken).ConfigureAwait(false);

            if (pageResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Verified against the live service: an unknown short url is a 404,
                // not a 200 error envelope.
                return CloudResolveResult.Fail(CloudErrorKind.NotFound, "分享链接不存在（服务器返回 404）。");
            }

            if (!pageResponse.IsSuccessStatusCode)
            {
                return CloudResolveResult.Fail(
                    CloudErrorKind.ProviderError,
                    $"分享页面返回 HTTP {(int)pageResponse.StatusCode}。");
            }

            page = await pageResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
        }

        var shareId = ExtractJsonNumber(page, "shareid");
        // The modern list endpoint keys off uk + shareid; the page carries both.
        var shareUk = ExtractJsonNumber(page, "share_uk") ?? ExtractString(page, "share_uk");
        if (shareId is null)
        {
            return CloudResolveResult.Fail(
                CloudErrorKind.ParseFailure,
                "分享页面里没有找到 shareid（页面结构可能已变化，或者需要登录）。");
        }

        // Step 2: exchange the extraction code for a sekey.
        string? sekey = null;
        if (reference.NeedsPassword)
        {
            var verifyUrl = $"{_apiBase}/share/verify"
                + $"?shareid={shareId}"
                + $"&t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
                + "&channel=chunlei&web=1&app_id=250528&clienttype=0";

            try
            {
                using var form = new System.Net.Http.FormUrlEncodedContent(
                    new Dictionary<string, string> { ["pwd"] = reference.Password! });

                using var verifyResponse = await session.PostAsync(verifyUrl, form, cancellationToken).ConfigureAwait(false);
                var verifyBody = await verifyResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!TryReadErrno(verifyBody, out var verifyErrno, out var verifyMessage))
                {
                    return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "校验提取码的响应无法解析。");
                }

                if (verifyErrno != 0)
                {
                    return CloudResolveResult.Fail(
                        ClassifyErrno(verifyErrno),
                        verifyMessage ?? DescribeErrno(verifyErrno));
                }

                sekey = ExtractString(verifyBody, "randsk");
                if (string.IsNullOrWhiteSpace(sekey))
                {
                    return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "校验通过但没有返回 sekey。");
                }
            }
            catch (HttpRequestException ex)
            {
                return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
            }
        }

        // A browser session that already passed the extraction code leaves BDCLND behind; it is the
        // same randsk the verify step returns, so a harvested cookie can stand in for the POST.
        if (sekey is null && credential?.Cookie is { Length: > 0 } cookie)
        {
            foreach (var pair in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (pair.StartsWith("BDCLND=", StringComparison.OrdinalIgnoreCase))
                {
                    sekey = pair["BDCLND=".Length..];
                    break;
                }
            }
        }

        // Step 3: list the share's contents. This is where 9019 appears without a sekey.
        var listUrl = $"{_apiBase}/share/list"
            + (shareUk is null
                ? $"?shorturl={Uri.EscapeDataString(reference.ShortUrl)}"
                : $"?uk={Uri.EscapeDataString(shareUk)}&shareid={shareId}")
            + "&root=1&page=1&num=1000&order=time&desc=1&web=1&app_id=250528&channel=chunlei&clienttype=0"
            + (sekey is null ? string.Empty : $"&sekey={Uri.EscapeDataString(sekey)}");

        string listBody;
        try
        {
            using var listResponse = await session.GetAsync(listUrl, cancellationToken).ConfigureAwait(false);
            listBody = await listResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
        }

        if (!TryReadErrno(listBody, out var listErrno, out var listMessage))
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "分享文件列表的响应无法解析。");
        }

        if (listErrno != 0)
        {
            return CloudResolveResult.Fail(ClassifyErrno(listErrno), listMessage ?? DescribeErrno(listErrno));
        }

        var picked = PickFile(listBody, fileName, out var pickError);
        if (picked is null)
        {
            return CloudResolveResult.Fail(CloudErrorKind.NotFound, pickError ?? "分享里没有可下载的文件。");
        }

        var (path, directLink, isFolder) = picked.Value;

        if (isFolder)
        {
            // 网页端「打包下载」对文件夹有 300MB 上限（超限 errno 31090），且需要登录态、
            // 大文件夹还要先转存再用客户端下载——没有一条可在这里真实验证的直链通道。
            // 统一给出可行动退路（spec v3.1），不伪造链接。
            return CloudResolveResult.Fail(
                CloudErrorKind.Unsupported,
                "百度网盘分享文件夹无法直接打包下载（打包通道有 300MB 上限且需要登录转存），请在插件中展开文件夹选择其中的文件。");
        }

        // Step 4: a dlink is not always in the listing, so it is asked for explicitly.
        var dlink = directLink;
        if (string.IsNullOrWhiteSpace(dlink))
        {
            var metasUrl = $"{_apiBase}/rest/2.0/xpan/multimedia?method=filemetas&dlink=1"
                + $"&target={Uri.EscapeDataString("[\"" + path + "\"]")}"
                + (sekey is null ? string.Empty : $"&sekey={Uri.EscapeDataString(sekey)}");

            try
            {
                using var metasResponse = await session.GetAsync(metasUrl, cancellationToken).ConfigureAwait(false);
                var metasBody = await metasResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!TryReadErrno(metasBody, out var metasErrno, out var metasMessage))
                {
                    return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "获取直链的响应无法解析。");
                }

                if (metasErrno != 0)
                {
                    return CloudResolveResult.Fail(ClassifyErrno(metasErrno), metasMessage ?? DescribeErrno(metasErrno));
                }

                dlink = ExtractString(metasBody, "dlink");
            }
            catch (HttpRequestException ex)
            {
                return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(dlink))
        {
            return CloudResolveResult.Fail(CloudErrorKind.ProviderError, "分享解析成功，但没有拿到下载直链。");
        }

        // The direct link only works with the sekey attached; handing over the bare
        // dlink would produce a 403 that looks like a broken share.
        var downloadUrl = sekey is null
            ? dlink
            : dlink + (dlink.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "sekey=" + Uri.EscapeDataString(sekey);

        return CloudResolveResult.Ok(new CloudDownloadHandle(
            new CloudFile(shareId, Path.GetFileName(path), false),
            downloadUrl,
            Note: reference.NeedsPassword
                ? "已用提取码校验；直链带 sekey，可能随会话失效。"
                : "未设提取码的分享；直链可能随会话失效。"));
    }

    /// <summary>Reads the first numeric field of the given name out of a page or body.</summary>
    internal static string? ExtractJsonNumber(string text, string field)
    {
        // Built from a quote character rather than escaped literals. The service
        // answers compact JSON (verified against the live endpoint), so tolerating
        // spaces is enough and the pattern stays readable.
        const char quote = '"';
        var pattern = quote + System.Text.RegularExpressions.Regex.Escape(field) + quote
            + "[ ]*:[ ]*" + quote + "?([0-9]+)";

        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            pattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Reads the first string field of the given name.</summary>
    internal static string? ExtractString(string text, string field)
    {
        const char quote = '"';
        var pattern = quote + System.Text.RegularExpressions.Regex.Escape(field) + quote
            + "[ ]*:[ ]*" + quote + "([^" + quote + "]*)" + quote;

        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            pattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Reads an errno envelope, keeping errmsg when the service sent one.</summary>
    internal static bool TryReadErrno(string json, out int errno, out string? message)
    {
        errno = 0;
        message = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("errno", out var errnoElement) && errnoElement.TryGetInt32(out var code))
            {
                errno = code;
            }

            if (root.TryGetProperty("errmsg", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Chooses which entry of a share to download. An ambiguous share is refused
    /// rather than resolved to whichever file happened to come first.
    /// </summary>
    private static (string Path, string? Dlink, bool IsFolder)? PickFile(string listBody, string? fileName, out string? error)
    {
        error = null;

        using var document = JsonDocument.Parse(listBody);
        if (!document.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            error = "分享的响应里没有文件列表。";
            return null;
        }

        var candidates = new List<(string Path, string? Dlink, bool IsFolder)>();
        foreach (var entry in list.EnumerateArray())
        {
            var path = entry.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var isFolder = entry.TryGetProperty("isdir", out var dirElement)
                && dirElement.TryGetInt32(out var dirValue)
                && dirValue == 1;

            var dlink = entry.TryGetProperty("dlink", out var dlinkElement) && dlinkElement.ValueKind == JsonValueKind.String
                ? dlinkElement.GetString()
                : null;

            candidates.Add((path!, dlink, isFolder));
        }

        if (candidates.Count == 0)
        {
            error = "分享里没有文件。";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var wanted = candidates
                .Where(c => string.Equals(Path.GetFileName(c.Path), fileName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (wanted.Count == 1)
            {
                return wanted[0];
            }

            error = wanted.Count == 0
                ? $"分享里没有名为 '{fileName}' 的文件。"
                : $"分享里有多个 '{fileName}'，无法确定是哪一个。";
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        error = $"分享里有 {candidates.Count} 个条目，请指定要下载的文件名。";
        return null;
    }

    /// <summary>
    /// Parses an xpan response. errno 0 means success; the token errors are
    /// separated from permission errors so the UI can ask for a re-login rather
    /// than showing a generic failure.
    /// </summary>
    internal static CloudListResult ParseFileList(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return CloudListResult.Fail(CloudErrorKind.ParseFailure, ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            var errno = root.TryGetProperty("errno", out var errnoElement) && errnoElement.TryGetInt32(out var code)
                ? code
                : 0;

            if (errno != 0)
            {
                return CloudListResult.Fail(ClassifyErrno(errno), DescribeErrno(errno));
            }

            if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return CloudListResult.Fail(CloudErrorKind.ProviderError, "百度网盘响应中缺少 list。");
            }

            var files = new List<CloudFile>();
            foreach (var entry in list.EnumerateArray())
            {
                var name = entry.TryGetProperty("server_filename", out var nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;

                var id = entry.TryGetProperty("fs_id", out var idElement)
                    ? idElement.ToString()
                    : name;

                var isFolder = entry.TryGetProperty("isdir", out var isDirElement)
                    && isDirElement.TryGetInt32(out var isDir)
                    && isDir == 1;

                long? size = entry.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value)
                    ? value
                    : null;

                var path = entry.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;

                DateTimeOffset? modified = null;
                if (entry.TryGetProperty("server_mtime", out var mtimeElement) && mtimeElement.TryGetInt64(out var seconds))
                {
                    modified = DateTimeOffset.FromUnixTimeSeconds(seconds);
                }

                files.Add(new CloudFile(id, name, isFolder, size, path, modified));
            }

            return CloudListResult.Ok(files);
        }
    }

    /// <summary>
    /// xpan errno values used by the file and share endpoints.
    ///
    /// <para>
    /// <b>9019 is verified against the live service</b>: calling
    /// <c>/share/list</c> without a valid <c>sekey</c> really answers
    /// <c>{"errno":9019,"errmsg":"need verify"}</c>, which is what pins the
    /// handshake order — verify first, list second. The share codes 105/106/110/112/118
    /// come from the public documentation and are <b>not</b> verified here, because
    /// that would need a real share link and its extraction code.
    /// </para>
    /// </summary>
    internal static CloudErrorKind ClassifyErrno(int errno) => errno switch
    {
        0 => CloudErrorKind.None,
        -6 or 111 => CloudErrorKind.AuthRequired,

        // 9019: the share needs verifying before it will list anything.
        9019 => CloudErrorKind.AuthRequired,

        -7 or -9 or -10 or 31064 => CloudErrorKind.Forbidden,

        // 110: a captcha is required, so the user must act.
        // 106: the extraction code was wrong.
        106 or 110 => CloudErrorKind.Forbidden,

        31034 or -2 => CloudErrorKind.NotFound,

        // 105 bad link, 112 expired page, 118 share cancelled or file removed.
        105 or 112 or 118 => CloudErrorKind.NotFound,
        _ => CloudErrorKind.ProviderError,
    };

    internal static string DescribeErrno(int errno) => errno switch
    {
        -6 => "百度网盘身份验证失败，请重新登录。",
        111 => "百度网盘访问令牌已过期。",
        9019 => "该分享需要先校验提取码（sekey 缺失或已过期）。",
        -7 => "没有访问该目录的权限。",
        -9 or -10 => "请求的文件不存在。",
        31064 => "该应用没有访问该目录的授权。",
        106 => "提取码错误。",
        110 => "该分享需要输入验证码。",
        105 => "分享链接地址错误。",
        112 => "分享页面已过期。",
        118 => "分享已被取消，或文件已被删除。",
        2 => "请求参数错误。",
        _ => $"百度网盘返回错误 errno={errno}。",
    };
}
