using System.Net;
using System.Text;
using System.Text.Json;

namespace EriReborn.Engine.Ai;

/// <summary>
/// Anthropic's own protocol: <c>GET /models</c> and <c>POST /messages</c>, with the key in an
/// <c>x-api-key</c> header and a required <c>anthropic-version</c>.
///
/// <para>
/// This exists because Anthropic is not an OpenAI-compatible endpoint, and the preset used to say it was:
/// the drop-down offered Claude, the code sent <c>Authorization: Bearer</c> to
/// <c>/chat/completions</c>, and the service could only ever answer "not found". A name in a list is not
/// support, so a service that speaks differently gets an implementation that speaks it.
/// </para>
///
/// <para>
/// The reply shape differs too: the text is in <c>content[]</c> as typed parts rather than in
/// <c>choices[0].message.content</c>. Non-text parts (thinking, tool calls) are skipped rather than
/// stringified into the answer.
/// </para>
/// </summary>
public sealed class AnthropicAiProvider : IAiProvider
{
    /// <summary>The API version Anthropic requires on every call.</summary>
    public const string ApiVersion = "2023-06-01";

    /// <summary>
    /// Anthropic requires a completion length to be stated. 4096 cut a full plugin draft off
    /// mid-JSON — the model was still writing entries when the budget ended — so the budget now
    /// matches what the OpenAI-compatible path asks for (spec 213.6).
    /// </summary>
    public const int DefaultMaxTokens = 8192;

    private const string VersionHeader = "anthropic-version";

    public AnthropicAiProvider(string id, string displayName, string defaultBaseUrl, string defaultModel)
    {
        Id = id;
        DisplayName = displayName;
        DefaultBaseUrl = defaultBaseUrl;
        DefaultModel = defaultModel;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string DefaultBaseUrl { get; }

    public string DefaultModel { get; }

    public bool RequiresKey => true;

    public AiProviderCapabilities Capabilities { get; } = new(
        AiImplementationKind.OfficialApi,
        ModelListing: true,
        ChatCompletion: true,
        AiAuthStyle.ApiKeyHeader,
        "使用 Anthropic 原生 /messages 协议与 x-api-key 鉴权。");

    public async Task<AiProbeResult> ProbeAsync(
        HttpClient client,
        string? baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (!AiEndpoints.TryNormalise(baseUrl, out var address, out var refusal))
        {
            return AiProbeResult.Fail(refusal);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{address}/models");
            Authorise(request, apiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var models = AiConnectivityProbe.ParseModels(body);

            if (!response.IsSuccessStatusCode)
            {
                return AiProbeResult.Fail(
                    $"{AiDiagnostics.ProbeReason(response.StatusCode)}（HTTP {(int)response.StatusCode}）。"
                    + AiDiagnostics.OneLine(body, 160),
                    (int)response.StatusCode);
            }

            var message = models.Count == 0
                ? $"连接成功（HTTP {(int)response.StatusCode}），但响应中没有解析到模型列表。"
                : $"连接成功（HTTP {(int)response.StatusCode}），可用模型 {models.Count} 个：{string.Join(", ", models.Take(3))}"
                  + (models.Count > 3 ? " …" : "。");

            return new AiProbeResult(true, (int)response.StatusCode, message, models);
        }
        catch (HttpRequestException ex)
        {
            return AiProbeResult.Fail($"网络错误：{ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            return AiProbeResult.Fail($"请求超时或被取消：{ex.Message}");
        }
    }

    public async Task<AiAnalysisResult> CompleteAsync(
        HttpClient client,
        string? baseUrl,
        string? apiKey,
        string? model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!AiEndpoints.TryNormalise(baseUrl, out var address, out var refusal))
        {
            return AiAnalysisResult.Fail(refusal);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return AiAnalysisResult.Fail("模型名为空，未发起请求。");
        }

        var payload = JsonSerializer.Serialize(new
        {
            model = model.Trim(),
            max_tokens = DefaultMaxTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } },
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{address}/messages")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            Authorise(request, apiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 from this service is usually the model name rather than the address, so the shared
                // wording is followed by what the service said instead of guessing between the two.
                return AiAnalysisResult.Fail(
                    $"{AiDiagnostics.CompletionReason(response.StatusCode, "/messages")}（HTTP {(int)response.StatusCode}）。"
                    + AiDiagnostics.Trailing(body, 300),
                    (int)response.StatusCode);
            }

            var text = ParseContent(body);
            if (string.IsNullOrWhiteSpace(text))
            {
                return AiAnalysisResult.Fail("服务返回成功，但响应里没有分析内容。", (int)response.StatusCode);
            }

            return new AiAnalysisResult(true, text.Trim(), $"分析完成（HTTP {(int)response.StatusCode}）。", (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return AiAnalysisResult.Fail($"网络请求失败：{ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return AiAnalysisResult.Fail($"请求超时：{ex.Message}");
        }
    }

    /// <summary>Anthropic's key and version headers, which every call to it needs.</summary>
    private static void Authorise(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
        }

        request.Headers.TryAddWithoutValidation(VersionHeader, ApiVersion);
    }

    /// <summary>
    /// Reads the text out of <c>content[]</c>. Only <c>text</c> parts are joined: a thinking or tool-use
    /// part is not an answer, and pasting its JSON into the reply would look like the model said it.
    /// </summary>
    public static string? ParseContent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (part.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && !string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(text.GetString()))
                {
                    parts.Add(text.GetString()!);
                }
            }

            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
