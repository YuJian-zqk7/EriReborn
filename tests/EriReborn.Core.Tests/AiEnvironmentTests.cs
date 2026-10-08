using System.Net;
using System.Text.Json;
using EriReborn.Engine.Ai;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The AI reads facts and writes prose. Two things must hold: the analysis is never
/// built on a truncated list it does not know about, and an empty answer is never
/// reported as one (spec 57/58).
/// </summary>
public sealed class AiEnvironmentTests
{
    private static AiEnvironmentFacts Facts() => new()
    {
        OperatingSystem = "Windows 11 24H2",
        Architecture = "x64",
        Processor = "12th Gen Intel Core i7",
        MemoryBytes = 17_179_869_184,
        FreeDiskBytes = 512_000_000_000,
        Graphics = new[] { "Intel UHD Graphics 770", "NVIDIA GeForce RTX 4060" },
        Storage = new[] { @"C:\ fixed，共 476.8 GB，可用 119.2 GB" },
        Installed = new[] { "Git 2.52", "VLC 3.0.21" },
        Missing = new[] { "Python 3.13" },
        Unknown = new[] { "某工具（无探测器）" },
        Unavailable = new[] { "安全启动状态" },
    };

    // ------------------------------------------------------------- the digest

    [Fact]
    public void The_digest_carries_the_facts_it_was_given()
    {
        var digest = AiEnvironmentDigestBuilder.Build(Facts());

        Assert.Contains("Windows 11 24H2", digest.Text);
        Assert.Contains("x64", digest.Text);
        Assert.Contains("Git 2.52", digest.Text);
        Assert.Contains("Python 3.13", digest.Text);
        Assert.Contains("某工具（无探测器）", digest.Text);
        Assert.False(digest.IsTruncated);
    }

    [Fact]
    public void Byte_counts_are_written_the_way_a_person_reads_them()
    {
        var digest = AiEnvironmentDigestBuilder.Build(Facts());

        // "17179869184" tells a reader nothing, and the model is a reader here.
        Assert.Contains("16 GB", digest.Text);
        Assert.Contains("476.8 GB", digest.Text);
        Assert.DoesNotContain("17179869184", digest.Text);
    }

    [Fact]
    public void A_fact_that_could_not_be_read_is_stated_rather_than_left_blank()
    {
        var digest = AiEnvironmentDigestBuilder.Build(Facts());

        // A missing line reads as "fine", and the analysis would then reason about a
        // machine that is not this one.
        Assert.Contains("无法读取", digest.Text);
        Assert.Contains("安全启动状态", digest.Text);
        Assert.Contains("不代表不存在", digest.Text);
    }

    [Fact]
    public void No_fact_lines_are_emitted_for_values_the_platform_did_not_have()
    {
        var digest = AiEnvironmentDigestBuilder.Build(new AiEnvironmentFacts
        {
            OperatingSystem = "Windows 11",
        });

        Assert.Contains("操作系统", digest.Text);
        Assert.DoesNotContain("处理器", digest.Text);
        Assert.DoesNotContain("内存", digest.Text);
    }

    [Fact]
    public void A_long_list_is_capped_and_the_omission_is_declared()
    {
        var many = Enumerable.Range(0, 100).Select(index => $"工具 {index}").ToList();
        var digest = AiEnvironmentDigestBuilder.Build(
            new AiEnvironmentFacts { Installed = many },
            new AiDigestLimits { MaxPerList = 10 });

        Assert.True(digest.IsTruncated);
        Assert.Contains("省略了", digest.Text);

        // The count is honest: 100 given, 10 shown.
        Assert.Equal(90, digest.OmittedCount);

        Assert.Contains("工具 0", digest.Text);
        Assert.DoesNotContain("工具 50", digest.Text);
    }

    [Fact]
    public void The_fact_count_covers_everything_including_what_was_omitted()
    {
        var digest = AiEnvironmentDigestBuilder.Build(Facts());

        // 4 scalar facts plus 2 + 1 + 1 + 1 listed entries, and the 2 adapters and 1 volume.
        Assert.Equal(13, digest.FactCount);
    }

    [Fact]
    public void The_digest_names_the_graphics_adapters_and_the_volumes()
    {
        var digest = AiEnvironmentDigestBuilder.Build(Facts());

        // "能装什么、装在哪" depends on both, and neither was in the facts before: an analysis asked
        // to plan an install without them is planning for a machine it was never told about.
        Assert.Contains("显卡（2 项）", digest.Text);
        Assert.Contains("Intel UHD Graphics 770", digest.Text);
        Assert.Contains("NVIDIA GeForce RTX 4060", digest.Text);
        Assert.Contains("存储卷（1 项）", digest.Text);
        Assert.Contains(@"C:\ fixed，共 476.8 GB，可用 119.2 GB", digest.Text);
    }

