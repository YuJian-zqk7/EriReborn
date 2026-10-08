using System.Net;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Engine.Plugins;
using EriReborn.Platform.Abstractions;

// CloudProbe：live 网盘验证探针。直接实例化各 Provider，对真实分享链接跑
// ListShareAsync / ResolveAsync / ResolveInShareAsync，打印每一步结果。
// 只用于开发期诊断，不参与发布。

// 与宿主共享 client 保持一致：Cookie 关闭（各 Provider 自己处理会话）。
var handler = new HttpClientHandler
{
    UseCookies = false,
    AutomaticDecompression = DecompressionMethods.All,
};
// 记录型管道：打印 provider 实际发出的每个请求（行 + 头 + body）。
var logging = new LoggingHandler(handler);
var http = new HttpClient(logging) { Timeout = TimeSpan.FromSeconds(30) };

INetworkService network = new ProbeNetwork(http);
ICredentialStore credentials = new ProbeCredentials();

var cases = new (string Name, Func<ICloudProvider> Create, string ShareUrl)[]
{
    ("蓝奏云(文件夹)", () => new ProviderLanzou(network, credentials), "https://lanzoui.com/b0000test0"),
    ("夸克", () => new ProviderQuark(network, credentials), "https://pan.quark.cn/s/test0000test#/list/share"),
    ("迅雷", () => new ProviderXunlei(network, credentials), "https://pan.xunlei.com/s/VTest0000Test0000Test0001?pwd=0000"),
    ("百度", () => new ProviderBaidu(network, credentials), "https://pan.baidu.com/s/1Test0000Test0000Test0000test0-w#list/path=%2F"),
};

// 蓝奏单文件直链不再单独测：cases 循环里 ResolveInShareAsync 已用列表第一个文件页覆盖。

var filter = args.Length > 0 ? args[0] : null;
var failures = 0;

// 蓝奏诊断：用 .NET HttpClient 完整复刻 provider 流程，逐步打印。
if (filter == "lan-diag")
{
    Console.WriteLine("=== 蓝奏诊断 ===");
    string page;
    using (var pageReq = new HttpRequestMessage(HttpMethod.Get, "https://lanzoui.com/b0000test0"))
    {
        pageReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        using var pageResp = await http.SendAsync(pageReq);
        page = await pageResp.Content.ReadAsStringAsync();
    }
    Console.WriteLine($"页面长度: {page.Length}");
    Console.WriteLine($"含 arg1 挑战: {page.Contains("arg1='")}");
    Console.WriteLine($"含 filemoreajax: {page.Contains("filemoreajax")}");
    Console.WriteLine(page.Length < 700 ? $"页面全文: {page}" : "页面开头: " + page[..200].Replace("\n", " "));

    var tSite = System.Text.RegularExpressions.Regex.Match(page, @"'t':\s*'?([A-Za-z0-9_]+)'?");
    var kSite = System.Text.RegularExpressions.Regex.Match(page, @"'k':\s*'?([A-Za-z0-9_]+)'?");
    var fid = System.Text.RegularExpressions.Regex.Match(page, @"'fid':(\d+)");
    var uid = System.Text.RegularExpressions.Regex.Match(page, @"'uid':\s*'(\d+)'");
    var puid = System.Text.RegularExpressions.Regex.Match(page, @"'puid':\s*'([^']+)'");
    Console.WriteLine($"t 参数位: {tSite.Groups[1].Value}");
    Console.WriteLine($"k 参数位: {kSite.Groups[1].Value}");
    Console.WriteLine($"fid: {fid.Groups[1].Value}  uid: {uid.Groups[1].Value}");
    Console.WriteLine($"puid: {puid.Groups[1].Value[..Math.Min(40, puid.Groups[1].Value.Length)]}…");

    async Task<string> ResolveVar(string html, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            html, $@"\bvar\s+{System.Text.RegularExpressions.Regex.Escape(name)}\s*=\s*'([^']+)'");
        return m.Success ? m.Groups[1].Value : "";
    }

    var tValue = tSite.Groups[1].Value.Length > 0 && char.IsDigit(tSite.Groups[1].Value[0])
        ? tSite.Groups[1].Value
        : await ResolveVar(page, tSite.Groups[1].Value);
    var kValue = kSite.Groups[1].Value.Length > 0 && char.IsDigit(kSite.Groups[1].Value[0])
        ? kSite.Groups[1].Value
        : await ResolveVar(page, kSite.Groups[1].Value);
    Console.WriteLine($"t 值: {tValue}");
    Console.WriteLine($"k 值: {kValue}");

    var body = new Dictionary<string, string>
    {
        ["lx"] = "2",
        ["fid"] = fid.Groups[1].Value,
        ["uid"] = uid.Groups[1].Value,
        ["puid"] = puid.Groups[1].Value,
        ["pg"] = "1",
        ["rep"] = "0",
        ["t"] = tValue,
        ["k"] = kValue,
        ["up"] = "1",
    };

    using var req = new HttpRequestMessage(HttpMethod.Post, "https://lanzoui.com/filemoreajax.php?file=" + Uri.EscapeDataString(fid.Groups[1].Value))
    {
        Content = new FormUrlEncodedContent(body),
    };
    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
    req.Headers.TryAddWithoutValidation("Referer", "https://lanzoui.com/b0000test0");
    req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
    req.Headers.TryAddWithoutValidation("Origin", "https://lanzoui.com");

    using var resp = await http.SendAsync(req);
    var json = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"HTTP {(int)resp.StatusCode}; Content-Type: {resp.Content.Headers.ContentType}");
    Console.WriteLine($"响应前 300 字符: {json[..Math.Min(300, json.Length)]}");
    Console.WriteLine($"版本: {resp.Version}");
    return 0;
}

