using System.Text.Json;

namespace EriReborn.Engine.Ai;

/// <summary>What came back. Success means text actually arrived, not that a request was sent.</summary>
public sealed record AiAnalysisResult(
    bool Success,
    string? Text,
    string Message,
    int? StatusCode = null)
{
    public static AiAnalysisResult Fail(string message, int? statusCode = null) =>
        new(false, null, message, statusCode);
}

/// <summary>
/// Asks a model to comment on this machine's environment.
///
/// <para>
/// It only ever <b>reads facts and returns prose</b>. It cannot change anything: there is no code path
/// from here to an install, a setting or a registry write, and that is deliberate — a model that can act
/// on a machine it does not fully understand is not a feature (spec 57/58).
/// </para>
///
/// <para>
/// The wording of these two prompts is this class's business; reaching the service is the provider's, so
/// the request itself goes through <see cref="OpenAiCompatibleAiProvider"/>. That is what keeps a
/// capability — "comment on this environment" — independent of which model answers it.
/// </para>
/// </summary>
public static class AiEnvironmentAnalyst
{
    /// <summary>
    /// The model is told what it is: a commentator on facts someone else gathered.
    /// Without this it tends to answer as if it had inspected the machine itself.
    /// </summary>
    internal const string SystemPrompt =
        "你是一名系统环境顾问。你只会收到一份关于某台电脑的事实清单，"
        + "你没有访问那台电脑的能力，也没有修改它的能力。"
        + "请只依据给出的事实分析：指出风险、缺口和优先顺序，不要臆测未给出的信息。"
        + "如果某个事实标注为无法读取，请把它当作未知，不要当作不存在。"
        + "用简洁的中文回答，最后给出最多 5 条可执行建议。";

    /// <summary>
    /// The prompt used when the model is asked to propose changes rather than to comment.
    ///
    /// <para>
    /// It is told that it is <b>proposing to a human</b>. A model asked for "what to install" answers with
    /// prose and a plan; one asked for "fix it" answers as if it had already done so, and the user is left
    /// reading a change log for changes that never happened.
    /// </para>
    /// </summary>
    internal const string ProposalSystemPrompt =
        "你是一名系统环境顾问。你只会收到一份关于某台电脑的事实清单，"
        + "你没有访问那台电脑的能力，也没有修改它的能力。"
        + "请依据事实提出不超过 10 项**待用户确认**的变更建议。"
        + "如果某个事实标注为无法读取，请当作未知，不要当作不存在。"
        + AiActionPlanner.ContractHint;

    /// <summary>Asks for comment only.</summary>
    public static Task<AiAnalysisResult> AnalyzeAsync(
        HttpClient client,
        string? baseUrl,
        string? apiKey,
        string? model,
        AiEnvironmentDigest digest,
        CancellationToken cancellationToken = default)
        => CompleteAsync(client, baseUrl, apiKey, model, digest, SystemPrompt, cancellationToken);

    /// <summary>Asks for a reviewable plan rather than prose.</summary>
    public static Task<AiAnalysisResult> ProposeAsync(
        HttpClient client,
        string? baseUrl,
        string? apiKey,
        string? model,
        AiEnvironmentDigest digest,
        CancellationToken cancellationToken = default)
        => CompleteAsync(client, baseUrl, apiKey, model, digest, ProposalSystemPrompt, cancellationToken);

    private static Task<AiAnalysisResult> CompleteAsync(
        HttpClient client,
        string? baseUrl,
        string? apiKey,
        string? model,
        AiEnvironmentDigest digest,
        string systemPrompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(client);

        return AiProviders.OpenAiCompatible.CompleteAsync(
            client, baseUrl, apiKey, model, systemPrompt, digest.Text, cancellationToken);
    }

    /// <summary>Reads <c>choices[0].message.content</c> from an OpenAI-compatible reply.</summary>
    internal static string? ParseContent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var errorMessage)
                && errorMessage.ValueKind == JsonValueKind.String)
            {
                // Some gateways answer 200 with an error object inside.
                return null;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
