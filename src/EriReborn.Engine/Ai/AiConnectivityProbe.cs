using System.Text.Json;

namespace EriReborn.Engine.Ai;

/// <summary>Outcome of checking whether an AI endpoint is reachable and usable.</summary>
public sealed record AiProbeResult(
    bool Success,
    int? StatusCode,
    string Message,
    IReadOnlyList<string> Models)
{
    public static AiProbeResult Fail(string message, int? statusCode = null) =>
        new(false, statusCode, message, Array.Empty<string>());
}

/// <summary>
/// Checks an AI endpoint for real by calling its OpenAI-compatible <c>/models</c> listing. Nothing is
/// reported as working unless the service actually answered (spec 58/69), and the failure text
/// distinguishes a bad key from a rate limit or a dead endpoint.
///
/// <para>
/// This is the check for an OpenAI-compatible service, and it now lives in
/// <see cref="OpenAiCompatibleAiProvider"/>: one implementation of one provider's capability, rather than
/// a second copy here that would drift from it. Callers that know which service they are talking to ask
/// the provider itself, because a service with its own protocol has its own check.
/// </para>
/// </summary>
public static class AiConnectivityProbe
{
    public static Task<AiProbeResult> ProbeAsync(
        HttpClient client,
        string? baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
        => AiProviders.OpenAiCompatible.ProbeAsync(client, baseUrl, apiKey, cancellationToken);

    /// <summary>Reads the OpenAI/Anthropic-compatible data[].id list.</summary>
    public static IReadOnlyList<string> ParseModels(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var models = new List<string>();
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    models.Add(entry.GetString()!);
                    continue;
                }

                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    models.Add(id.GetString()!);
                }
            }

            return models;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
