namespace EriReborn.Engine.Ai;

/// <summary>
/// How much of a provider is really implemented here.
///
/// <para>
/// A name in a drop-down is not support. This says which of them the app can actually speak to, so a
/// service that has no implementation behind it can be shown as exactly that instead of failing later
/// with the endpoint's complaint — the difference between "Unknown" and "Missing" applied to providers
/// (spec 10/40).
/// </para>
/// </summary>
public enum AiImplementationKind
{
    /// <summary>Its own protocol, spoken here (e.g. Anthropic's /messages).</summary>
    OfficialApi,

    /// <summary>An OpenAI-compatible endpoint, reached through the shared client.</summary>
    CompatibleEndpoint,

    /// <summary>Listed, but nothing speaks to it. Never reported as working.</summary>
    NotImplemented,
}

/// <summary>How a service wants its key presented.</summary>
public enum AiAuthStyle
{
    /// <summary>No key at all — a local server (Ollama, LM Studio).</summary>
    None,

    /// <summary><c>Authorization: Bearer &lt;key&gt;</c>, what OpenAI-compatible endpoints expect.</summary>
    Bearer,

    /// <summary><c>x-api-key: &lt;key&gt;</c>, what Anthropic expects.</summary>
    ApiKeyHeader,
}

/// <summary>What a provider can really do, said plainly enough to show the user.</summary>
public sealed record AiProviderCapabilities(
    AiImplementationKind Kind,
    bool ModelListing,
    bool ChatCompletion,
    AiAuthStyle AuthStyle,
    string Note)
{
    /// <summary>False when nothing should be claimed about this service.</summary>
    public bool IsImplemented => Kind != AiImplementationKind.NotImplemented;
}

/// <summary>
/// One AI service the app can talk to.
///
/// <para>
/// Providers are a registry, not a switch: the shared code asks for a provider by id and never branches
/// on a name, so adding a service is adding an implementation rather than another <c>if</c> in the
/// engine (the same shape as cloud providers). Capabilities are the service's; what the app does with a
/// model — comment on an environment, propose changes, build a plugin draft — is the app's, and does not
/// live here (spec 58).
/// </para>
///
/// <para>
/// The endpoint is always passed in rather than baked into the implementation: a user pointing DeepSeek
/// at a gateway, or Ollama at another port, must not need a new provider to do it.
/// </para>
/// </summary>
public interface IAiProvider
{
    /// <summary>Stable id, and what a saved configuration refers to.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>What its own service uses when the user has not typed an address.</summary>
    string DefaultBaseUrl { get; }

    /// <summary>A model that service normally serves, offered as the default.</summary>
    string DefaultModel { get; }

    /// <summary>False for a local server that answers without a key.</summary>
    bool RequiresKey { get; }

    AiProviderCapabilities Capabilities { get; }

    /// <summary>Asks the service what models it has. Never reports success it did not receive.</summary>
    Task<AiProbeResult> ProbeAsync(
        HttpClient client,
        string? baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default);

    /// <summary>Asks for one completion, and returns the text only when text actually arrived.</summary>
    Task<AiAnalysisResult> CompleteAsync(
        HttpClient client,
        string? baseUrl,
        string? apiKey,
        string? model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}
