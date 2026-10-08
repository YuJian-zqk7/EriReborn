using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EriReborn.Engine.Ai;

/// <summary>
/// A service reached through OpenAI's shape: <c>GET /models</c> and <c>POST /chat/completions</c>, with
/// the key in an <c>Authorization: Bearer</c> header.
///
/// <para>
/// This is the implementation most services need — OpenAI, DeepSeek, Qwen's compatibility mode, Gemini's
/// OpenAI endpoint, Ollama and LM Studio all answer to it — so it is one implementation instantiated with
/// different addresses and defaults rather than eight copies that drift. A service that genuinely speaks
/// something else gets its own implementation (see <see cref="AnthropicAiProvider"/>), because pretending
/// otherwise is how a drop-down entry becomes a feature that never works.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleAiProvider : IAiProvider
{
    /// <summary>How many model ids are echoed back into the probe's message.</summary>
    private const int SampleSize = 3;

    /// <summary>
    /// Output tokens asked for on every completion. Large enough for a full plugin draft
    /// (hundreds of entries with one-line reasons), small enough that no mainstream
    /// endpoint refuses it outright.
    /// </summary>
    internal const int OutputTokenBudget = 16384;

    /// <summary>The fallback for endpoints that cap max_tokens below <see cref="OutputTokenBudget"/>.</summary>
    internal const int ConservativeOutputTokenBudget = 4096;

    public OpenAiCompatibleAiProvider(
        string id,
        string displayName,
        string defaultBaseUrl,
        string defaultModel,
        bool requiresKey = true)
    {
        Id = id;
        DisplayName = displayName;
        DefaultBaseUrl = defaultBaseUrl;
        DefaultModel = defaultModel;
        RequiresKey = requiresKey;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string DefaultBaseUrl { get; }

    public string DefaultModel { get; }

    public bool RequiresKey { get; }

    public AiProviderCapabilities Capabilities { get; } = new(
        AiImplementationKind.CompatibleEndpoint,
        ModelListing: true,
        ChatCompletion: true,
        AiAuthStyle.Bearer,
        "使用 OpenAI 兼容的 /models 与 /chat/completions。");

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

        var endpoint = $"{address}/models";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

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
                : $"连接成功（HTTP {(int)response.StatusCode}），可用模型 {models.Count} 个：{string.Join(", ", models.Take(SampleSize))}"
                  + (models.Count > SampleSize ? " …" : "。");

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
            // A completion without a model name is a request that cannot succeed, and
            // sending it anyway would report the endpoint's complaint rather than ours.
            return AiAnalysisResult.Fail("模型名为空，未发起请求。");
        }

        // An omitted max_tokens lets each service apply its own default, and those defaults run
        // small — a 60-entry plugin draft does not fit in what many endpoints give unprompted, so
        // the answer stops mid-JSON and the draft dies at validation. Ask for a generous budget;
        // endpoints that cap below it refuse the request, and one retry at the conservative
        // figure still beats a draft that was never going to finish.
        var endpoint = $"{address}/chat/completions";
        var result = await CompleteOnceAsync(
            client, endpoint, apiKey, model, systemPrompt, userPrompt,
            OutputTokenBudget, cancellationToken).ConfigureAwait(false);

        if (!result.Success && result.StatusCode is 400 or 422)
        {
            result = await CompleteOnceAsync(
                client, endpoint, apiKey, model, systemPrompt, userPrompt,
                ConservativeOutputTokenBudget, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task<AiAnalysisResult> CompleteOnceAsync(
        HttpClient client,
        string endpoint,
        string? apiKey,
        string model,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = model.Trim(),
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            stream = false,
            max_tokens = maxTokens,
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AiAnalysisResult.Fail(
                    $"{AiDiagnostics.CompletionReason(response.StatusCode, "/chat/completions")}（HTTP {(int)response.StatusCode}）。"
                    + AiDiagnostics.Trailing(body, 300),
                    (int)response.StatusCode);
            }

            var text = AiEnvironmentAnalyst.ParseContent(body);
            if (string.IsNullOrWhiteSpace(text))
            {
                // Reporting success here would show an empty box over a machine that
                // is fine, and the user would have no idea what happened.
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
}

/// <summary>Turning a typed address into one a request can use, or saying why it cannot.</summary>
internal static class AiEndpoints
{
    /// <summary>
    /// True when <paramref name="baseUrl"/> is an absolute http(s) address; the trimmed form without a
    /// trailing slash comes back in <paramref name="address"/>. Refusing here rather than sending keeps
    /// the complaint ours instead of the endpoint's (spec 58).
    /// </summary>
    internal static bool TryNormalise(string? baseUrl, out string address, out string refusal)
    {
        address = string.Empty;
        refusal = string.Empty;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            refusal = "Base URL 为空，未发起请求。";
            return false;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            refusal = "Base URL 不是合法的 http(s) 绝对地址，未发起请求。";
            return false;
        }

        address = baseUrl.Trim().TrimEnd('/');
        return true;
    }
}
