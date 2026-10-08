using System.Net;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Baidu share resolution. The parts that could be verified against the live
/// service are, and they say so; everything else is pinned against a stub so the
/// handshake's order and parameters cannot drift unnoticed.
///
/// <para>
/// The handshake has <b>not</b> been run against a real share link and code,
/// because none were available. What is tested is that it asks in the right order,
/// passes the sekey through, and fails loudly at the step it cannot pass.
/// </para>
/// </summary>
public sealed class BaiduShareTests
{
    // ------------------------------------------------------- the share link

    [Theory]
    [InlineData("https://pan.baidu.com/s/1AbCdEfGhIj", "1AbCdEfGhIj", null)]
    [InlineData("https://pan.baidu.com/s/1AbCdEfGhIj?pwd=1234", "1AbCdEfGhIj", "1234")]
    [InlineData("https://pan.baidu.com/s/1AbCdEfGhIj#1234", "1AbCdEfGhIj", "1234")]
    [InlineData("1AbCdEfGhIj", "1AbCdEfGhIj", null)]
    [InlineData("1AbCdEfGhIj 1234", "1AbCdEfGhIj", "1234")]
    [InlineData("1AbCdEfGhIj:1234", "1AbCdEfGhIj", "1234")]
    [InlineData("https://pan.baidu.com/s/1AbCdEfGhIj?pwd=abcd", "1AbCdEfGhIj", "abcd")]
    public void A_pasted_link_is_taken_apart(string input, string expectedUrl, string? expectedPassword)
    {
        var reference = BaiduShareReference.Parse(input);

        Assert.NotNull(reference);
        Assert.Equal(expectedUrl, reference!.ShortUrl);
        Assert.Equal(expectedPassword, reference.Password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://pan.baidu.com/s/")]
    [InlineData("https://pan.baidu.com/s/1abc/extra")]
    public void A_link_that_is_not_a_share_is_refused_rather_than_guessed_at(string? input)
    {
        // Guessing would send a request to the wrong page and produce an error
        // nobody can interpret.
        Assert.Null(BaiduShareReference.Parse(input));
    }

    [Fact]
    public void A_foreign_host_is_still_read_as_a_short_url()
    {
        // Being strict about the host would reject the aliases people really paste.
        // It is safe to be lenient because the page url is always rebuilt against
        // Baidu — the host someone pasted is never contacted.
        var reference = BaiduShareReference.Parse("https://example.invalid/s/1abc");

        Assert.NotNull(reference);
        Assert.Equal("1abc", reference!.ShortUrl);
        Assert.StartsWith(BaiduShareReference.Host, reference.PageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_url_is_built_from_the_short_url()
    {
        var reference = BaiduShareReference.Parse("1AbCdEfGhIj");

        Assert.Equal("https://pan.baidu.com/s/1AbCdEfGhIj", reference!.PageUrl);
    }

    // ------------------------------------------------------ the error table

    [Fact]
    public void The_verified_errno_pins_the_handshake_order()
    {
        // Captured from the live service: /share/list without a sekey really does
        // answer this. It is why verifying first is not a guess.
        Assert.Equal(CloudErrorKind.AuthRequired, ProviderBaidu.ClassifyErrno(9019));
        Assert.Contains("sekey", ProviderBaidu.DescribeErrno(9019));
    }

    [Theory]
    [InlineData(105, CloudErrorKind.NotFound)]
    [InlineData(112, CloudErrorKind.NotFound)]
    [InlineData(118, CloudErrorKind.NotFound)]
    [InlineData(106, CloudErrorKind.Forbidden)]
    [InlineData(110, CloudErrorKind.Forbidden)]
    [InlineData(-6, CloudErrorKind.AuthRequired)]
    public void The_share_errno_values_map_to_something_actionable(int errno, CloudErrorKind expected)
    {
        Assert.Equal(expected, ProviderBaidu.ClassifyErrno(errno));
    }

    [Fact]
    public void Every_share_errno_has_a_sentence_not_a_code()
    {
        foreach (var errno in new[] { 2, 105, 106, 110, 112, 118, 9019 })
        {
            var text = ProviderBaidu.DescribeErrno(errno);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("errno=", text);
        }
    }

    // --------------------------------------------------- the real envelopes

    [Fact]
    public void The_real_list_envelope_is_read_including_its_message()
    {
        // Copied from a live call to /share/list without a sekey.
        const string live = "{\"errno\":9019,\"errmsg\":\"need verify\",\"request_id\":8679322116086959651}";

        Assert.True(ProviderBaidu.TryReadErrno(live, out var errno, out var message));
        Assert.Equal(9019, errno);
        Assert.Equal("need verify", message);
    }

    [Fact]
    public void The_real_verify_envelope_is_read()
    {
        // Copied from a live call to /share/verify with a bogus shareid.
        const string live = "{\"errno\":2,\"request_id\":8679322104738154346}";

        Assert.True(ProviderBaidu.TryReadErrno(live, out var errno, out var message));
        Assert.Equal(2, errno);
        Assert.Null(message);
    }

    [Fact]
    public void Fields_are_read_out_of_a_page_or_a_body()
    {
        const string page = "{\"shareid\":123456,\"uk\":789,\"title\":\"分享\"}";

        Assert.Equal("123456", ProviderBaidu.ExtractJsonNumber(page, "shareid"));
        Assert.Equal("789", ProviderBaidu.ExtractJsonNumber(page, "uk"));
        Assert.Equal("分享", ProviderBaidu.ExtractString(page, "title"));
        Assert.Null(ProviderBaidu.ExtractJsonNumber(page, "nope"));
    }

    // --------------------------------------------------------- the handshake

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.Method + " " + request.RequestUri);
            }

            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    private static ProviderBaidu Provider(ScriptedHandler handler)
        => new(new TestNetworkService(new HttpClient(handler)), new InMemoryCredentialStore());

    private const string PageWithShareId =
        "<html>{\"shareid\":123456,\"uk\":789}</html>";

    [Fact]
    public async Task The_handshake_verifies_before_it_lists_and_carries_the_sekey()
    {
        var verifyCalled = false;

        var handler = new ScriptedHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/s/", StringComparison.Ordinal))
            {
                return Html(PageWithShareId);
            }

            if (url.Contains("/share/verify", StringComparison.Ordinal))
            {
                verifyCalled = true;
                return Json("{\"errno\":0,\"randsk\":\"SEKEY123\"}");
            }

            if (url.Contains("/share/list", StringComparison.Ordinal))
            {
                // The list must not be asked before the code was exchanged.
                Assert.True(verifyCalled, "在 verify 之前就请求了 share/list。");
                Assert.Contains("sekey=SEKEY123", url);

                return Json("{\"errno\":0,\"list\":[{\"path\":\"/工具/tool.exe\",\"dlink\":\"https://d.pcs.baidu.com/file/x\",\"isdir\":0}]}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await Provider(handler).ResolveAsync("https://pan.baidu.com/s/1AbCdEfGhIj?pwd=1234", null, null);

        Assert.True(result.Success, result.Message);

        // The direct link only works with the sekey attached; handing over the bare
        // dlink produced a 403 that looks like a broken share.
        Assert.Contains("sekey=SEKEY123", result.Handle!.DownloadUrl);
    }

    [Fact]
    public async Task A_share_that_needs_verifying_says_so_instead_of_failing_generically()
    {
        var handler = new ScriptedHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/s/", StringComparison.Ordinal))
            {
                return Html(PageWithShareId);
            }

            // The real envelope, verbatim.
            return Json("{\"errno\":9019,\"errmsg\":\"need verify\",\"request_id\":1}");
        });

        // No password given, so the list is called without a sekey and the service
        // refuses — which is a fact about this share, not a bug in the caller.
        var result = await Provider(handler).ResolveAsync("1AbCdEfGhIj", null, null);

        Assert.False(result.Success);
        Assert.Equal(CloudErrorKind.AuthRequired, result.Error);
        Assert.Contains("need verify", result.Message);
    }

    [Fact]
    public async Task An_unknown_share_is_a_404_not_a_parse_error()
    {
        // Verified live: an unknown short url answers 404, not a 200 envelope.
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await Provider(handler).ResolveAsync("1AbCdEfGhIj", null, null);

        Assert.False(result.Success);
        Assert.Equal(CloudErrorKind.NotFound, result.Error);
        Assert.Contains("404", result.Message);
    }

    [Fact]
    public async Task A_wrong_extraction_code_is_reported_as_such()
    {
        var handler = new ScriptedHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/s/", StringComparison.Ordinal))
            {
                return Html(PageWithShareId);
            }

            return Json("{\"errno\":106}");
        });

        var result = await Provider(handler).ResolveAsync("1AbCdEfGhIj 0000", null, null);

        Assert.False(result.Success);
        Assert.Equal(CloudErrorKind.Forbidden, result.Error);
        Assert.Contains("提取码", result.Message);
    }

