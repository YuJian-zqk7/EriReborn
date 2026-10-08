using System.Net;
using System.Text;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The five first-stage cloud platforms are complete peers, and none of them may
/// report success for work it did not do (spec 26/27/76, and the "no fake
/// success" rule).
/// </summary>
public sealed class CloudProviderTests
{
    /// <summary>Exactly what the live xpan endpoint returns without a token.</summary>
    private const string LiveUnauthenticatedResponse = """{"errno":-6,"request_id":8653275738888897022}""";

    private const string DocumentedListResponse = """
    {
      "errno": 0,
      "request_id": 1,
      "list": [
        { "fs_id": 111, "path": "/apps/demo", "server_filename": "demo", "size": 0, "isdir": 1, "server_mtime": 1700000000 },
        { "fs_id": 222, "path": "/apps/demo/tool.zip", "server_filename": "tool.zip", "size": 5242880, "isdir": 0, "server_mtime": 1700000100, "md5": "abc" }
      ]
    }
    """;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static ProviderBaidu Baidu(HttpMessageHandler handler, string baseUrl)
        => new(
            new TestNetworkService(new HttpClient(handler)),
            new InMemoryCredentialStore(),
            baseUrl);

    /// <summary>夸克分享读取是「token → detail」两步，按端点回不同桩响应，并记录请求 URL 供断言。</summary>
    private sealed class QuarkShareHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            string body;
            if (url.Contains("file/download", StringComparison.Ordinal))
            {
                body = """{"status":200,"code":0,"data":[{"download_url":"https://cdn.quark.test/a.zip"}]}""";
            }
            else if (url.Contains("sharepage/token", StringComparison.Ordinal))
            {
                body = """{"status":200,"code":0,"data":{"stoken":"stok-1"}}""";
            }
            else
            {
                body = """
                {"status":200,"code":0,"data":{"list":[
                  {"fid":"fid-sub","file_name":"SubDir","dir":true,"size":0},
                  {"fid":"fid-file","file_name":"a.zip","dir":false,"size":123}
                ]}}
                """;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static ProviderQuark Quark(QuarkShareHandler handler)
        => new(new TestNetworkService(new HttpClient(handler)), new InMemoryCredentialStore());

    private static CloudCredential Token(string providerId) => new(providerId, Token: "test-token");

    private static IReadOnlyList<ICloudProvider> AllProviders()
    {
        var network = new TestNetworkService(new HttpClient());
        var credentials = new InMemoryCredentialStore();
        return new ICloudProvider[]
        {
            new Provider123(network, credentials),
            new ProviderBaidu(network, credentials),
            new ProviderQuark(network, credentials),
            new ProviderLanzou(network, credentials),
            new ProviderXunlei(network, credentials),
        };
    }

    [Fact]
    public void All_five_first_stage_providers_are_registered()
    {
        var registry = new CloudProviderRegistry(AllProviders(), AppLog.For("Test"));

        Assert.Equal(5, registry.Providers.Count);
        foreach (var id in CloudProviderIds.All)
        {
            Assert.NotNull(registry.Resolve(id));
        }
    }

    [Fact]
    public void No_provider_is_privileged()
    {
        var registry = new CloudProviderRegistry(AllProviders(), AppLog.For("Test"));

        // Display order is the declared list, with no reordering or promotion.
        Assert.Equal(CloudProviderIds.All, registry.InDisplayOrder().Select(p => p.Id).ToArray());

        // Every provider answers the same question the same way.
        foreach (var provider in registry.Providers)
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(provider.Id));

            // A provider that needs auth must say so with no credential present.
            if (provider.RequiresAuthentication)
            {
                Assert.Equal(CloudAuthState.AuthRequired, provider.GetAuthState(null));
            }
        }
    }

    [Fact]
    public void A_provider_declaring_no_implementation_must_say_why()
    {
        foreach (var provider in AllProviders())
        {
            Assert.True(
                Enum.IsDefined(provider.ImplementationKind),
                $"{provider.Id} declares an undefined implementation kind.");

            if (provider.ImplementationKind == CloudImplementationKind.NotImplemented)
            {
                // Declaring "not implemented" is only honest if it is stated.
                Assert.False(
                    string.IsNullOrWhiteSpace(provider.LimitationNote),
                    $"{provider.Id} reports NotImplemented without a limitation note.");
            }
        }
    }

    [Fact]
    public async Task A_provider_that_cannot_do_the_work_fails_instead_of_faking_success()
    {
        foreach (var provider in AllProviders())
        {
            if (provider.ImplementationKind != CloudImplementationKind.NotImplemented)
            {
                continue;
            }

            // Authenticated, so the only reason to fail is that it is not built.
            var credential = Token(provider.Id);
            var list = await provider.ListChildrenAsync("/", credential);
            var resolved = await provider.ResolveAsync("https://example.invalid/s/abc", null, credential);

            Assert.False(list.Success, $"{provider.Id} claimed a listing it cannot produce.");
            Assert.Empty(list.Files);
            Assert.NotEqual(CloudErrorKind.None, list.Error);

            Assert.False(resolved.Success, $"{provider.Id} claimed a download handle it cannot produce.");
            Assert.Null(resolved.Handle);
        }
    }

    [Fact]
    public async Task A_provider_that_needs_a_credential_refuses_without_one()
    {
        // 蓝奏云 share pages are public, so it does not require authentication;
        // the others must never call out without a credential.
        foreach (var provider in AllProviders())
        {
            if (!provider.RequiresAuthentication)
            {
                Assert.Equal(CloudAuthState.NotRequired, provider.GetAuthState(null));
                continue;
            }

            var list = await provider.ListChildrenAsync("/", null);

            Assert.False(list.Success);
            Assert.Equal(CloudErrorKind.AuthRequired, list.Error);
        }
    }

    [Fact]
    public void Baidu_classifies_the_envelope_the_live_endpoint_actually_returns()
    {
        // These bytes came from https://pan.baidu.com/rest/2.0/xpan/file?method=list&dir=/
        var result = ProviderBaidu.ParseFileList(LiveUnauthenticatedResponse);

        Assert.False(result.Success);
        Assert.Equal(CloudErrorKind.AuthRequired, result.Error);
        Assert.Contains("身份验证失败", result.Message);
    }

    [Theory]
    [InlineData(-6, CloudErrorKind.AuthRequired)]
    [InlineData(111, CloudErrorKind.AuthRequired)]
    [InlineData(-7, CloudErrorKind.Forbidden)]
    [InlineData(31064, CloudErrorKind.Forbidden)]
    [InlineData(31034, CloudErrorKind.NotFound)]
    [InlineData(-9999, CloudErrorKind.ProviderError)]
    public void Baidu_maps_errno_values_to_provider_neutral_errors(int errno, CloudErrorKind expected)
    {
        Assert.Equal(expected, ProviderBaidu.ClassifyErrno(errno));
    }

    [Fact]
    public void Baidu_parses_the_documented_list_shape()
    {
        var result = ProviderBaidu.ParseFileList(DocumentedListResponse);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Files.Count);

        var folder = result.Files[0];
        Assert.Equal("demo", folder.Name);
        Assert.True(folder.IsFolder);
        Assert.Equal("111", folder.Id);
        Assert.Equal("/apps/demo", folder.ParentPath);

        var file = result.Files[1];
        Assert.False(file.IsFolder);
        Assert.Equal(5242880, file.SizeBytes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000100), file.ModifiedAt);
    }

    [Fact]
    public void Baidu_reports_a_parse_failure_rather_than_throwing()
    {
        var result = ProviderBaidu.ParseFileList("<html>not json</html>");

        Assert.False(result.Success);
        Assert.Equal(CloudErrorKind.ParseFailure, result.Error);
    }

    [Fact]
    public void Baidu_reports_a_missing_list_as_a_provider_error()
    {
        var result = ProviderBaidu.ParseFileList("""{"errno":0,"request_id":1}""");

        Assert.False(result.Success);
        Assert.Equal(CloudErrorKind.ProviderError, result.Error);
    }

    [Fact]
    public async Task Baidu_calls_the_documented_endpoint_and_returns_real_entries()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DocumentedListResponse);
        var provider = Baidu(handler, "https://baidu.test");

        var result = await provider.ListChildrenAsync("/apps", Token(CloudProviderIds.Baidu));

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Files.Count);

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("https://baidu.test/rest/2.0/xpan/file?", request);
        Assert.Contains("method=list", request);
        Assert.Contains("dir=%2Fapps", request);
        Assert.Contains("access_token=test-token", request);
    }

    [Fact]
    public async Task Baidu_does_not_call_out_at_all_without_a_token()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DocumentedListResponse);
        var provider = Baidu(handler, "https://baidu.test");

        var result = await provider.ListChildrenAsync("/", null);

        Assert.False(result.Success);
        Assert.Equal(CloudErrorKind.AuthRequired, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Baidu_surfaces_an_application_error_even_though_http_succeeded()
    {
        var handler = new StubHandler(HttpStatusCode.OK, LiveUnauthenticatedResponse);
        var provider = Baidu(handler, "https://baidu.test");

        var result = await provider.ListChildrenAsync("/", Token(CloudProviderIds.Baidu));

        Assert.False(result.Success);
        Assert.Equal(CloudErrorKind.AuthRequired, result.Error);
    }

    [Fact]
    public async Task Baidu_still_honestly_declines_share_resolution()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var provider = Baidu(handler, "https://baidu.test");

        var result = await provider.ResolveAsync("https://pan.baidu.com/s/1abc", null, Token(CloudProviderIds.Baidu));

        Assert.False(result.Success);
        Assert.Null(result.Handle);
    }

    [Fact]
    public async Task Baidu_refuses_a_shared_folder_with_the_unified_actionable_message()
    {
        // 桩：分享页给出 shareid/share_uk，/share/list 只回一个文件夹。百度文件夹打包有 300MB
        // 上限且需登录转存，未真验不接能力位；拒绝文案必须与其它平台口径一致并给出退路。
        var handler = new BaiduShareFolderHandler();
        var provider = Baidu(handler, "https://baidu.test");

        var result = await provider.ResolveAsync("https://pan.baidu.com/s/1abc", null, Token(CloudProviderIds.Baidu));

        Assert.False(result.Success);
        Assert.Null(result.Handle);
        Assert.Equal(CloudErrorKind.Unsupported, result.Error);
        Assert.Contains("展开", result.Message);
        Assert.DoesNotContain("只支持", result.Message);
        Assert.False(provider.SupportsFolderPackage);
    }

    /// <summary>百度分享流程的两步桩：分享页 HTML 带 shareid/share_uk，/share/list 回单个文件夹。</summary>
    private sealed class BaiduShareFolderHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            string body = url.Contains("/share/list", StringComparison.Ordinal)
                ? """{"errno":0,"list":[{"path":"/Client","isdir":1}]}"""
                : """<html><script>{"shareid":42,"share_uk":7}</script></html>""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void Credential_keys_are_namespaced_per_provider()
    {
        Assert.Equal("cloud/123/token", CloudProviderBase.CredentialKey(CloudProviderIds.Pan123, "token"));
        Assert.Equal("cloud/baidu/cookie", CloudProviderBase.CredentialKey(CloudProviderIds.Baidu, "cookie"));

        var keys = AllProviders().Select(p => CloudProviderBase.CredentialKey(p.Id, "token")).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Quark_reads_a_share_subfolder_with_the_folder_fid_as_pdir_fid()
    {
        var handler = new QuarkShareHandler();
        var provider = Quark(handler);

        // 根目录与进入子目录走同一个已实测 detail 接口，只是 pdir_fid 不同。
        var root = await provider.ListShareAsync("https://pan.quark.cn/s/abcd1234", null);
        Assert.True(root.Success, root.Message);

        var sub = await provider.ListShareFolderAsync("https://pan.quark.cn/s/abcd1234", "fid-parent", null);
        Assert.True(sub.Success, sub.Message);
        Assert.Equal(2, sub.Files.Count);

        var folder = sub.Files[0];
        Assert.Equal("SubDir", folder.Name);
        Assert.True(folder.IsFolder);

        var file = sub.Files[1];
        Assert.Equal("fid-file", file.Id);
        Assert.False(file.IsFolder);
        Assert.Equal(123, file.SizeBytes);

        var detailCalls = handler.Requests
            .Where(url => url.Contains("sharepage/detail", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, detailCalls.Count);
        Assert.Contains("pdir_fid=0", detailCalls[0]);
        Assert.Contains("pdir_fid=fid-parent", detailCalls[1]);
    }

    [Fact]
    public async Task Quark_resolves_a_walked_nested_share_file_by_its_fid_without_re_listing_the_root()
    {
        // 树 walk 已逐层定位到嵌套文件（CloudFile.Id 即 fid）。默认实现会按名回查 pdir_fid=0，
        // 嵌套文件必然找不到；夸克 override 必须直接拿该 fid 请求 file/download。
        var handler = new QuarkShareHandler();
        var provider = Quark(handler);

        var result = await provider.ResolveShareItemAsync(
            "https://pan.quark.cn/s/abcd1234",
            new CloudFile("fid-deep", "Deep.zip", false, 777),
            Token(CloudProviderIds.Quark));

        Assert.True(result.Success, result.Message);
        Assert.Equal("https://cdn.quark.test/a.zip", result.Handle!.DownloadUrl);
        Assert.Equal("Deep.zip", result.Handle.File.Name);
        Assert.Equal(777, result.Handle.File.SizeBytes);

        Assert.DoesNotContain(handler.Requests, url => url.Contains("sharepage/token", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, url => url.Contains("sharepage/detail", StringComparison.Ordinal));
        Assert.Single(handler.Requests, url => url.Contains("file/download", StringComparison.Ordinal));
        Assert.Equal("""{"fids":["fid-deep"]}""", handler.LastBody);
    }

    [Fact]
    public void Only_a_platform_with_a_real_packaging_endpoint_advertises_folder_packages()
    {
        // 能力位必须与真实打包接口成对：只有 123（batch_download_share_info 已落地）可说支持；
        // 其余平台在真实验证通过前保持诚实的 false（spec v3.1，禁虚报）。
        var providers = AllProviders().ToDictionary(p => p.Id, StringComparer.Ordinal);

        Assert.True(providers[CloudProviderIds.Pan123].SupportsFolderPackage);

        foreach (var id in new[]
        {
            CloudProviderIds.Quark,
            CloudProviderIds.Baidu,
            CloudProviderIds.Xunlei,
            CloudProviderIds.Lanzou,
        })
        {
            Assert.False(providers[id].SupportsFolderPackage);
        }
    }

    [Fact]
    public async Task Quark_refuses_whole_folder_packages_with_an_actionable_message()
    {
        // 夸克没有官方整夹打包接口：即使作者在插件树里选了文件夹，下载端也必须诚实拒绝，
        // 绝不伪造一个文件直链。拒绝不产生任何网络请求。
        var handler = new QuarkShareHandler();
        var provider = Quark(handler);

        var result = await provider.ResolveShareFolderPackageAsync(
            "https://pan.quark.cn/s/abcd1234",
            new CloudFile("fid-client", "Client", true, 4096),
            Token(CloudProviderIds.Quark));

        Assert.False(result.Success);
        Assert.Null(result.Handle);
        Assert.Equal(CloudErrorKind.Unsupported, result.Error);
        Assert.Contains("打包下载", result.Message);
        Assert.Contains("展开", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Every_peer_without_a_packaging_endpoint_refuses_folder_packages_the_same_way()
    {
        // 123 是当前唯一接入真实打包接口的平台；其余四个对整夹下载必须给出同一口径的、
        // 可行动的 Unsupported 拒绝（spec v3.1 拍板①），且不得发出网络请求/返回直链。
        var peers = AllProviders()
            .Where(p => !p.SupportsFolderPackage)
            .ToList();

        Assert.Equal(4, peers.Count);

        foreach (var provider in peers)
        {
            var result = await provider.ResolveShareFolderPackageAsync(
                "https://example.invalid/s/abc",
                new CloudFile("fid-folder", "Client", true, 4096),
                null);

            Assert.False(result.Success);
            Assert.Null(result.Handle);
            Assert.Equal(CloudErrorKind.Unsupported, result.Error);
            Assert.Contains("打包下载", result.Message);
            Assert.Contains("展开", result.Message);
        }
    }
}
