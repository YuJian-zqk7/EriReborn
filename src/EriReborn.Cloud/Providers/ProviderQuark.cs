using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Cloud.Providers;

/// <summary>
/// 夸克网盘. Cookie-based session. The share flow was verified live against a real link:
/// sharepage/token returns a stoken, sharepage/detail returns the file list, and file/download needs a
/// signed-in cookie (it answers 401 <c>31001 require login</c> without one).
/// </summary>
public sealed class ProviderQuark(INetworkService network, ICredentialStore credentials)
    : CloudProviderBase(network, credentials)
{
    private const string TokenEndpoint = "https://drive-pc.quark.cn/1/clouddrive/share/sharepage/token?pr=ucpro&fr=pc";
    private const string DetailEndpoint = "https://drive-pc.quark.cn/1/clouddrive/share/sharepage/detail";
    private const string DownloadEndpoint = "https://drive-pc.quark.cn/1/clouddrive/file/download?pr=ucpro&fr=pc";
    private const string SortEndpoint = "https://drive-pc.quark.cn/1/clouddrive/file/sort?pr=ucpro&fr=pc";

    public override string Id => CloudProviderIds.Quark;

    public override string DisplayName => "夸克网盘";

    public override bool RequiresAuthentication => true;

    public override CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

    public override string? DocumentationUrl => "https://pan.quark.cn/";

    public override string? LimitationNote =>
        "分享可匿名列出（token → detail 已实测），并支持按文件夹逐层进入；下载需要登录后的 Cookie 会话。"
        + "夸克没有提供整个文件夹打包下载的官方接口，请在插件中展开文件夹选择其中的文件（spec v3.1）。";

    public override async Task<CloudListResult> ListChildrenAsync(
        string folderId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var cookie = SessionCookie(credential);
        if (cookie is null)
        {
            return CloudListResult.Fail(
                CloudErrorKind.AuthRequired,
                "夸克网盘需要登录后的 Cookie 会话（可用「用内置浏览器登录」获取）。");
        }

        try
        {
            var url = SortEndpoint
                + "&pdir_fid=" + Uri.EscapeDataString(folderId)
                + "&_page=1&_size=100&_fetch_total=1&_sort=file_type:asc,updated_at:desc";

            using var response = await SendAsync(
                () => Request(HttpMethod.Get, url, null, cookie, null),
                cancellationToken).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Read through the shape-checking helpers. A refused call answers with data:null, and
            // TryGetProperty on a null element throws — which is what used to close the application when a
            // drive session expired (see CloudJson).
            if (CloudJson.Code(root) is int code && code != 0)
            {
                return CloudListResult.Fail(
                    CloudErrorKind.AuthRequired,
                    "夸克网盘拒绝了这次请求：" + (CloudJson.Message(root) ?? "登录态可能已失效。"));
            }

            var files = new List<CloudFile>();
            if (CloudJson.TryObject(root, "data", out var data)
                && CloudJson.TryArray(data, "list", out var list))
            {
                foreach (var item in list.EnumerateArray())
                {
                    CloudJson.TryString(item, "fid", out var fid);
                    CloudJson.TryString(item, "file_name", out var name);
                    if (fid is null || name is null)
                    {
                        continue;
                    }

                    var isDir = item.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.True;
                    var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0L;
                    files.Add(new CloudFile(fid, name, isDir, size));
                }
            }

            return CloudListResult.Ok(files);
        }
        catch (JsonException ex)
        {
            return CloudListResult.Fail(CloudErrorKind.ParseFailure, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return CloudListResult.Fail(CloudErrorKind.Network, ex.Message);
        }
    }

    public override async Task<CloudResolveResult> ResolveAsync(
        string shareUrl,
        string? fileName,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shareUrl))
        {
            return CloudResolveResult.Fail(CloudErrorKind.NotFound, "缺少分享链接。");
        }

        var shareId = ExtractShareId(shareUrl);
        if (string.IsNullOrEmpty(shareId))
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "无法从链接中解析出夸克分享 ID。");
        }

        var passcode = ExtractPasscode(shareUrl);
        var cookie = SessionCookie(credential);

        try
        {
            // Step 1: exchange the share id for a stoken. This works anonymously.
            var tokenBody = "{\"pwd_id\":\"" + shareId + "\",\"passcode\":\"" + (passcode ?? string.Empty) + "\"}";
            using var tokenResponse = await SendAsync(
                () => Request(HttpMethod.Post, TokenEndpoint, tokenBody, cookie, null),
                cancellationToken).ConfigureAwait(false);

            var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var tokenDocument = JsonDocument.Parse(tokenJson);
            var tokenRoot = tokenDocument.RootElement;

            if (CloudJson.Code(tokenRoot) is int tokenCode && tokenCode != 0)
            {
                return CloudResolveResult.Fail(
                    CloudErrorKind.AuthRequired,
                    "夸克分享令牌获取失败：" + (CloudJson.Message(tokenRoot) ?? "可能需要提取码。"));
            }

            string? stoken = null;
            if (CloudJson.TryObject(tokenRoot, "data", out var tokenData))
            {
                CloudJson.TryString(tokenData, "stoken", out stoken);
            }
            if (string.IsNullOrEmpty(stoken))
            {
                return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "分享接口没有返回 stoken。");
            }

            // Step 2: list the share, which also carries the file id used by the download call.
            var detailUrl = DetailEndpoint
                + "?pr=ucpro&fr=pc"
                + "&pwd_id=" + Uri.EscapeDataString(shareId)
                + "&stoken=" + Uri.EscapeDataString(stoken)
                + "&pdir_fid=0&force=0&_page=1&_size=50"
                + "&_fetch_banner=1&_fetch_share=1&_fetch_total=1&_sort=file_type:asc,updated_at:desc";

            using var detailResponse = await SendAsync(
                () => Request(HttpMethod.Get, detailUrl, null, cookie, null),
                cancellationToken).ConfigureAwait(false);

            var detailJson = await detailResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? entryFid = null;
            string entryName = fileName ?? shareId;
            long entrySize = 0L;
            var entryIsDir = false;

            using (var detailDocument = JsonDocument.Parse(detailJson))
            {
                var detailRoot = detailDocument.RootElement;
                if (CloudJson.Code(detailRoot) is int detailCode && detailCode != 0)
                {
                    return CloudResolveResult.Fail(
                        CloudErrorKind.ProviderError,
                        "夸克分享目录读取失败：" + (CloudJson.Message(detailRoot) ?? "未知原因。"));
                }

                if (!CloudJson.TryObject(detailRoot, "data", out var detailData)
                    || !CloudJson.TryArray(detailData, "list", out var entries)
                    || entries.GetArrayLength() == 0)
                {
                    return CloudResolveResult.Fail(CloudErrorKind.NotFound, "分享里没有文件。");
                }

                foreach (var item in entries.EnumerateArray())
                {
                    CloudJson.TryString(item, "file_name", out var name);
                    if (fileName is not null && name != fileName)
                    {
                        continue;
                    }

                    CloudJson.TryString(item, "fid", out entryFid);
                    entryName = name ?? entryName;
                    entrySize = CloudJson.TryInt64(item, "size") ?? 0L;
                    entryIsDir = item.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.True;
                    break;
                }
            }

            if (entryFid is null)
            {
                return CloudResolveResult.Fail(CloudErrorKind.NotFound, "分享里找不到这个文件名。");
            }

            // Step 3: ask for the direct URL. This is the step that needs the signed-in cookie.
            var downloadBody = "{\"fids\":[\"" + entryFid + "\"]}";
            using var downloadResponse = await SendAsync(
                () => Request(HttpMethod.Post, DownloadEndpoint, downloadBody, cookie, null),
                cancellationToken).ConfigureAwait(false);

            var downloadJson = await downloadResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var downloadDocument = JsonDocument.Parse(downloadJson);
            var downloadRoot = downloadDocument.RootElement;

            if (CloudJson.Code(downloadRoot) is int downloadCode && downloadCode != 0)
            {
                return CloudResolveResult.Fail(
                    CloudErrorKind.AuthRequired,
                    "夸克下载链接获取失败：" + (CloudJson.Message(downloadRoot) ?? "需要登录后的 Cookie 会话。"));
            }

            if (CloudJson.TryArray(downloadRoot, "data", out var downloadData))
            {
                foreach (var item in downloadData.EnumerateArray())
                {
                    CloudJson.TryString(item, "download_url", out var url);
                    if (!string.IsNullOrEmpty(url))
                    {
                        return CloudResolveResult.Ok(new CloudDownloadHandle(
                            new CloudFile(entryFid, entryName, entryIsDir, entrySize),
                            url,
                            Note: "直链来自夸克网盘 file/download。"));
                    }
                }
            }

            return CloudResolveResult.Fail(CloudErrorKind.NotFound, "夸克下载接口没有返回 download_url。");
        }
        catch (JsonException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
        }
    }

    /// <summary>Builds a quark request with the headers its edge requires (Pr/Fr are not optional).</summary>
    private static HttpRequestMessage Request(
        HttpMethod method,
        string url,
        string? jsonBody,
        string? cookie,
        string? extraCookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Referer", "https://pan.quark.cn/");
        request.Headers.TryAddWithoutValidation("Origin", "https://pan.quark.cn");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Pr", "ucpro");
        request.Headers.TryAddWithoutValidation("Fr", "pc");

        var merged = cookie;
        if (!string.IsNullOrEmpty(extraCookie))
        {
            merged = string.IsNullOrEmpty(merged) ? extraCookie : merged + "; " + extraCookie;
        }

        if (!string.IsNullOrEmpty(merged))
        {
            request.Headers.TryAddWithoutValidation("Cookie", merged);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>The stored cookie session, whether it arrived as a token field or a harvested cookie.</summary>
    private static string? SessionCookie(CloudCredential? credential)
    {
        if (!string.IsNullOrEmpty(credential?.Cookie))
        {
            return credential!.Cookie;
        }

        return string.IsNullOrEmpty(credential?.Token) ? null : credential!.Token;
    }

    private static string? ExtractShareId(string shareUrl)
    {
        var match = System.Text.RegularExpressions.Regex.Match(shareUrl, "/s/([A-Za-z0-9]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractPasscode(string shareUrl)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            shareUrl,
            "(?:pwd|passcode)=([^&#]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    /// <summary>Reads the files inside a quark share link (token then detail, share root).</summary>
    public override Task<CloudListResult> ListShareAsync(
        string shareUrl,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => ListShareAtAsync(shareUrl, parentItemId: "0", credential, cancellationToken);

    /// <summary>
    /// Reads one folder inside a quark share. The detail call verified for the root
    /// (<c>pdir_fid=0</c>) takes the folder fid the same way, so the share tree can descend one
    /// level at a time instead of being stuck at the root (spec v3.1) — this is also what makes
    /// "expand the folder and pick files inside it" a real alternative where whole-folder packaging
    /// is unavailable.
    /// </summary>
    public override Task<CloudListResult> ListShareFolderAsync(
        string shareUrl,
        string? parentItemId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => ListShareAtAsync(
            shareUrl,
            string.IsNullOrWhiteSpace(parentItemId) ? "0" : parentItemId,
            credential,
            cancellationToken);

    /// <summary>token → detail 的完整分享读取流程；<paramref name="parentItemId"/> 为 "0" 时即分享根。</summary>
    private async Task<CloudListResult> ListShareAtAsync(
        string shareUrl,
        string parentItemId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var shareId = ExtractShareId(shareUrl);
        if (string.IsNullOrEmpty(shareId))
        {
            return CloudListResult.Fail(CloudErrorKind.ParseFailure, "无法从链接中解析出夸克分享 ID。");
        }

        var passcode = ExtractPasscode(shareUrl);
        var cookie = SessionCookie(credential);

        try
        {
            var tokenBody = "{\"pwd_id\":\"" + shareId + "\",\"passcode\":\"" + (passcode ?? string.Empty) + "\"}";
            using var tokenResponse = await SendAsync(
                () => Request(HttpMethod.Post, TokenEndpoint, tokenBody, cookie, null),
                cancellationToken).ConfigureAwait(false);

            var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? stoken;
            using (var tokenDocument = JsonDocument.Parse(tokenJson))
            {
                var tokenRoot = tokenDocument.RootElement;
                if (CloudJson.Code(tokenRoot) is int tokenCode && tokenCode != 0)
                {
                    return CloudListResult.Fail(
                        CloudErrorKind.AuthRequired,
                        "夸克分享令牌获取失败：" + (CloudJson.Message(tokenRoot) ?? "可能需要提取码。"));
                }

                stoken = null;
                if (CloudJson.TryObject(tokenRoot, "data", out var tokenData))
                {
                    CloudJson.TryString(tokenData, "stoken", out stoken);
                }
            }

            if (string.IsNullOrEmpty(stoken))
            {
                return CloudListResult.Fail(CloudErrorKind.ParseFailure, "分享接口没有返回 stoken。");
            }

            var detailUrl = DetailEndpoint
                + "?pr=ucpro&fr=pc"
                + "&pwd_id=" + Uri.EscapeDataString(shareId)
                + "&stoken=" + Uri.EscapeDataString(stoken)
                + "&pdir_fid=" + Uri.EscapeDataString(parentItemId)
                + "&force=0&_page=1&_size=50"
                + "&_fetch_banner=1&_fetch_share=1&_fetch_total=1&_sort=file_type:asc,updated_at:desc";

            using var detailResponse = await SendAsync(
                () => Request(HttpMethod.Get, detailUrl, null, cookie, null),
                cancellationToken).ConfigureAwait(false);

            var detailJson = await detailResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var detailDocument = JsonDocument.Parse(detailJson);
            var detailRoot = detailDocument.RootElement;

            if (CloudJson.Code(detailRoot) is int detailCode && detailCode != 0)
            {
                return CloudListResult.Fail(CloudErrorKind.ProviderError, "夸克分享目录读取失败：" + (CloudJson.Message(detailRoot) ?? "未知原因。"));
            }

            var files = new List<CloudFile>();
            if (CloudJson.TryObject(detailRoot, "data", out var detailData)
                && CloudJson.TryArray(detailData, "list", out var list))
            {
                foreach (var item in list.EnumerateArray())
                {
                    CloudJson.TryString(item, "fid", out var fid);
                    CloudJson.TryString(item, "file_name", out var name);
                    if (fid is null || name is null)
                    {
                        continue;
                    }

                    var isDir = item.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.True;
                    var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0L;
                    files.Add(new CloudFile(fid, name, isDir, size));
                }
            }

            return CloudListResult.Ok(files);
        }
        catch (JsonException ex)
        {
            return CloudListResult.Fail(CloudErrorKind.ParseFailure, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return CloudListResult.Fail(CloudErrorKind.Network, ex.Message);
        }
    }

    /// <inheritdoc />
    public override async Task<CloudResolveResult> ResolveDriveFileAsync(
        string fileId,
        string fileName,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => await DownloadByFidAsync(
            fileId,
            fileName,
            size: null,
            credential,
            "直链来自夸克网盘 file/download（个人文件）。",
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Resolves a share item the tree walk already located. The default base implementation re-queries
    /// the share root by name, which cannot find a file nested in a folder — the very case the
    /// whole-folder refusal tells authors to use ("展开选择其中的文件"). The walk carries the item's
    /// fid, so the direct link is requested for that fid directly; no re-listing, no guessing.
    /// </summary>
    public override Task<CloudResolveResult> ResolveShareItemAsync(
        string shareUrl,
        CloudFile item,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => DownloadByFidAsync(
            item.Id,
            item.Name,
            item.SizeBytes,
            credential,
            "直链来自夸克网盘 file/download（分享内文件，已按文件夹逐层定位）。",
            cancellationToken);

    /// <summary>
    /// Shared fid → file/download call for drive files and already-walked share items. The step
    /// needs the signed-in cookie, exactly as the share flow already documented.
    /// </summary>
    private async Task<CloudResolveResult> DownloadByFidAsync(
        string fid,
        string fileName,
        long? size,
        CloudCredential? credential,
        string note,
        CancellationToken cancellationToken)
    {
        var cookie = SessionCookie(credential);
        var body = "{\"fids\":[\"" + fid + "\"]}";

        try
        {
            using var response = await SendAsync(
                () => Request(HttpMethod.Post, DownloadEndpoint, body, cookie, null),
                cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (CloudJson.Code(root) is int code && code != 0)
            {
                return CloudResolveResult.Fail(
                    CloudErrorKind.AuthRequired,
                    "夸克直链获取失败：" + (CloudJson.Message(root) ?? "需要登录后的 Cookie 会话。"));
            }

            if (CloudJson.TryArray(root, "data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    CloudJson.TryString(item, "download_url", out var url);
                    if (!string.IsNullOrEmpty(url))
                    {
                        return CloudResolveResult.Ok(new CloudDownloadHandle(
                            new CloudFile(fid, fileName, false, size),
                            url,
                            Note: note));
                    }
                }
            }

            return CloudResolveResult.Fail(CloudErrorKind.NotFound, "夸克没有返回 download_url。");
        }
        catch (JsonException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
        }
    }

}