// 插件 JSON 解析验证：用与宿主一致的 PluginReader 走一遍，报告节点数与问题清单。
// 用法: CloudProbe plugin [json路径]；不传路径时验证重建的 user_resources。
if (filter == "plugin")
{
    var pluginPath = args.Length > 1
        ? args[1]
        : @"e:\harness\default-workspace\EriReborn\publish\win-x64-fixed\EriReborn-data\plugins\user_resources.eriplugin.json";
    Console.WriteLine($"=== 插件解析: {pluginPath} ===");
    var pluginJson = File.ReadAllText(pluginPath);
    var (plugin, pluginIssues) = PluginReader.Parse(pluginJson);
    if (plugin is null)
    {
        Console.WriteLine("[解析] 失败:");
        foreach (var issue in pluginIssues)
        {
            Console.WriteLine($"  - {issue.Code}: {issue.Message}");
        }
        return 1;
    }

    Console.WriteLine($"[解析] 成功: id={plugin.Id} name={plugin.Name} version={plugin.Version} schema={plugin.Schema}");
    foreach (var root in plugin.Nodes)
    {
        var kind = root is PluginGroupNode
            ? "group"
            : root is PluginResourceNode r ? r.Kind.ToString() : "?";
        Console.WriteLine($"  {kind} \"{root.Name}\" (id={root.NodeId}, children={root.Children.Count})");
    }
    Console.WriteLine($"  节点总数: {CountTree(plugin.Nodes)}");
    Console.WriteLine($"  问题数: {pluginIssues.Count}");
    foreach (var issue in pluginIssues)
    {
        Console.WriteLine($"  - {issue.Code}: {issue.Message} [{issue.Subject}]");
    }
    return 0;
}

static int CountTree(IReadOnlyList<PluginNode> nodes)
{
    var total = 0;
    foreach (var node in nodes)
    {
        total++;
        total += CountTree(node.Children);
    }
    return total;
}

