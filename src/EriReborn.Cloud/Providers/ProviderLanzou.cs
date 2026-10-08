using System.Text;
using System.Text.RegularExpressions;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Cloud.Providers;

/// <summary>
/// 蓝奏云（2026 版页面结构）。
///
/// <para>文件夹分享页（/b…）的文件列表由内联 JS 通过 <c>filemoreajax.php</c> 动态加载，
/// 页面 HTML 里没有任何文件行；单文件页（/i…）被 acw_sc__v2（阿里 WAF）JS 挑战保护，
/// 直链通过 <c>ajaxfile.php</c>（wp_sign + ajaxdata 参数）签发。三段都是纯 HTTP 可复算的：
/// 列表用页面内新鲜参数直接 POST；挑战 cookie 用标准算法本地计算。因此这里报告
/// <see cref="CloudImplementationKind.HtmlParsing"/>，并在 UI 说明这一事实（spec 33）。</para>
///
/// <para>2026-10 重写：旧实现假设文件列表是静态 &lt;a&gt; 行、直链走 ajaxm.php，
/// 新版页面两条都变了，导致全部蓝奏资源"没有可识别的文件行"而失败。</para>
/// </summary>
public sealed partial class ProviderLanzou(
    INetworkService network,
    ICredentialStore credentials,
    ICloudBrowserChannel? browser = null)
    : CloudProviderBase(network, credentials, browser: browser)
{
    public override string Id => CloudProviderIds.Lanzou;

    public override string DisplayName => "蓝奏云";

    /// <summary>Direct-link resolution on a share page needs no account.</summary>
    public override bool RequiresAuthentication => false;

    public override CloudImplementationKind ImplementationKind => CloudImplementationKind.HtmlParsing;

    public override string? LimitationNote =>
        "当前实现基于网页结构解析（HTML），并非官方完整 API；页面结构变化会导致目录不完整或解析失败。"
        + "蓝奏分享文件夹是页面内逐个文件的下载链接，没有整包 zip 通道，请在插件中展开文件夹选择其中的文件（spec v3.1）。";

    public override string? DocumentationUrl => "https://www.lanzou.com/";

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

        try
        {
            var html = await GetPageAsync(shareUrl, cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                return CloudResolveResult.Fail(
                    CloudErrorKind.Unsupported,
                    "蓝奏云分享页是 JavaScript 挑战页，纯 HTTP 读不到内容：请先用内置浏览器打开一次该链接。");
            }

            if (html.Contains("文件取消分享", StringComparison.Ordinal) || html.Contains("文件不存在", StringComparison.Ordinal))
            {
                return CloudResolveResult.Fail(CloudErrorKind.NotFound, "分享已取消或文件不存在。");
            }

            var iframeMatch = IframeRegex().Match(html);
            if (!iframeMatch.Success)
            {
                return CloudResolveResult.Fail(
                    CloudErrorKind.ParseFailure,
                    "未在分享页中找到下载 iframe；蓝奏云页面结构可能已变化。");
            }

            var frameUrl = iframeMatch.Groups[1].Value;
            if (frameUrl.StartsWith("//", StringComparison.Ordinal))
            {
                frameUrl = "https:" + frameUrl;
            }
            else if (frameUrl.StartsWith('/'))
            {
                var origin = new Uri(shareUrl);
                frameUrl = $"{origin.Scheme}://{origin.Host}{frameUrl}";
            }

            var frameHtml = await GetPageAsync(frameUrl, cancellationToken).ConfigureAwait(false);
            if (frameHtml is null)
            {
                return CloudResolveResult.Fail(
                    CloudErrorKind.ParseFailure,
                    "下载页读不出来（挑战未通过）；蓝奏云解析规则需要更新。");
            }

            // 2026 版：/fn? 页内联 wp_sign + ajaxdata，直链由 apifile.*/ajaxfile.php 签发。
            if (TryParseAjaxFile(frameHtml, frameUrl, out var ajaxFile))
            {
                using var ajaxResponse = await SendAsync(
                    () => AjaxPost(ajaxFile.Url, ajaxFile.Referer, ajaxFile.Parameters),
                    cancellationToken).ConfigureAwait(false);
                var ajaxJson = await ajaxResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var direct = ParseDirectLink(ajaxJson);
                if (direct is null)
                {
                    return CloudResolveResult.Fail(
                        CloudErrorKind.ParseFailure,
                        "蓝奏云直链接口没有返回有效地址：" + Summarize(ajaxJson));
                }

                return CloudResolveResult.Ok(new CloudDownloadHandle(
                    new CloudFile(shareUrl, fileName ?? ExtractTitle(frameHtml) ?? "download", false),
                    direct,
                    IsHtmlParsed: true,
                    Note: "该直链由 HTML 解析得到，可能随蓝奏云页面结构变化而失效。"));
            }

            // 旧版（仍可能出现在部分域名）：sign → ajaxm.php。
            var signMatch = SignRegex().Match(frameHtml);
            if (!signMatch.Success)
            {
                return CloudResolveResult.Fail(
                    CloudErrorKind.ParseFailure,
                    "下载页缺少 sign 参数；蓝奏云解析规则需要更新。");
            }

            var ajaxmUrl = new Uri(frameUrl).GetLeftPart(UriPartial.Authority) + "/ajaxm.php";
            // Built fresh per attempt: a retry cannot reuse a sent body.
            using var response = await SendAsync(
                () => AjaxPost(ajaxmUrl, frameUrl, new Dictionary<string, string>
                {
                    ["action"] = "downprocess",
                    ["sign"] = signMatch.Groups[1].Value,
                    ["ves"] = "1",
                }),
                cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var legacyDirect = ParseDirectLink(json);
            if (legacyDirect is null)
            {
                return CloudResolveResult.Fail(CloudErrorKind.ProviderError, "蓝奏云拒绝了下拉请求。" + Summarize(json));
            }

            return CloudResolveResult.Ok(new CloudDownloadHandle(
                new CloudFile(shareUrl, fileName ?? ExtractTitle(frameHtml) ?? "download", false),
                legacyDirect,
                IsHtmlParsed: true,
                Note: "该直链由 HTML 解析得到，可能随蓝奏云页面结构变化而失效。"));
        }
        catch (HttpRequestException ex)
        {
            return CloudResolveResult.Fail(CloudErrorKind.Network, ex.Message);
        }
    }

    /// <summary>
    /// 文件夹分享（"/b…"）2026 版把文件列表放在 <c>filemoreajax.php</c> 的 JSON 里，
    /// 页面 HTML 只有一个空的 <c>&lt;div id="infos"&gt;</c>。这里解析内联 JS 里的
    /// fid/uid/puid/t/k 新鲜参数直接 POST 列表接口，翻页直到取完；旧式静态
    /// &lt;a&gt; 行保留为后备。
    /// </summary>
    public override async Task<CloudListResult> ListShareAsync(
        string shareUrl,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shareUrl))
        {
            return CloudListResult.Fail(CloudErrorKind.NotFound, "缺少分享链接。");
        }

        try
        {
            var html = await GetPageAsync(shareUrl, cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                return CloudListResult.Fail(
                    CloudErrorKind.Unsupported,
                    "蓝奏云分享页是 JavaScript 挑战页，纯 HTTP 读不到目录：请先用内置浏览器打开一次该链接。");
            }

            // 新版：页面内联 filemoreajax 参数 → 直接调列表接口。
            if (TryParseFolderAjax(html, shareUrl, out var folderAjax))
            {
                var files = new List<CloudFile>();
                for (var page = 1; page <= MaxListPages; page++)
                {
                    var parameters = new Dictionary<string, string>(folderAjax.Parameters);
                    parameters["pg"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    using var response = await SendAsync(
                        () => AjaxPost(folderAjax.Url, shareUrl, parameters),
                        cancellationToken).ConfigureAwait(false);
                    var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (!TryReadFolderPage(json, shareUrl, folderAjax.Base, files, out var noMore, out var message))
                    {
                        return CloudListResult.Fail(
                            page == 1 ? CloudErrorKind.NotFound : CloudErrorKind.ParseFailure,
                            message);
                    }

                    if (noMore)
                    {
                        break;
                    }
                }

                if (files.Count == 0)
                {
                    return CloudListResult.Fail(
                        CloudErrorKind.NotFound,
                        "这个蓝奏分享里没有文件（可能已被分享者清空）。");
                }

                return CloudListResult.Ok(files);
            }

            // 旧版：静态 <a> 行。
            var legacy = new List<CloudFile>();
            foreach (Match match in FolderRowRegex().Matches(html))
            {
                var href = match.Groups[1].Value;
                var name = match.Groups[2].Value;
                if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (IsNonFileLink(href, name))
                {
                    continue;
                }

                var absolute = href.StartsWith("//", StringComparison.Ordinal)
                    ? "https:" + href
                    : href.StartsWith('/')
                        ? new Uri(new Uri(shareUrl), href).ToString()
                        : href;

                legacy.Add(new CloudFile(absolute, name, IsFolderPageId(absolute), null, shareUrl));
            }

            if (legacy.Count == 0)
            {
                return CloudListResult.Fail(
                    CloudErrorKind.ParseFailure,
                    "已读到的蓝奏分享页里没有可识别的文件行（页面结构可能已变化）。");
            }

            return CloudListResult.Ok(legacy);
        }
        catch (HttpRequestException ex)
        {
            return CloudListResult.Fail(CloudErrorKind.Network, ex.Message);
        }
    }

    /// <summary>
    /// 蓝奏的分享内子目录页和根分享页是同一种页面（/b…），所以"进入下一层"
    /// 就是把该层的页面 URL 当作分享页再读一次。parentItemId 由
    /// <see cref="ListShareAsync"/> 生成的 CloudFile.Id 携带（完整页面 URL）。
    /// </summary>
    public override Task<CloudListResult> ListShareFolderAsync(
        string shareUrl,
        string? parentItemId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parentItemId))
        {
            return ListShareAsync(shareUrl, credential, cancellationToken);
        }

        // 子目录 Id 是完整页面 URL（https://host/bxxx）；容忍只有路径的形态。
        var folderUrl = parentItemId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? parentItemId
            : new Uri(new Uri(shareUrl), parentItemId).ToString();

        return ListShareAsync(folderUrl, credential, cancellationToken);
    }

    /// <summary>
    /// 走到分享内的具体条目后，直接对该条目的文件页（/i…，由
    /// <see cref="ListShareAsync"/> 写进 CloudFile.Id）做单文件解析，
    /// 不再按名字回分享根目录重找——根目录解析永远够不到子文件夹里的文件。
    /// </summary>
    public override Task<CloudResolveResult> ResolveShareItemAsync(
        string shareUrl,
        CloudFile item,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var pageUrl = item.Id.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? item.Id
            : new Uri(new Uri(shareUrl), item.Id).ToString();

        return ResolveAsync(pageUrl, item.Name, credential, cancellationToken);
    }

    // ---------- 页面获取与挑战 ----------

    /// <summary>
    /// 读取一个蓝奏页面；被 acw_sc__v2（阿里 WAF）JS 挑战拦截时，先用标准算法
    /// 本地算出 cookie 再取一次。本地算不出或仍被拦时，回退到浏览器渲染通道
    /// （provider 自有或全局安装的）；浏览器也不可用时返回 null，让调用方如实报告。
    /// </summary>
    private async Task<string?> GetPageAsync(string url, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var arg1 = AcwArgRegex().Match(html);
        if (!arg1.Success)
        {
            // 不是挑战页就是正常页面；是挑战页时本地没有 arg1 可解，交给浏览器渲染。
            return LooksLikeChallenge(html)
                ? await FetchRenderedAsync(url, cancellationToken).ConfigureAwait(false)
                : html;
        }

        var cookie = SolveAcwScV2(arg1.Groups[1].Value);
        if (cookie is null)
        {
            return await FetchRenderedAsync(url, cancellationToken).ConfigureAwait(false);
        }

        var retry = await GetStringAsync(url, cancellationToken, "acw_sc__v2=" + cookie).ConfigureAwait(false);
        return AcwArgRegex().Match(retry).Success
            ? await FetchRenderedAsync(url, cancellationToken).ConfigureAwait(false)
            : retry;
    }

    /// <summary>本地解不动挑战时的浏览器渲染回退；浏览器不可用或渲染为空时返回 null。</summary>
    private async Task<string?> FetchRenderedAsync(string url, CancellationToken cancellationToken)
    {
        var browser = ActiveBrowser;
        if (browser is not { IsAvailable: true })
        {
            return null;
        }

        var rendered = await browser.FetchRenderedHtmlAsync(url, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(rendered) ? null : rendered;
    }

    private async Task<string> GetStringAsync(
        string url,
        CancellationToken cancellationToken,
        string? cookie = null)
    {
        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                SetBrowserUserAgent(request);
                if (!string.IsNullOrEmpty(cookie))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", cookie);
                }

                return request;
            },
            cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 换成浏览器 UA：共享 client 自带 EriReborn UA（ParseAdd 进默认头），叠加会让
    /// 请求带两个 UA，蓝奏前的 WAF 会当异常流量处理。先移除再写一个。
    /// </summary>
    private static void SetBrowserUserAgent(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
    }

    /// <summary>
    /// Recognises the anti bot challenge instead of mistaking it for a page whose structure changed.
    /// acw_sc__v2 挑战页的特征是内联 arg1；旧式挑战页是带 .off{ 的短页。
    /// 不能再拿 bakstotre 当特征——那是蓝奏的静态资源域，正常页面同样带着它。
    /// </summary>
    private static bool LooksLikeChallenge(string html) =>
        html.Length > 0
        && !html.Contains("src=\"/fn?", StringComparison.OrdinalIgnoreCase)
        && (html.Contains("arg1='", StringComparison.Ordinal)
            || (html.Length < 4096 && html.Contains(".off{", StringComparison.Ordinal)));

    /// <summary>
    /// acw_sc__v2 标准算法：arg1 按固定位置表重排，再与固定 key 逐字节异或。
    /// 表与 key 都是挑战脚本里的常量，各家页面一致（实测 lanzoui.com 可用）。
    /// </summary>
    private static string? SolveAcwScV2(string arg1)
    {
        if (arg1.Length != AcwPositionTable.Length)
        {
            return null;
        }

        var reordered = new char[arg1.Length];
        for (var x = 0; x < arg1.Length; x++)
        {
            for (var z = 0; z < AcwPositionTable.Length; z++)
            {
                if (AcwPositionTable[z] == x + 1)
                {
                    reordered[z] = arg1[x];
                }
            }
        }

        var shuffled = new string(reordered);
        var result = new StringBuilder(shuffled.Length / 2);
        for (var x = 0; x + 1 < shuffled.Length && x + 1 < AcwKey.Length; x += 2)
        {
            var value = Convert.ToInt32(shuffled.Substring(x, 2), 16)
                        ^ Convert.ToInt32(AcwKey.Substring(x, 2), 16);
            result.Append(value.ToString("x2"));
        }

        return result.ToString();
    }

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private const int MaxListPages = 20;

    private static readonly int[] AcwPositionTable =
    {
        0xf, 0x23, 0x1d, 0x18, 0x21, 0x10, 0x1, 0x26, 0xa, 0x9,
        0x13, 0x1f, 0x28, 0x1b, 0x16, 0x17, 0x19, 0xd, 0x6, 0xb,
        0x27, 0x12, 0x14, 0x8, 0xe, 0x15, 0x20, 0x1a, 0x2, 0x1e,
        0x7, 0x4, 0x11, 0x5, 0x3, 0x1c, 0x22, 0x25, 0xc, 0x24,
    };

    private const string AcwKey = "3000176000856006061501533003690027800375";

    // ---------- 直链 ----------

    /// <summary>新版 /fn? 页的直链签发参数（wp_sign、ajaxdata、两个 ajaxfile 域名）。</summary>
    private sealed record AjaxFileInfo(string Url, string Referer, Dictionary<string, string> Parameters);

    private static bool TryParseAjaxFile(string frameHtml, string frameUrl, out AjaxFileInfo info)
    {
        info = null!;
        var sign = Regex.Match(frameHtml, @"wp_sign\s*=\s*'([^']+)'");
        var ajaxData = Regex.Match(frameHtml, @"ajaxdata\s*=\s*'([^']+)'");
        if (!sign.Success || !ajaxData.Success)
        {
            return false;
        }

        // 页面给了两个等价域名；先试 lanzouw，失败时调用方拿到的是第一个能用的。
        var domain2 = Regex.Match(frameHtml, @"domain2\s*=\s*'([^']+)'");
        var domain1 = Regex.Match(frameHtml, @"domain1\s*=\s*'([^']+)'");
        var url = domain2.Success ? domain2.Groups[1].Value
            : domain1.Success ? domain1.Groups[1].Value
            : null;
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        info = new AjaxFileInfo(url, frameUrl, new Dictionary<string, string>
        {
            ["action"] = "downprocess",
            ["websignkey"] = ajaxData.Groups[1].Value,
            ["signs"] = ajaxData.Groups[1].Value,
            ["sign"] = sign.Groups[1].Value,
            ["websign"] = string.Empty,
            ["kd"] = "1",
            ["ves"] = "1",
        });
        return true;
    }

    /// <summary>解析直链接口的 JSON：zt==1 时直链 = dom + "/file/" + url。</summary>
    private static string? ParseDirectLink(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;

            var status = ReadStatus(root);
            if (status != 1)
            {
                return null;
            }

            if (!root.TryGetProperty("dom", out var dom) || !root.TryGetProperty("url", out var path))
            {
                return null;
            }

            var domValue = dom.GetString();
            var pathValue = path.GetString();
            if (string.IsNullOrEmpty(domValue) || string.IsNullOrEmpty(pathValue))
            {
                return null;
            }

            return $"{domValue}/file/{pathValue}";
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ---------- 文件夹列表 ----------

    /// <summary>新版文件夹页的列表接口参数（fid/uid/puid/t/k 等内联 JS 新鲜值）。</summary>
    private sealed record FolderAjaxInfo(string Url, string Base, Dictionary<string, string> Parameters);

    private static bool TryParseFolderAjax(string html, string shareUrl, out FolderAjaxInfo info)
    {
        info = null!;
        var fid = Regex.Match(html, @"'fid':(\d+)");
        var fileRef = Regex.Match(html, @"filemoreajax\.php\?file=(\d+)");
        if (!fid.Success && !fileRef.Success)
        {
            return false;
        }

        var host = new Uri(shareUrl).GetLeftPart(UriPartial.Authority);
        var folderId = fid.Success ? fid.Groups[1].Value : fileRef.Groups[1].Value;

        // t/k 两个反爬参数在页面里可能是变量引用（'t':ibcxu1），先抓参数位上的
        // 标识符：纯数字就是字面量，变量名就去找它的 var 赋值。抓不到就放弃，
        // 让调用方走旧版静态行解析或如实报错。
        var tValue = ResolveInlineValue(html, FolderAjaxTRegex());
        var kValue = ResolveInlineValue(html, FolderAjaxKRegex());
        if (string.IsNullOrEmpty(tValue) || string.IsNullOrEmpty(kValue))
        {
            return false;
        }

        var parameters = new Dictionary<string, string>
        {
            ["lx"] = "2",
            ["fid"] = folderId,
            ["uid"] = FolderAjaxUidRegex().Match(html) is { Success: true } uid ? uid.Groups[1].Value : string.Empty,
            ["puid"] = FolderAjaxPuidRegex().Match(html) is { Success: true } puid ? puid.Groups[1].Value : string.Empty,
            ["pg"] = "1",
            ["rep"] = "0",
            ["t"] = tValue,
            ["k"] = kValue,
            ["up"] = "1",
        };

        info = new FolderAjaxInfo(
            host + "/filemoreajax.php?file=" + Uri.EscapeDataString(folderId),
            host,
            parameters);
        return true;
    }

    /// <summary>
    /// 参数位（如 "'t':ibcxu1"）→ 数字字面量直接用；变量名就在全文找
    /// "var 这个名字 = '值'" 的赋值（按变量名精准查找，不碰别的变量）。
    /// 两样都拿不到返回空串。
    /// </summary>
    private static string ResolveInlineValue(string html, Regex parameterSite)
    {
        var site = parameterSite.Match(html);
        if (!site.Success)
        {
            return string.Empty;
        }

        var value = site.Groups[1].Value;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (char.IsDigit(value[0]))
        {
            return value;
        }

        // 变量引用：找同名 var 赋值。每次解析至多调两次，不必预编译。
        var assignment = Regex.Match(
            html,
            $@"\bvar\s+{Regex.Escape(value)}\s*=\s*'([^']+)'",
            RegexOptions.None,
            TimeSpan.FromSeconds(2));
        return assignment.Success ? assignment.Groups[1].Value : string.Empty;
    }

    /// <summary>解析列表接口一页的 JSON；noMore 表示后面没有更多页了。</summary>
    private static bool TryReadFolderPage(
        string json,
        string shareUrl,
        string baseUrl,
        List<CloudFile> files,
        out bool noMore,
        out string message)
    {
        noMore = false;
        message = string.Empty;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            var status = ReadStatus(root);

            if (status == 2)
            {
                // "没有了"：第一页就是它 = 分享是空的；翻页翻到它 = 取完了。
                noMore = true;
                message = files.Count == 0 ? "这个蓝奏分享里没有文件（可能已被分享者清空）。" : string.Empty;
                return true;
            }

            if (status == 4)
            {
                // "请刷新，重试"：蓝奏的 t/k 反爬参数是一次性的，第一页消耗掉之后
                // 再用同一组翻页就会得到它——所以已经取到条目时它就是"取完了"；
                // 一页都没取到时它才是真的参数失效，如实报错。
                noMore = files.Count > 0;
                message = files.Count == 0 ? "蓝奏云目录接口拒绝请求（t/k 参数已失效）：" + Summarize(json) : string.Empty;
                return files.Count > 0;
            }

            if (status != 1 || !root.TryGetProperty("text", out var text) || text.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                message = "蓝奏云目录接口拒绝了这次请求：" + Summarize(json);
                return false;
            }

            foreach (var item in text.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElement))
                {
                    continue;
                }

                var id = idElement.GetString();
                if (string.IsNullOrEmpty(id) || id == "-1")
                {
                    continue;
                }

                var name = item.TryGetProperty("name_all", out var nameElement) ? nameElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // name_all 可能带内联的「推广」小标（<span …>），剥掉再给 UI。
                name = NameMarkupRegex().Replace(name, string.Empty).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                // 蓝奏的文件夹 id 以 b 开头（分享链接 /b… 同源），文件 id 以 i 开头。
                var isFolder = IsFolderPageId(id);
                var pageUrl = baseUrl + "/" + id;
                files.Add(new CloudFile(pageUrl, name, isFolder, null, shareUrl));
            }

            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            message = "蓝奏云目录接口返回了无法解析的内容。";
            return false;
        }
    }

    /// <summary>蓝奏接口的 zt 状态字段，数字和字符串两种形态都认。</summary>
    private static int ReadStatus(System.Text.Json.JsonElement root)
        => root.TryGetProperty("zt", out var zt)
            ? zt.ValueKind == System.Text.Json.JsonValueKind.Number
                ? zt.GetInt32()
                : int.TryParse(zt.GetString(), out var parsed) ? parsed : -1
            : -1;

    /// <summary>蓝奏的文件夹 id 以 b 开头（分享链接 /b… 同源），文件 id 以 i 开头。</summary>
    private static bool IsFolderPageId(string idOrUrl)
    {
        var id = idOrUrl;
        var slash = id.LastIndexOf('/');
        if (slash >= 0)
        {
            id = id[(slash + 1)..];
        }

        return id.StartsWith('b');
    }

    private static string? ExtractTitle(string html)
    {
        var title = Regex.Match(html, @"<title>([^<]+)</title>", RegexOptions.IgnoreCase);
        return title.Success ? title.Groups[1].Value.Trim() : null;
    }

    private static string Summarize(string json)
    {
        var trimmed = json.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120] + "…";
    }

    /// <summary>蓝奏 AJAX 都是表单 POST；统一的头集让接口认得出这是页面自己的调用。</summary>
    private static HttpRequestMessage AjaxPost(string url, string referer, Dictionary<string, string> parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(parameters),
        };
        SetBrowserUserAgent(request);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            request.Headers.TryAddWithoutValidation("Origin", uri.GetLeftPart(UriPartial.Authority));
        }

        return request;
    }

    /// <summary>
    /// 蓝奏云文件夹页面顶部有「登录」「注册」「用户主页」「客服」等链接，
    /// 它们也是 &lt;a href&gt;，旧版静态行解析会把它们抓成文件。按 URL 形态和
    /// 名字双重过滤掉已知的非文件链接（旧版页面后备路径仍在用）。
    /// </summary>
    private static bool IsNonFileLink(string href, string name)
    {
        var lower = href.ToLowerInvariant();

        // JavaScript / 锚点 / mailto：不算文件链接
        if (lower.StartsWith("javascript:", StringComparison.Ordinal)
            || lower.StartsWith("#", StringComparison.Ordinal)
            || lower.StartsWith("mailto:", StringComparison.Ordinal))
        {
            return true;
        }

        // 蓝奏云账户/登录/注册/帮助等已知非文件路径
        if (lower.Contains("account.php", StringComparison.Ordinal)
            || lower.Contains("action=login", StringComparison.Ordinal)
            || lower.Contains("action=register", StringComparison.Ordinal)
            || lower.Contains("action=logout", StringComparison.Ordinal)
            || lower.Contains("home.php", StringComparison.Ordinal)
            || lower.Contains("help.php", StringComparison.Ordinal)
            || lower.Contains("about.php", StringComparison.Ordinal)
            || lower.Contains("privacy.php", StringComparison.Ordinal)
            || lower.Contains("/u/", StringComparison.Ordinal)  // 用户主页
            || lower.Contains("feedback", StringComparison.Ordinal)
            || lower.Contains("agreement", StringComparison.Ordinal)
            || lower.Contains("/index.php", StringComparison.Ordinal))
        {
            return true;
        }

        // 名字黑名单：页面顶部的导航/账户按钮，文件不会有这种名字
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed == "登录" || trimmed == "注册" || trimmed == "退出"
            || trimmed == "登录/注册" || trimmed == "登录注册"
            || trimmed == "用户中心" || trimmed == "我的账户"
            || trimmed == "首页" || trimmed == "帮助"
            || trimmed == "联系客服" || trimmed == "客服"
            || trimmed == "繁體" || trimmed == "English"
            || trimmed == "客户端下载" || trimmed == "移动端")
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"<a[^>]+href=""(//[^""]+?/[^""]+?|/[^""]+?)""[^>]*>([^<]{1,200})</a>", RegexOptions.IgnoreCase)]
    private static partial Regex FolderRowRegex();

    [GeneratedRegex(@"src=""(/fn\?[^""]+|//[^""]*?file/[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex IframeRegex();

    [GeneratedRegex(@"'sign':'([^']+)'")]
    private static partial Regex SignRegex();

    [GeneratedRegex(@"arg1='([0-9A-Fa-f]+)'")]
    private static partial Regex AcwArgRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex NameMarkupRegex();

    // 文件夹页内联 JS 的列表接口参数位：数字字面量或变量名都由
    // ResolveInlineValue 二次判断（"'t':1791…" 与 "'t':ibcxu1"）。
    [GeneratedRegex(@"'t':\s*'?([A-Za-z0-9_]+)'?")]
    private static partial Regex FolderAjaxTRegex();

    [GeneratedRegex(@"'k':\s*'?([A-Za-z0-9_]+)'?")]
    private static partial Regex FolderAjaxKRegex();

    [GeneratedRegex(@"'uid':\s*'(\d+)'")]
    private static partial Regex FolderAjaxUidRegex();

    [GeneratedRegex(@"'puid':\s*'([^']+)'")]
    private static partial Regex FolderAjaxPuidRegex();
}
