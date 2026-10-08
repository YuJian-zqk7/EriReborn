using System.Net;
using System.Text;
using EriReborn.Core.Logging;
using EriReborn.Engine.Ai;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The AI page must not claim a connection it never made, and must leave the
/// user's endpoint configuration intact across restarts (spec 58/69).
/// </summary>
public sealed class AiConnectivityProbeTests
{
    private const string ModelsBody = """
    { "object": "list", "data": [ { "id": "gpt-4o-mini" }, { "id": "gpt-4o" }, { "id": "o3" }, { "id": "o4-mini" } ] }
    """;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public StubHandler(HttpStatusCode status, string body)
            : this(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") })
        {
        }

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        public List<string> Requests { get; } = new();

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(_respond());
        }
    }

    private static HttpClient Client(StubHandler handler) => new(handler);

    [Fact]
    public async Task A_reachable_endpoint_reports_the_models_it_returned()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ModelsBody);

        var result = await AiConnectivityProbe.ProbeAsync(Client(handler), "https://api.test/v1", "sk-1");

        Assert.True(result.Success, result.Message);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(4, result.Models.Count);
        Assert.Contains("gpt-4o-mini", result.Message);
        Assert.Equal("Bearer sk-1", handler.LastAuthorization);
    }

    [Fact]
    public async Task The_probe_calls_the_documented_models_endpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ModelsBody);

        await AiConnectivityProbe.ProbeAsync(Client(handler), "https://api.test/v1/", "sk-1");

        // A trailing slash must not produce a doubled separator.
        Assert.Equal("https://api.test/v1/models", Assert.Single(handler.Requests));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API Key 无效")]
    [InlineData(HttpStatusCode.Forbidden, "无权访问")]
    [InlineData(HttpStatusCode.TooManyRequests, "限流")]
    [InlineData(HttpStatusCode.NotFound, "没有 /models 接口")]
    public async Task A_rejected_call_explains_why(HttpStatusCode status, string expected)
    {
        var handler = new StubHandler(status, """{"error":{"message":"nope"}}""");

        var result = await AiConnectivityProbe.ProbeAsync(Client(handler), "https://api.test/v1", "sk-bad");

        Assert.False(result.Success);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task A_server_error_is_reported_as_a_failure()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "boom");

        var result = await AiConnectivityProbe.ProbeAsync(Client(handler), "https://api.test/v1", "sk-1");

        Assert.False(result.Success);
        Assert.Equal(500, result.StatusCode);
    }

    [Fact]
    public async Task A_success_without_a_model_list_says_so_instead_of_inventing_models()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"object":"list"}""");

        var result = await AiConnectivityProbe.ProbeAsync(Client(handler), "https://api.test/v1", "sk-1");

        Assert.True(result.Success);
        Assert.Empty(result.Models);
        Assert.Contains("没有解析到模型列表", result.Message);
    }

    [Fact]
    public async Task An_unreachable_endpoint_reports_a_network_error()
    {
        var handler = new StubHandler(() => throw new HttpRequestException("connection refused"));

        var result = await AiConnectivityProbe.ProbeAsync(Client(handler), "https://api.test/v1", "sk-1");

        Assert.False(result.Success);
        Assert.Contains("网络错误", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://api.test")]
    public async Task An_unusable_base_url_never_causes_a_request(string baseUrl)
    {
        var handler = new StubHandler(HttpStatusCode.OK, ModelsBody);

        var result = await AiConnectivityProbe.ProbeAsync(Client(handler), baseUrl, "sk-1");

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_local_endpoint_without_a_key_is_still_probed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ModelsBody);

        var result = await AiConnectivityProbe.ProbeAsync(Client(handler), "http://localhost:11434/v1", null);

        Assert.True(result.Success);
        Assert.Null(handler.LastAuthorization);
    }

    [Fact]
    public void Model_parsing_accepts_objects_and_bare_strings()
    {
        Assert.Equal(new[] { "a", "b" }, AiConnectivityProbe.ParseModels("""{"data":[{"id":"a"},{"id":"b"}]}"""));
        Assert.Equal(new[] { "x" }, AiConnectivityProbe.ParseModels("""{"data":["x"]}"""));
        Assert.Empty(AiConnectivityProbe.ParseModels("""{"data":[]}"""));
        Assert.Empty(AiConnectivityProbe.ParseModels("<html>"));
        Assert.Empty(AiConnectivityProbe.ParseModels(""));
    }

    [Fact]
    public void A_hand_edited_config_in_camel_case_is_not_silently_ignored()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = EriReborn.App.Shared.AppPaths.Detect(userDataOverride: userData);
            Directory.CreateDirectory(userData);

            // Exactly the shape a person would write by hand.
            File.WriteAllText(
                paths.UserConfigFile,
                """{"aiBaseUrl":"https://verify.example/v1","aiModel":"verify-model","includeSubcategoryFolder":false}""");

            var service = new EriReborn.App.Shared.Services.UserConfigService(paths, AppLog.For("Test"));
            var loaded = service.Load();

            Assert.Equal("https://verify.example/v1", loaded.AiBaseUrl);
            Assert.Equal("verify-model", loaded.AiModel);
            Assert.False(loaded.IncludeSubcategoryFolder);
        }
        finally
        {
            try
            {
                Directory.Delete(userData, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public void The_ai_endpoint_settings_survive_a_restart()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = EriReborn.App.Shared.AppPaths.Detect(userDataOverride: userData);
            var first = new EriReborn.App.Shared.Services.UserConfigService(paths, AppLog.For("Test"));
            first.Load();
            first.SetAiSettings("deepseek", "https://api.deepseek.com/v1", "deepseek-chat");

            // A fresh service instance reads the file again, as a restart would.
            var second = new EriReborn.App.Shared.Services.UserConfigService(paths, AppLog.For("Test"));
            var reloaded = second.Load();

            Assert.Equal("https://api.deepseek.com/v1", reloaded.AiBaseUrl);
            Assert.Equal("deepseek-chat", reloaded.AiModel);

            // The service is part of the configuration because a capability cannot infer the protocol
            // from an address: a gateway would otherwise be spoken to wrongly.
            Assert.Equal("deepseek", reloaded.AiProviderId);
        }
        finally
        {
            try
            {
                Directory.Delete(userData, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
