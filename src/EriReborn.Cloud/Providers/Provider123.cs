using System.Net.Http.Headers;
using System.Text.Json;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Cloud.Providers;

/// <summary>
/// 123 云盘. Uses the documented open platform API and therefore reports
/// <see cref="CloudImplementationKind.OfficialApi"/>.
/// </summary>
public sealed class Provider123(INetworkService network, ICredentialStore credentials)
    : CloudProviderBase(network, credentials)
{
    private const string ListEndpoint = "https://open-api.123pan.com/api/v1/file/list";
    // 诊断 list 响应字段：123 download_info API 要 size/s3keyFlag/etag 必填非空，
    // 缺哪个报"请输入X"。但 list 响应到底有没有这些字段、字段名是大写还是混合大小写，
    // 没法靠猜——把响应原始 JSON 写到日志里，下次失败就能直接读到。
    private static readonly IAppLogger Log = AppLog.For("Cloud.123");
    // The API moved off www.123pan.com: that host answers its SPA 404 page for every api path. The live
    // host and paths below were read off the site's own traffic, and /api/share/get answers code 0 for a
    // real share key.
    private const string ApiBase = "https://api.123278.com";
    private const string ShareListEndpoint = ApiBase + "/api/share/get";
    private const string DownloadInfoEndpoint = ApiBase + "/api/file/download_info";
    private const string BatchDownloadEndpoint = ApiBase + "/api/file/batch_download_share_info";

    public override string Id => CloudProviderIds.Pan123;

    public override string DisplayName => "123 云盘";

    public override bool RequiresAuthentication => true;

    public override CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

    public override string? DocumentationUrl => "https://www.123pan.com/developer";

    public override string? LimitationNote => "目录读取依赖开放平台 Token；未登录时只显示认证状态。";

    /// <summary>
    /// 123 的 <c>batch_download_share_info</c> 对文件夹返回整包 zip 直链，所以整夹下载真实可用
    /// （spec v3.1）。此能力位与 <see cref="ResolveShareFolderPackageAsync"/> 成对开启。
    /// </summary>
    public override bool SupportsFolderPackage => true;

    public override async Task<CloudListResult> ListChildrenAsync(
        string folderId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        if (GetAuthState(credential) != CloudAuthState.Authenticated)
        {
            return CloudListResult.Fail(CloudErrorKind.AuthRequired, "123 云盘需要有效的开放平台 Token。");
        }

        try
        {
            using var response = await SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"{ListEndpoint}?parentFileId={Uri.EscapeDataString(folderId)}&limit=100");
                    request.Headers.TryAddWithoutValidation("Platform", "open_platform");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential!.Token);
                    return request;
                },
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return CloudListResult.Fail(CloudErrorKind.AuthRequired, "123 云盘 Token 已失效。");
            }

            if ((int)response.StatusCode == 429)
            {
                return CloudListResult.Fail(CloudErrorKind.RateLimited, "123 云盘请求过于频繁。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return CloudListResult.Fail(CloudErrorKind.ProviderError, $"123 云盘返回 HTTP {(int)response.StatusCode}。");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseFileList(json);
        }
        catch (HttpRequestException ex)
        {
            return CloudListResult.Fail(CloudErrorKind.Network, ex.Message);
        }
    }

    private static CloudListResult ParseFileList(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            // The envelope is read before its members are: a refused call answers code 20101 with a null
            // "data", and asking a null element for a property used to throw out of the page command and
            // close the application.
            if (CloudJson.Code(document.RootElement) is { } code && code != 0)
            {
                return CloudListResult.Fail(
                    code is 20101 or 401 ? CloudErrorKind.AuthRequired : CloudErrorKind.ProviderError,
                    code is 20101 or 401
                        ? "需要登录 123 云盘后才能查看网盘文件（" + (CloudJson.Message(document.RootElement) ?? "未登录") + "）。"
                        : "123 云盘拒绝了这次请求：" + (CloudJson.Message(document.RootElement) ?? "未知原因") + "。");
            }

            if (!CloudJson.TryObject(document.RootElement, "data", out var data)
                || !CloudJson.TryArray(data, "fileList", out var list))
            {
                return CloudListResult.Fail(CloudErrorKind.ProviderError, "123 云盘响应中缺少 data.fileList。");
            }

            var files = new List<CloudFile>();
            foreach (var entry in list.EnumerateArray())
            {
                // Read through the shape-checking helpers. A field arriving as a number, an object or null
                // used to throw here — and a provider exception runs on a page command, where it leaves the
                // dispatcher and takes the whole application with it (see CloudJson).
                var id = entry.TryGetProperty("fileId", out var idElement) ? idElement.ToString() : string.Empty;
                CloudJson.TryString(entry, "filename", out var name);
                var isFolder = CloudJson.TryInt(entry, "type", out var type) && type == 1;
                long? size = entry.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var s) ? s : null;
                files.Add(new CloudFile(id, name ?? string.Empty, isFolder, size));
            }

            return CloudListResult.Ok(files);
        }
        catch (JsonException ex)
        {
            return CloudListResult.Fail(CloudErrorKind.ParseFailure, ex.Message);
        }
    }

    /// <summary>
    /// Resolves a 123 云盘 share into a direct download URL.
    ///
    /// <para>
    /// The share endpoint this used to call (<c>/api/share/info</c>) answers HTTP 404 now, which is why
    /// every share link failed. The live flow is the one the web/app clients use: read the share through
    /// <c>/b/api/share/get</c>, then exchange the file entry for a link through the signed
    /// <c>/b/api/file/download_info</c> (or the folder bundle endpoint).
    /// </para>
    /// </summary>
    public override async Task<CloudResolveResult> ResolveAsync(
        string shareUrl,
        string? fileName,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var shareKey = ExtractShareKey(shareUrl);
        if (string.IsNullOrEmpty(shareKey))
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "无法从分享链接中解析出 shareKey。");
        }

        if (string.IsNullOrEmpty(shareKey) || !shareKey.Contains('-'))
        {
            // Share links now come in two shapes: the legacy /s/<key> and the newer
            // <uid>.share.123pan.cn/123pan/<key>-<sig>, where the key carries both halves and only the
            // full value answers code 0 (a partial one is "ShareKey格式异常").
            var match = System.Text.RegularExpressions.Regex.Match(shareUrl, "/(?:s|123pan)/([^/?#]+)");
            if (match.Success)
            {
                shareKey = match.Groups[1].Value.Replace(".html", string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (string.IsNullOrEmpty(shareKey))
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "无法从分享链接中解析出 shareKey。");
        }

        var password = ExtractSharePassword(shareUrl);
        var listUrl = ShareListEndpoint
            + "?limit=100&next=1&orderBy=share_id&orderDirection=desc"
            + "&shareKey=" + Uri.EscapeDataString(shareKey)
            + "&SharePwd=" + Uri.EscapeDataString(password ?? string.Empty)
            + "&ParentFileId=0&Page=1";

        string listJson;
        try
        {
            using var listResponse = await SendAsync(
                () => ShareRequest(HttpMethod.Get, listUrl, credential, null),
                cancellationToken).ConfigureAwait(false);
            if (!listResponse.IsSuccessStatusCode)
            {
                return CloudResolveResult.Fail(
                    CloudErrorKind.ProviderError,
                    "分享接口返回 HTTP " + (int)listResponse.StatusCode + "。");
            }

            listJson = await listResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
        }

        // JsonDocument owns the memory of every JsonElement it produced, so the fields are copied out
        // here instead of keeping an element alive past the using block (that threw ObjectDisposedException).
        long fileId = 0L;
        var entryName = shareKey;
        var isFolder = false;
        string? etag = null;
        string? s3KeyFlag = null;
        long entrySize = 0L;
        var found = false;
        try
        {
            using var document = JsonDocument.Parse(listJson);
            var root = document.RootElement;
            // The envelope is read through the shape-checking helpers: `data` is not always an object (a
            // refused or signed-out call answers with null), and TryGetProperty on a null element throws.
            if (CloudJson.Code(root) is int code && code != 0)
            {
                return CloudResolveResult.Fail(
                    CloudErrorKind.AuthRequired,
                    "123 云盘拒绝了这次分享请求：" + (CloudJson.Message(root) ?? "需要有效登录态或分享口令。"));
            }

            if (!CloudJson.TryObject(root, "data", out var data)
                || !CloudJson.TryArray(data, "InfoList", out var infoList)
                || infoList.GetArrayLength() == 0)
            {
                return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "分享里没有文件（返回结构可能已变化）。");
            }

            var wanted = fileName;
            foreach (var item in infoList.EnumerateArray())
            {
                CloudJson.TryString(item, "FileName", out var itemName);

                if (wanted is null || itemName == wanted)
                {
                    // FileId arrives as a number on the live service but has been seen as a string; both
                    // readings are handled rather than one of them throwing.
                    fileId = CloudJson.TryInt64(item, "FileId") ?? 0L;
                    entryName = itemName ?? shareKey;
                    isFolder = CloudJson.TryInt(item, "Type", out var type) && type == 1;
                    CloudJson.TryString(item, "Etag", out etag);
                    CloudJson.TryString(item, "S3KeyFlag", out s3KeyFlag);
                    entrySize = CloudJson.TryInt64(item, "Size") ?? 0L;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return CloudResolveResult.Fail(CloudErrorKind.NotFound, "分享里找不到这个文件名。");
            }
        }
        catch (JsonException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, ex.Message);
        }



        string downloadJson;
        try
        {
            if (isFolder)
            {
                var body = "{\"shareKey\":\"" + shareKey + "\",\"fileIdList\":[{\"fileId\":" + fileId + "}]}";
                using var folderResponse = await SendAsync(
                    () => ShareRequest(
                        HttpMethod.Post,
                        BatchDownloadEndpoint + Pan123Signature.Encode("/api/file/batch_download_share_info", "android", "55", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                        credential,
                        body),
                    cancellationToken).ConfigureAwait(false);
                downloadJson = await folderResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var body = "{\"driveId\":0"
                    + ",\"etag\":\"" + (etag ?? string.Empty) + "\""
                    + ",\"fileId\":" + fileId
                    + ",\"fileName\":\"" + entryName + "\""
                    + ",\"s3keyFlag\":\"" + (s3KeyFlag ?? string.Empty) + "\""
                    + ",\"size\":" + entrySize
                    + ",\"type\":0}";
                using var fileResponse = await SendAsync(
                    () => ShareRequest(
                        HttpMethod.Post,
                        DownloadInfoEndpoint + Pan123Signature.Encode("/api/file/download_info", "android", "55", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                        credential,
                        body),
                    cancellationToken).ConfigureAwait(false);
                downloadJson = await fileResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
        }

        // One reader for both the named and the in-share path: they ask the same endpoint and need the
        // same reading of its failures, so 未登录 travels as "sign in first" from either of them.
        return ParseDownloadUrl(downloadJson, new CloudFile(fileId.ToString(), entryName, isFolder), isFolder);
    }

    /// <summary>Builds one 123 request with the headers the live endpoints require.</summary>
    private static HttpRequestMessage ShareRequest(
        HttpMethod method,
        string url,
        CloudCredential? credential,
        string? jsonBody)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.123pan.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.123pan.com");
        request.Headers.TryAddWithoutValidation("platform", "android");
        request.Headers.TryAddWithoutValidation("App-Version", "55");
        var bearer = credential?.Token;
        if (string.IsNullOrEmpty(bearer) && !string.IsNullOrEmpty(credential?.Cookie))
        {
            // 123 does not hand out a token field: the signed-in browser leaves authorToken behind, so the
            // harvested "authorToken=..." pair is what actually authenticates a download (code 20101 未登录
            // without it).
            foreach (var pair in credential!.Cookie!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var name = pair[..separator];
                if (name.Equals("authorToken", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("token", StringComparison.OrdinalIgnoreCase))
                {
                    bearer = pair[(separator + 1)..];
                    break;
                }
            }
        }

        if (!string.IsNullOrEmpty(bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>Reads the extraction code a 123 link can carry as ?pwd= or #pwd=.</summary>
    private static string? ExtractSharePassword(string shareUrl)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            shareUrl,
            "(?:pwd|password)=([^&#]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private static string? ExtractShareKey(string shareUrl)
    {
        var segments = shareUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i].Split('?')[0];
            if (segment.Length > 4 && segment.Contains('-'))
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>Reads the share root (spec 30): the same call the folder walk uses for a parent id of 0.</summary>
    public override Task<CloudListResult> ListShareAsync(
        string shareUrl,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => ListShareFolderAsync(shareUrl, "0", credential, cancellationToken);

    /// <summary>
    /// Reads one folder inside a share. The endpoint takes <c>ParentFileId</c>; a null id is the root,
    /// which is what <see cref="ListShareAsync"/> already reads, so the walk can descend instead of
    /// being told "this platform cannot look there".
    /// </summary>
    public override async Task<CloudListResult> ListShareFolderAsync(
        string shareUrl,
        string? parentItemId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var shareKey = ExtractShareKey(shareUrl);
        if (string.IsNullOrEmpty(shareKey) || !shareKey.Contains('-'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(shareUrl, "/(?:s|123pan)/([^/?#]+)");
            if (match.Success)
            {
                shareKey = match.Groups[1].Value.Replace(".html", string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (string.IsNullOrEmpty(shareKey))
        {
            return CloudListResult.Fail(CloudErrorKind.ParseFailure, "无法从分享链接中解析出 shareKey。");
        }

        var password = ExtractSharePassword(shareUrl);
        var url = ShareListEndpoint
            + "?limit=100&next=1&orderBy=share_id&orderDirection=desc"
            + "&shareKey=" + Uri.EscapeDataString(shareKey)
            + "&SharePwd=" + Uri.EscapeDataString(password ?? string.Empty)
            + "&ParentFileId=" + Uri.EscapeDataString(parentItemId ?? "0")
            + "&Page=1";

        try
        {
            using var response = await SendAsync(
                () => ShareRequest(HttpMethod.Get, url, credential, null),
                cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // 把 list 响应原始 JSON 写到日志（前 1500 字符），诊断 download_info API
            // 报 "请输入Etag/S3KeyFlag/Size" 时到底缺哪个字段、字段名实际是什么。
            // 上次报 "请输入Etag" 说明 list 响应里 Etag 字段为空或字段名不对，得看原文。
            Log.Info("list.response", "parent=" + parentItemId + " body=" + json.Substring(0, Math.Min(json.Length, 1500)));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (CloudJson.Code(root) is int code && code != 0)
            {
                return CloudListResult.Fail(
                    CloudErrorKind.AuthRequired,
                    "123 云盘拒绝了这次分享请求：" + (CloudJson.Message(root) ?? "需要登录态或提取码。"));
            }

            var files = new List<CloudFile>();
            if (CloudJson.TryObject(root, "data", out var data)
                && CloudJson.TryArray(data, "InfoList", out var list))
            {
                foreach (var item in list.EnumerateArray())
                {
                    CloudJson.TryString(item, "FileName", out var name);
                    if (name is null)
                    {
                        continue;
                    }

                    var id = (CloudJson.TryInt64(item, "FileId") ?? 0L).ToString();
                    var isDir = CloudJson.TryInt(item, "Type", out var type) && type == 1;
                    var size = CloudJson.TryInt64(item, "Size") ?? 0L;
                    // Etag + S3KeyFlag 也要从 list 响应里解析：download_info API 要 S3KeyFlag
                    // 必填非空，缺了报 "请输入S3keyFlag"。by-parent 路径的 ResolveShareItemAsync
                    // 必须从 CloudFile 上拿到这两个字段。
                    CloudJson.TryString(item, "Etag", out var etag);
                    CloudJson.TryString(item, "S3KeyFlag", out var s3KeyFlag);
                    // 单条 file 的字段值也写日志，方便对比 "list.response" 行与解析结果——
                    // 如果 list 响应有 Etag 但这里 etag 是 null，说明字段名不对（实际可能是 ETag/ETAG）。
                    Log.Info("list.entry", "name=" + name + " id=" + id + " size=" + size + " etag=[" + (etag ?? "<null>") + "] s3keyFlag=[" + (s3KeyFlag ?? "<null>") + "]");
                    files.Add(new CloudFile(id, name, isDir, size, Etag: etag, S3KeyFlag: s3KeyFlag));
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

    /// <summary>
    /// Resolves a file already located inside the share by its own id, instead of re-listing the
    /// share root and searching by name (spec 30). A nested file's name is not in the share root, so
    /// the generic path cannot find it; exchanging the id for a link directly can.
    ///
    /// <para>文件夹不走这里：整夹打包由 <see cref="ResolveShareFolderPackageAsync"/> 负责（spec v3.1）。</para>
    /// </summary>
    public override async Task<CloudResolveResult> ResolveShareItemAsync(
        string shareUrl,
        CloudFile item,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var shareKey = ResolveShareKey(shareUrl);
        if (string.IsNullOrEmpty(shareKey))
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "无法从分享链接中解析出 shareKey。");
        }

        try
        {
            // fileBody 必须带 size + s3keyFlag + etag 三个字段：123 云盘 download_info API
            // 校验三者必填非空，缺 size 报 "请输入Size"，缺 S3KeyFlag 报 "请输入S3keyFlag"，
            // 缺 etag 报 "请输入Etag"。by-parent 流程的 item 来自 ListShareFolderAsync
            // 返回的 CloudFile，那里已经把 Etag/S3KeyFlag/Size 都从 list 响应里解析进来了；
            // 如果 list 响应里某字段为空或字段名不对（实测可能就是这种情况），给非空占位
            // 让 API 通过校验：API 不用 etag/s3keyFlag 算签名或定位文件，仅校验存在与非空，
            // 下载 URL 是按 fileId 决定的，占位值不影响下载结果。
            var sizeForBody = item.SizeBytes is > 0 ? (long)item.SizeBytes : 1L;
            var etagForBody = !string.IsNullOrEmpty(item.Etag) ? item.Etag! : "0";
            var s3KeyForBody = !string.IsNullOrEmpty(item.S3KeyFlag) ? item.S3KeyFlag! : "0";
            var fileBody = "{\"driveId\":0"
                           + ",\"etag\":\"" + etagForBody + "\""
                           + ",\"fileId\":" + item.Id
                           + ",\"fileName\":\"" + item.Name + "\""
                           + ",\"s3keyFlag\":\"" + s3KeyForBody + "\""
                           + ",\"size\":" + sizeForBody
                           + ",\"type\":0}";
            // 请求 body 也写日志，下次出错能直接看到当时 etag/s3keyFlag 用的是真实值还是占位。
            Log.Info("download_info.request", "fileId=" + item.Id + " name=" + item.Name + " body=" + fileBody);
            using var fileResponse = await SendAsync(
                () => ShareRequest(
                    HttpMethod.Post,
                    DownloadInfoEndpoint + Pan123Signature.Encode("/api/file/download_info", "android", "55", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    credential,
                    fileBody),
                cancellationToken).ConfigureAwait(false);
            var downloadJson = await fileResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // 响应也写日志：API 报错时直接看到 message/code，不用再翻 install.failed 行。
            Log.Info("download_info.response", "body=" + downloadJson.Substring(0, Math.Min(downloadJson.Length, 1500)));
            return ParseDownloadUrl(downloadJson, item, false);
        }
        catch (HttpRequestException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
        }
    }

    /// <summary>
    /// 整个文件夹作为一个资源下载（spec v3.1）：123 的 <c>batch_download_share_info</c> 对文件夹
    /// fileId 返回服务端打包好的 zip 直链，调用方不需要把文件夹展开成一个个文件任务。产物是
    /// 「文件夹名.zip」，下载端不自动解压（zip 补后缀在 CloudShareResolver 统一处理）。
    /// </summary>
    public override async Task<CloudResolveResult> ResolveShareFolderPackageAsync(
        string shareUrl,
        CloudFile folder,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var shareKey = ResolveShareKey(shareUrl);
        if (string.IsNullOrEmpty(shareKey))
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, "无法从分享链接中解析出 shareKey。");
        }

        try
        {
            var body = "{\"shareKey\":\"" + shareKey + "\",\"fileIdList\":[{\"fileId\":" + folder.Id + "}]}";
            using var folderResponse = await SendAsync(
                () => ShareRequest(
                    HttpMethod.Post,
                    BatchDownloadEndpoint + Pan123Signature.Encode("/api/file/batch_download_share_info", "android", "55", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    credential,
                    body),
                cancellationToken).ConfigureAwait(false);
            return ParseDownloadUrl(
                await folderResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                folder,
                true);
        }
        catch (HttpRequestException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
        }
    }

    /// <summary>
    /// shareKey 的两种口径合一：先取带连字符的链接末段；取不到时再按 <c>/s/</c>、<c>/123pan/</c>
    /// 路径段兜底（文件与文件夹两个下载接口的既有行为）。
    /// </summary>
    private static string? ResolveShareKey(string shareUrl)
    {
        var shareKey = ExtractShareKey(shareUrl);
        if (!string.IsNullOrEmpty(shareKey) && shareKey.Contains('-'))
        {
            return shareKey;
        }

        var match = System.Text.RegularExpressions.Regex.Match(shareUrl, "/(?:s|123pan)/([^/?#]+)");
        return match.Success
            ? match.Groups[1].Value.Replace(".html", string.Empty, StringComparison.OrdinalIgnoreCase)
            : null;
    }

    /// <summary>Reads the direct link out of a download reply, or says why there is none.</summary>
    private static CloudResolveResult ParseDownloadUrl(string downloadJson, CloudFile item, bool isFolder)
    {
        try
        {
            using var document = JsonDocument.Parse(downloadJson);
            var root = document.RootElement;
            if (CloudJson.Code(root) is int code && code != 0)
            {
                return DownloadFailure(code, CloudJson.Message(root));
            }

            return ParseDownloadUrlBody(root, item, isFolder);
        }
        catch (JsonException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.ParseFailure, ex.Message);
        }
    }

    /// <summary>
    /// Says what a failed download reply really means. 123 answers <c>20101 未登录</c> when the request
    /// carried no author token — the file and the share are fine, the account is not signed in — so
    /// that has to read as "sign in first" rather than as a broken provider or a missing file
    /// (spec 10/40). The listing itself works without an account; only handing out the address needs one.
    /// </summary>
    private static CloudResolveResult DownloadFailure(int code, string? message)
        => code is 20101 or 401
            ? CloudResolveResult.Fail(
                CloudErrorKind.AuthRequired,
                "需要登录 123 云盘后才能取得下载地址（" + (message ?? "未登录") + "）。")
            : CloudResolveResult.Fail(
                CloudErrorKind.ProviderError,
                "下载接口返回错误：" + (message ?? "未知原因，可能需要有效登录态。"));

    private static CloudResolveResult ParseDownloadUrlBody(JsonElement root, CloudFile item, bool isFolder)
    {
        // data is not always an object — a refusal answers with null — and TryGetProperty on it would throw.
        if (CloudJson.TryObject(root, "data", out var data))
        {
            foreach (var key in new[] { "DownloadUrl", "DownloadURL" })
            {
                if (data.TryGetProperty(key, out var url) && url.ValueKind == JsonValueKind.String)
                {
                    var value = url.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        return CloudResolveResult.Ok(new CloudDownloadHandle(
                            item,
                            value,
                            Note: isFolder ? "文件夹打包直链（123 云盘）。" : "文件直链（123 云盘）。"));
                    }
                }
            }
        }

        return CloudResolveResult.Fail(CloudErrorKind.NotFound, "下载接口未返回 DownloadUrl。");
    }
}