    [Fact]
    public void Adapters_and_volumes_are_left_out_when_the_platform_reported_none()
    {
        var digest = AiEnvironmentDigestBuilder.Build(new AiEnvironmentFacts
        {
            OperatingSystem = "Windows 11",
        });

        // A heading with nothing under it reads as an empty machine; "we could not read this" is
        // carried in Unavailable instead, where it is worded as such.
        Assert.DoesNotContain("显卡", digest.Text);
        Assert.DoesNotContain("存储卷", digest.Text);
    }

    [Fact]
    public void The_digest_is_deterministic()
    {
        var first = AiEnvironmentDigestBuilder.Build(Facts());
        var second = AiEnvironmentDigestBuilder.Build(Facts());

        Assert.Equal(first.Text, second.Text);
    }

    // ---------------------------------------------------------- the analysis

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public string? Body { get; private set; }

        public string? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Uri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    private static AiEnvironmentDigest Digest() => AiEnvironmentDigestBuilder.Build(Facts());

    [Fact]
    public async Task The_request_names_the_model_and_sends_both_messages()
    {
        var handler = new CapturingHandler(_ => Json("{\"choices\":[{\"message\":{\"content\":\"分析结果\"}}]}"));
        using var client = new HttpClient(handler);

        var result = await AiEnvironmentAnalyst.AnalyzeAsync(
            client,
            "https://ai.invalid/v1",
            "sk-test",
            "deepseek-chat",
            Digest());

        Assert.True(result.Success, result.Message);
        Assert.Equal("分析结果", result.Text);

        Assert.EndsWith("/chat/completions", handler.Uri);
        Assert.Equal("Bearer sk-test", handler.Authorization);

        using var sent = JsonDocument.Parse(handler.Body!);
        var root = sent.RootElement;

        Assert.Equal("deepseek-chat", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("没有修改它的能力", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Contains("Windows 11 24H2", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task An_empty_answer_is_a_failure_not_an_empty_success()
    {
        var handler = new CapturingHandler(_ => Json("{\"choices\":[{\"message\":{\"content\":\"   \"}}]}"));
        using var client = new HttpClient(handler);

        var result = await AiEnvironmentAnalyst.AnalyzeAsync(client, "https://ai.invalid/v1", "k", "m", Digest());

        // Showing an empty box over a machine that is fine tells the user nothing.
        Assert.False(result.Success);
        Assert.Contains("没有分析内容", result.Message);
    }

    [Fact]
    public async Task An_error_envelope_inside_a_200_is_not_treated_as_content()
    {
        var handler = new CapturingHandler(_ => Json("{\"error\":{\"message\":\"quota exceeded\"}}"));
        using var client = new HttpClient(handler);

        var result = await AiEnvironmentAnalyst.AnalyzeAsync(client, "https://ai.invalid/v1", "k", "m", Digest());

        Assert.False(result.Success);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API Key")]
    [InlineData(HttpStatusCode.TooManyRequests, "限流")]
    [InlineData(HttpStatusCode.NotFound, "Base URL")]
    public async Task A_refusal_says_which_kind_of_refusal_it_was(HttpStatusCode status, string expected)
    {
        var handler = new CapturingHandler(_ => Json("{}", status));
        using var client = new HttpClient(handler);

        var result = await AiEnvironmentAnalyst.AnalyzeAsync(client, "https://ai.invalid/v1", "k", "m", Digest());

        Assert.False(result.Success);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task A_missing_model_never_reaches_the_network()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("不应该发出请求。"));
        using var client = new HttpClient(handler);

        var result = await AiEnvironmentAnalyst.AnalyzeAsync(client, "https://ai.invalid/v1", "k", "  ", Digest());

        Assert.False(result.Success);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_missing_base_url_never_reaches_the_network()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("不应该发出请求。"));
        using var client = new HttpClient(handler);

        var result = await AiEnvironmentAnalyst.AnalyzeAsync(client, "", "k", "m", Digest());

        Assert.False(result.Success);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_non_json_body_is_reported_rather_than_crashing()
    {
        var handler = new CapturingHandler(_ => Json("<html>gateway</html>"));
        using var client = new HttpClient(handler);

        var result = await AiEnvironmentAnalyst.AnalyzeAsync(client, "https://ai.invalid/v1", "k", "m", Digest());

        Assert.False(result.Success);
    }
}
