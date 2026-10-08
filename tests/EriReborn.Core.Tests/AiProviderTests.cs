using System.Net;
using System.Text;
using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Ai;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Which services the app can really talk to, and what it says when it cannot.
///
/// <para>
/// The provider list used to be a list of names: Claude sat in the same drop-down as DeepSeek and was
/// reached with the same OpenAI-shaped request, so it could only ever answer "not found" — a name in a
/// list standing in for support. These tests hold the line the other way: a service that speaks its own
/// protocol is spoken to in that protocol, and a capability is never claimed on a service's behalf.
/// </para>
///
/// <para>
/// A real call to these services is not possible here (no keys, and the point of the tests is the shape
/// of the request), so the endpoints are stubbed and the requests are read field by field — the same
/// approach the connectivity tests already took.
/// </para>
/// </summary>
public sealed class AiProviderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Claude_is_asked_in_anthropics_own_protocol()
    {
        HttpRequestMessage? seen = null;
        string? body = null;

        var handler = new StubHandler(async request =>
        {
            seen = request;
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return Json("""{"content":[{"type":"text","text":"先装运行库。"}]}""");
        });

        var provider = AiProviders.BuiltIn.Resolve("claude")!;
        var result = await provider.CompleteAsync(
            new HttpClient(handler),
            "https://api.anthropic.com/v1",
            "sk-ant-1",
            "claude-sonnet-4-5",
            "你是顾问。",
            "事实清单");

        Assert.True(result.Success, result.Message);
        Assert.Equal("先装运行库。", result.Text);

        // The address, the headers and the body all have to be Anthropic's, because that is the only shape
        // this service answers to.
        Assert.Equal("https://api.anthropic.com/v1/messages", seen!.RequestUri!.ToString());
        Assert.Equal("sk-ant-1", seen.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicAiProvider.ApiVersion, seen.Headers.GetValues("anthropic-version").Single());
        Assert.Null(seen.Headers.Authorization);
        Assert.DoesNotContain("chat/completions", body!);
        Assert.Contains("\"system\"", body!);
        Assert.Contains("max_tokens", body!);
    }

    [Fact]
    public async Task Claude_lists_models_with_its_own_headers()
    {
        HttpRequestMessage? seen = null;

        var handler = new StubHandler(request =>
        {
            seen = request;
            return Task.FromResult(Json("""{"data":[{"id":"claude-sonnet-4-5"}]}"""));
        });

        var provider = AiProviders.BuiltIn.Resolve("claude")!;
        var result = await provider.ProbeAsync(new HttpClient(handler), "https://api.anthropic.com/v1", "sk-ant-1");

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { "claude-sonnet-4-5" }, result.Models);
        Assert.Equal("https://api.anthropic.com/v1/models", seen!.RequestUri!.ToString());
        Assert.Equal(AnthropicAiProvider.ApiVersion, seen.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public void A_provider_that_is_offered_says_what_it_can_do()
    {
        foreach (var provider in AiProviders.BuiltIn.All)
        {
            // Either it is implemented and explains itself, or it is honest about not being implemented.
            if (provider.Capabilities.Kind == AiImplementationKind.NotImplemented)
            {
                Assert.False(provider.Capabilities.ChatCompletion);
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(provider.Capabilities.Note));
        }

        // And the one service that is not OpenAI-compatible does not claim to be.
        var claude = AiProviders.BuiltIn.Resolve("claude")!;
        Assert.Equal(AiImplementationKind.OfficialApi, claude.Capabilities.Kind);
        Assert.Equal(AiAuthStyle.ApiKeyHeader, claude.Capabilities.AuthStyle);

        var deepseek = AiProviders.BuiltIn.Resolve("deepseek")!;
        Assert.Equal(AiImplementationKind.CompatibleEndpoint, deepseek.Capabilities.Kind);
        Assert.Equal(AiAuthStyle.Bearer, deepseek.Capabilities.AuthStyle);
    }

    [Fact]
    public void An_unknown_service_resolves_to_nothing_rather_than_to_someone_else()
    {
        Assert.Null(AiProviders.BuiltIn.Resolve("没有这个服务"));
        Assert.Null(AiProviders.BuiltIn.Resolve(null));
        Assert.Null(AiProviders.BuiltIn.Resolve("   "));
    }

    [Fact]
    public async Task The_configuration_page_offers_the_registrys_providers_and_no_second_list()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "assets", "skins")))
        {
            directory = directory.Parent;
        }

        var assets = Path.Combine(directory!.FullName, "assets");

        var host = await AppHost.CreateAsync(
            AppPaths.Detect(assetsOverride: assets, userDataOverride: userData),
            new TestPlatform(
                new TestFileSystemService(userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore()),
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

        var ai = new AiViewModel(host);

        Assert.Equal(
            AiProviders.BuiltIn.All.Select(provider => provider.Id),
            ai.Presets.Select(preset => preset.Id));

        // Selecting a service explains it, so the page never shows a name with nothing behind it.
        Assert.False(string.IsNullOrWhiteSpace(ai.ProviderNote));
    }
}
