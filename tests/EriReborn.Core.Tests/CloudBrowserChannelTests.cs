using System.Net;
using System.Text;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// 蓝奏 serves a JavaScript challenge to plain HTTP clients, so the only honest way to read a share
/// page is to let a real browser run it. These tests pin that contract: without a browser the
/// provider must say why, and with one it must actually parse the rendered page.
/// </summary>
public sealed class CloudBrowserChannelTests
{
    private const string ShareUrl = "https://lanzoui.example.test/b0019iws";

    /// <summary>The ~1 KB anti-bot page the real host returns to curl.</summary>
    private const string Challenge =
        "<!DOCTYPE html><html><head><title></title>"
        + "<link rel=\"shortcut icon\" href=\"https://images.bakstotre.com/assets/favicon.ico\"></head>"
        + "<body><style>.off{text-align: center;font-size: 12px;color: #b3b3b3}</style></body></html>";

    /// <summary>What the same URL looks like once scripts have run.</summary>
    private const string RealPage =
        "<html><body><div class=\"file\"><iframe class=\"ifr2\" src=\"//lanzoui.example.test/file/abc\"></iframe>"
        + "</div></body></html>";

    private const string FrameHtml = "<html><body><script>var file='x';\nvar s='y';\n'";
    private const string FrameTail = "<html><body><script>'sign':'SIGN123'</script></body></html>";

    private sealed class ScriptedLanzou(bool challenge) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (request.Method == HttpMethod.Get && url == ShareUrl)
            {
                return Html(challenge ? Challenge : RealPage);
            }

            if (request.Method == HttpMethod.Get && url.Contains("/file/abc", StringComparison.Ordinal))
            {
                return Html(FrameTail);
            }

            if (url.Contains("ajaxm.php", StringComparison.Ordinal))
            {
                await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return Json("{\"zt\":1,\"dom\":\"https://dl.example.test\",\"url\":\"xyz\"}");
            }

            return Html("<html></html>");
        }

        private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FakeBrowser(string rendered) : ICloudBrowserChannel
    {
        public List<string> Visited { get; } = new();

        public bool IsAvailable => true;

        public Task<string?> FetchRenderedHtmlAsync(string url, CancellationToken cancellationToken = default)
        {
            Visited.Add(url);
            return Task.FromResult<string?>(rendered);
        }
    }

    private static ProviderLanzou Create(bool challenge, out FakeBrowser? browser)
    {
        var network = new TestNetworkService(new HttpClient(new ScriptedLanzou(challenge)));
        var credentials = new InMemoryCredentialStore();
        browser = null;
        return new ProviderLanzou(network, credentials);
    }

    [Fact]
    public async Task Without_a_browser_the_challenge_page_is_reported_as_such()
    {
        var provider = Create(challenge: true, out var browser);
        Assert.Null(browser);

        var result = await provider.ResolveAsync(ShareUrl, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("内置浏览器", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_browser_the_rendered_page_is_parsed_into_a_direct_link()
    {
        var network = new TestNetworkService(new HttpClient(new ScriptedLanzou(true)));
        var credentials = new InMemoryCredentialStore();
        var browser = new FakeBrowser(RealPage);
        var provider = new ProviderLanzou(network, credentials, browser);

        var result = await provider.ResolveAsync(ShareUrl, "示例文件.zip", null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("https://dl.example.test/file/xyz", result.Handle?.DownloadUrl);
        Assert.Contains(ShareUrl, browser.Visited);
    }

    [Fact]
    public async Task An_app_wide_browser_is_used_when_the_provider_owns_none()
    {
        var network = new TestNetworkService(new HttpClient(new ScriptedLanzou(true)));
        var credentials = new InMemoryCredentialStore();
        var browser = new FakeBrowser(RealPage);

        CloudBrowser.Install(browser);
        try
        {
            var provider = new ProviderLanzou(network, credentials);
            var result = await provider.ResolveAsync(ShareUrl, "示例文件.zip", null, CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.Equal("https://dl.example.test/file/xyz", result.Handle?.DownloadUrl);
            Assert.Contains(ShareUrl, browser.Visited);
        }
        finally
        {
            CloudBrowser.Install(null);
        }
    }

    [Fact]
    public async Task With_nothing_installed_the_provider_reports_the_challenge_again()
    {
        CloudBrowser.Install(null);
        var provider = Create(challenge: true, out _);

        var result = await provider.ResolveAsync(ShareUrl, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(CloudBrowser.IsAvailable);
        Assert.Contains("内置浏览器", result.Message ?? string.Empty, StringComparison.Ordinal);
    }
}
