namespace EriReborn.Engine.Ai;

/// <summary>
/// The AI services this build knows how to talk to.
///
/// <para>
/// Shaped like <c>CloudProviderRegistry</c> on purpose: the shared code resolves a provider by id and
/// never branches on a name, so the set can grow — a new service, a self-hosted gateway, an extension's
/// own backend — without editing the engine. What each provider can really do is carried on the provider
/// itself, so the interface can say "this one is not implemented" instead of offering a field that
/// cannot work.
/// </para>
/// </summary>
public sealed class AiProviderRegistry
{
    private readonly List<IAiProvider> _providers = new();

    /// <summary>Every registered provider, in the order they should be offered.</summary>
    public IReadOnlyList<IAiProvider> All => _providers;

    /// <summary>Adds a provider, or replaces the one with the same id.</summary>
    public void Add(IAiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var index = _providers.FindIndex(entry => string.Equals(entry.Id, provider.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            _providers[index] = provider;
        }
        else
        {
            _providers.Add(provider);
        }
    }

    public bool Remove(string id)
        => _providers.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)) > 0;

    /// <summary>The provider with this id, or null — never a silent fallback to someone else's protocol.</summary>
    public IAiProvider? Resolve(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : _providers.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
}

/// <summary>The providers that ship with the app.</summary>
public static class AiProviders
{
    /// <summary>
    /// The OpenAI-compatible client the earlier entry points delegate to.
    ///
    /// <para>
    /// Its id is <c>custom</c> and it carries no fixed address: the endpoint is always passed in by the
    /// caller, so this stands for "some OpenAI-compatible service" rather than for one particular company.
    /// </para>
    /// </summary>
    public const string CompatibleId = "custom";

    private static readonly AiProviderRegistry BuiltInRegistry = CreateBuiltIn();

    /// <summary>The registered providers, in the order the configuration page offers them.</summary>
    public static AiProviderRegistry BuiltIn => BuiltInRegistry;

    /// <summary>The shared OpenAI-compatible client, for callers that do not name a provider.</summary>
    public static IAiProvider OpenAiCompatible { get; } =
        BuiltInRegistry.Resolve(CompatibleId)
        ?? throw new InvalidOperationException("内置的 OpenAI 兼容 provider 缺失。");

    /// <summary>
    /// The shipped service whose default address this is, or null when no service in this build claims
    /// it. One rule, in one place: the configuration page restoring a saved endpoint and the smoke
    /// check naming the service it tested must not each decide this differently.
    /// </summary>
    public static IAiProvider? ForBaseUrl(string? baseUrl)
        => string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : BuiltInRegistry.All.FirstOrDefault(provider =>
                string.Equals(provider.DefaultBaseUrl, baseUrl.Trim(), StringComparison.OrdinalIgnoreCase));

    private static AiProviderRegistry CreateBuiltIn()
    {
        var registry = new AiProviderRegistry();

        registry.Add(new OpenAiCompatibleAiProvider(
            "openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"));

        registry.Add(new OpenAiCompatibleAiProvider(
            "deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"));

        registry.Add(new OpenAiCompatibleAiProvider(
            "qwen", "Qwen (DashScope)", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"));

        registry.Add(new OpenAiCompatibleAiProvider(
            "gemini", "Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.0-flash"));

        // The one service here that is not OpenAI-compatible, and so the one that needs its own protocol.
        registry.Add(new AnthropicAiProvider(
            "claude", "Claude", "https://api.anthropic.com/v1", "claude-sonnet-4-5"));

        registry.Add(new OpenAiCompatibleAiProvider(
            "ollama", "Ollama (本地)", "http://localhost:11434/v1", "llama3.1", requiresKey: false));

        registry.Add(new OpenAiCompatibleAiProvider(
            "lmstudio", "LM Studio (本地)", "http://localhost:1234/v1", "local-model", requiresKey: false));

        registry.Add(new OpenAiCompatibleAiProvider(
            CompatibleId, "OpenAI-compatible", "https://", string.Empty));

        return registry;
    }
}