foreach (var (name, create, shareUrl) in cases)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    Console.WriteLine();
    Console.WriteLine($"=== {name} ===");
    Console.WriteLine($"  分享链接: {shareUrl}");
    var provider = create();

    try
    {
        var list = await provider.ListShareAsync(shareUrl, null);
        if (!list.Success)
        {
            failures++;
            Console.WriteLine($"  [列表] 失败 ({list.Error}): {list.Message}");
        }
        else
        {
            Console.WriteLine($"  [列表] 成功，共 {list.Files.Count} 条:");
            foreach (var file in list.Files.Take(10))
            {
                var size = file.SizeBytes is { } s ? $"{s / 1024.0:F1}KB" : "-";
                Console.WriteLine($"    - {file.Name}  {(file.IsFolder ? "[文件夹]" : $"({size})")}  id={Truncate(file.Id, 60)}");
            }

            var firstFile = list.Files.FirstOrDefault(f => !f.IsFolder);
            if (firstFile is null && list.Files.FirstOrDefault(f => f.IsFolder) is { } sub)
            {
                // 列表只有文件夹：进入第一个子文件夹再找文件（测 ListShareFolderAsync）。
                Console.WriteLine($"  [子目录] 进入 {sub.Name} (id={sub.Id})");
                var subList = await provider.ListShareFolderAsync(shareUrl, sub.Id, null);
                if (!subList.Success)
                {
                    failures++;
                    Console.WriteLine($"  [子目录] 失败 ({subList.Error}): {subList.Message}");
                }
                else
                {
                    Console.WriteLine($"  [子目录] 成功，共 {subList.Files.Count} 条:");
                    foreach (var file in subList.Files.Take(5))
                    {
                        Console.WriteLine($"    - {file.Name}  {(file.IsFolder ? "[文件夹]" : "")}  id={Truncate(file.Id, 50)}");
                    }
                    firstFile = subList.Files.FirstOrDefault(f => !f.IsFolder);
                }
            }

            if (firstFile is not null)
            {
                // 模拟插件安装路径：locator 指向该文件，走 ResolveInShareAsync。
                var locator = new ResourceLocator
                {
                    Kind = ResourceLocatorKind.File,
                    ProviderItemId = firstFile.Id,
                    Name = firstFile.Name,
                };
                var resolve = await provider.ResolveInShareAsync(shareUrl, locator, null);
                if (!resolve.Success)
                {
                    failures++;
                    Console.WriteLine($"  [直链] 失败 ({resolve.Error}): {resolve.Message}");
                }
                else
                {
                    Console.WriteLine($"  [直链] 成功: {Truncate(resolve.Handle!.DownloadUrl, 100)}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"  [异常] {ex.GetType().Name}: {ex.Message}");
    }
}

// 百度：ListShareAsync 未实现（设计如此），但 ResolveAsync 有完整分享解析流程可实测。
if (filter is null || "百度".Contains(filter, StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine();
    Console.WriteLine("=== 百度(ResolveAsync) ===");
    try
    {
        var baidu = new ProviderBaidu(network, credentials);
        var resolve = await baidu.ResolveAsync(
            "https://pan.baidu.com/s/1Test0000Test0000Test0000test0-w", null, null);
        Console.WriteLine(resolve.Success
            ? $"  [直链] 成功: {Truncate(resolve.Handle!.DownloadUrl, 100)}"
            : $"  [直链] 失败 ({resolve.Error}): {resolve.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [异常] {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部通过" : $"有 {failures} 处失败");
return failures == 0 ? 0 : 1;

static string Truncate(string value, int max)
    => value.Length <= max ? value : value[..max] + "…";

/// <summary>最小 INetworkService：只暴露一个 HttpClient。</summary>
internal sealed class ProbeNetwork(HttpClient client) : INetworkService
{
    public HttpClient Client { get; } = client;
}

/// <summary>请求记录管道：打印方法/URL/头/body，用于 provider 与成功请求的逐头对照。</summary>
internal sealed class LoggingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Console.WriteLine($"--> {request.Method} {request.RequestUri}");
        foreach (var header in request.Headers)
        {
            Console.WriteLine($"    {header.Key}: {string.Join(", ", header.Value)}");
        }
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                Console.WriteLine($"    [content] {header.Key}: {string.Join(", ", header.Value)}");
            }
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"    [body] {body}");
        }

        var response = await base.SendAsync(request, cancellationToken);
        Console.WriteLine($"<-- {(int)response.StatusCode} {response.Version}");
        return response;
    }
}

/// <summary>内存凭据库：探针不带账号，所有需登录的调用应如实报"需要登录"。</summary>
internal sealed class ProbeCredentials : ICredentialStore
{
    public bool IsAvailable => true;

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