    [Fact]
    public async Task A_share_with_several_files_is_not_resolved_to_whichever_came_first()
    {
        var handler = new ScriptedHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/s/", StringComparison.Ordinal))
            {
                return Html(PageWithShareId);
            }

            return Json("{\"errno\":0,\"list\":["
                + "{\"path\":\"/a.exe\",\"dlink\":\"https://d/file/a\",\"isdir\":0},"
                + "{\"path\":\"/b.exe\",\"dlink\":\"https://d/file/b\",\"isdir\":0}]}");
        });

        var result = await Provider(handler).ResolveAsync("1AbCdEfGhIj", null, null);

        Assert.False(result.Success);
        Assert.Contains("指定", result.Message);
    }

    [Fact]
    public async Task Asking_for_a_file_by_name_picks_that_one()
    {
        var handler = new ScriptedHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/s/", StringComparison.Ordinal))
            {
                return Html(PageWithShareId);
            }

            return Json("{\"errno\":0,\"list\":["
                + "{\"path\":\"/a.exe\",\"dlink\":\"https://d/file/a\",\"isdir\":0},"
                + "{\"path\":\"/b.exe\",\"dlink\":\"https://d/file/b\",\"isdir\":0}]}");
        });

        var result = await Provider(handler).ResolveAsync("1AbCdEfGhIj", "b.exe", null);

        Assert.True(result.Success, result.Message);
        Assert.Contains("https://d/file/b", result.Handle!.DownloadUrl);
    }

    [Fact]
    public async Task A_page_without_a_share_id_says_the_page_changed()
    {
        var handler = new ScriptedHandler(_ => Html("<html>请登录</html>"));

        var result = await Provider(handler).ResolveAsync("1AbCdEfGhIj", null, null);

        Assert.False(result.Success);
        Assert.Equal(CloudErrorKind.ParseFailure, result.Error);
        Assert.Contains("shareid", result.Message);
    }

    [Fact]
    public async Task A_link_that_is_not_a_share_never_reaches_the_network()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("不应该发出请求。"));

        var result = await Provider(handler).ResolveAsync("https://example.invalid/thing", null, null);

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }
}
