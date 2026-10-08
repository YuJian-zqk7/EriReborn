using EriReborn.Engine.Plugins;

namespace EriReborn.Engine.Ai;

/// <summary>
/// The kinds of work the app asks a model to do.
///
/// <para>
/// These are capabilities, not services. One configured provider serves all of them, which is the
/// whole point: switching from DeepSeek to Qwen to a local Ollama must not mean a different code
/// path per combination (spec 213.71). A capability that needed its own provider copy would be a
/// second system pretending to be a feature.
/// </para>
/// </summary>
public enum AiCapabilityKind
{
    /// <summary>Ordinary question and answer. Not what the AI page is for (spec 86/16).</summary>
    Chat,

    /// <summary>Turns a real share listing into a plugin draft (spec P0-3).</summary>
    PluginGenerator,

    /// <summary>Multi-turn tree generator: each turn revises one complete draft tree (spec v3.1).</summary>
    PluginTreeGenerator,

    /// <summary>Describes what a set of real resources actually is.</summary>
    ResourceAnalyzer,

    /// <summary>Comments on this machine's environment, and proposes reviewable changes.</summary>
    EnvironmentAnalyst,
}

/// <summary>
/// What to ask about: the data someone else gathered, plus an optional intent.
///
/// <para>
/// The <paramref name="Text"/> is never invented here. It is a digest of facts collected elsewhere —
/// a share listing read from the platform, a screen of environment facts — because a capability that
/// went and fetched its own data would be a second data path beside the real one (spec 213.6).
/// </para>
/// </summary>
public sealed record AiCapabilityInput(string Text, string? Intent = null);

/// <summary>The two prompts one request is made of. Building them touches no network.</summary>
public sealed record AiCapabilityRequest(string SystemPrompt, string UserPrompt);

/// <summary>
/// One thing the app can ask a model to do, independent of which service answers.
///
/// <para>
/// A capability owns the wording and the contract; a provider owns the protocol. Neither knows the
/// other's business, which is what lets the model be swapped without touching this side (spec 58).
/// </para>
/// </summary>
public interface IAiCapability
{
    AiCapabilityKind Kind { get; }

    string DisplayName { get; }

    /// <summary>What this capability does, in the words the configuration page shows.</summary>
    string Description { get; }

    /// <summary>Builds the prompts for this request. Never performs I/O.</summary>
    AiCapabilityRequest Build(AiCapabilityInput input);
}

/// <summary>The capabilities this build has, resolved by kind and never by name (spec 161).</summary>
public sealed class AiCapabilityRegistry
{
    private readonly List<IAiCapability> _capabilities = new();

    public IReadOnlyList<IAiCapability> All => _capabilities;

    /// <summary>Adds a capability, or replaces the one with the same kind.</summary>
    public void Add(IAiCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        var index = _capabilities.FindIndex(entry => entry.Kind == capability.Kind);
        if (index >= 0)
        {
            _capabilities[index] = capability;
        }
        else
        {
            _capabilities.Add(capability);
        }
    }

    public bool Remove(AiCapabilityKind kind)
        => _capabilities.RemoveAll(entry => entry.Kind == kind) > 0;

    public IAiCapability? Resolve(AiCapabilityKind kind)
        => _capabilities.FirstOrDefault(entry => entry.Kind == kind);
}

/// <summary>Which service to talk to, and with what. The key comes from the credential store.</summary>
public sealed record AiEndpointSettings(
    string? ProviderId,
    string? BaseUrl,
    string? Model,
    string? ApiKey);

/// <summary>
/// Runs a capability against the configured provider.
///
/// <para>
/// This is the piece that used to be missing: the analysis path reached for the OpenAI-compatible
/// client directly, so a service with its own protocol — Claude — could be configured and tested and
/// then quietly answered through the wrong one. Here the provider comes from the registry by id, so
/// the protocol follows the configuration and no code changes when the service does (spec 58/71).
/// </para>
/// </summary>
public sealed class AiService(AiProviderRegistry providers, AiCapabilityRegistry capabilities)
{
    /// <summary>The registry's own instance, for pages that do not need to compose their own.</summary>
    public static AiService Default { get; } = new(AiProviders.BuiltIn, AiCapabilities.BuiltIn);

    public IReadOnlyList<IAiCapability> Capabilities => capabilities.All;

    public IAiCapability? Resolve(AiCapabilityKind kind) => capabilities.Resolve(kind);

    /// <summary>
    /// Asks for one completion. Every failure that can be decided here is decided here, so the
    /// caller gets our words rather than an endpoint's complaint about a request never made.
    /// </summary>
    public async Task<AiAnalysisResult> RunAsync(
        AiCapabilityKind kind,
        AiEndpointSettings settings,
        AiCapabilityInput input,
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(client);

        if (providers.Resolve(settings.ProviderId) is not { } provider)
        {
            return AiAnalysisResult.Fail($"没有接入 AI 服务商「{settings.ProviderId ?? "(空)"}」，未发起请求。");
        }

        if (capabilities.Resolve(kind) is not { } capability)
        {
            return AiAnalysisResult.Fail($"这个构建没有「{kind}」这项能力，未发起请求。");
        }

        var request = capability.Build(input);

        return await provider
            .CompleteAsync(
                client,
                settings.BaseUrl,
                settings.ApiKey,
                settings.Model,
                request.SystemPrompt,
                request.UserPrompt,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>The capabilities that ship with the app.</summary>
public static class AiCapabilities
{
    private static readonly AiCapabilityRegistry BuiltInRegistry = CreateBuiltIn();

    public static AiCapabilityRegistry BuiltIn => BuiltInRegistry;

    private static AiCapabilityRegistry CreateBuiltIn()
    {
        var registry = new AiCapabilityRegistry();
        registry.Add(new ChatCapability());
        registry.Add(new PluginGeneratorCapability());
        registry.Add(new PluginTreeGeneratorCapability());
        registry.Add(new ResourceAnalyzerCapability());
        registry.Add(new EnvironmentAnalystCapability());
        return registry;
    }
}

/// <summary>Ordinary question and answer.</summary>
public sealed class ChatCapability : IAiCapability
{
    public AiCapabilityKind Kind => AiCapabilityKind.Chat;

    public string DisplayName => "通用问答";

    public string Description =>
        "把一段文字交给模型回答。AI 页面本身不是聊天客户端，这一项只作为其它能力的基础而存在。";

    private const string SystemPrompt =
        "你是 EriReborn 的助手。回答要简洁、直接。"
        + "你不了解你没有被给出的信息：不确定就说不确定，不要把推测说成事实。"
        + "你没有修改这台电脑的能力。";

    public AiCapabilityRequest Build(AiCapabilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new AiCapabilityRequest(SystemPrompt, input.Text ?? string.Empty);
    }
}

/// <summary>
/// Turns a real share listing into a plugin draft.
///
/// <para>
/// The model is shown only what the platform reported, and is held to it by
/// <see cref="PluginDraftValidator"/>. It cannot see an account, a token or a download address, and
/// it does not get to invent one (spec P0-3).
/// </para>
/// </summary>
public sealed class PluginGeneratorCapability : IAiCapability
{
    public AiCapabilityKind Kind => AiCapabilityKind.PluginGenerator;

    public string DisplayName => "插件生成（Plugin Generator）";

    public string Description =>
        "读取一个网盘分享的真实目录树，让模型把里面的条目整理成一份可审阅的插件草稿；"
        + "模型只能引用真实存在的条目，不能编造文件、大小、哈希或下载地址。";

    // Not const: it carries PluginDraftValidator.ContractHint, which states the real entry limit and is
    // therefore read from that limit rather than typed twice.
    internal static readonly string SystemPrompt =
        "你是 EriReborn Plugin Generator。"
        + "你的任务不是进行普通聊天，而是根据 EriReborn 提供的真实云端资源数据，生成结构化 Plugin Draft。"
        + "你只能使用输入 items 中真实存在的资源。"
        + "严格禁止：编造不存在的文件或文件夹；编造文件大小；编造 SHA256；编造下载 URL；编造网盘平台；"
        + "编造资源在分享里的位置；编造 Token、Cookie 或账号信息；修改用户提供的位置；把分享链接当成具体文件；"
        + "把一个分享强行当成一个资源；把多个真实资源合并成不存在的资源；"
        + "只因为你无法理解就删掉真实存在的资源；在信息不足时猜测。"
        + "无法确定时写 Unknown，Unknown 不等于 Missing。"
        + "每一条资源都必须引用输入 items 里真实存在的 candidateId。"
        + "你不负责下载、不负责登录云盘、不负责修改系统、不负责执行安装。你只生成 Plugin Draft。"
        + PluginDraftValidator.ContractHint;

    public AiCapabilityRequest Build(AiCapabilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var prompt =
            "下面是 EriReborn 从网盘平台真实读取到的分享目录树（JSON）。"
            + "请据此生成插件草稿，只能引用其中的 candidateId。"
            + "\r\n\r\n"
            + input.Text;

        return new AiCapabilityRequest(SystemPrompt, prompt);
    }
}

/// <summary>
/// Multi-turn tree generator (spec v3.1). Each turn the panel sends the real share tree (full on the
/// first turn, a compact id index afterwards), the current draft tree and the conversation so far;
/// the model must answer with one complete revised draft tree, held to it by
/// <see cref="PluginTreeDraftValidator"/>.
/// </summary>
public sealed class PluginTreeGeneratorCapability : IAiCapability
{
    public AiCapabilityKind Kind => AiCapabilityKind.PluginTreeGenerator;

    public string DisplayName => "插件树生成（Plugin Tree Generator）";

    public string Description =>
        "多轮对话式：读取真实分享树后，用户逐轮提出整理要求，模型每轮返回修订后的完整草稿树；"
        + "资源节点只能引用真实 candidateId，分组由模型命名，拒绝原因逐条可见。";

    public AiCapabilityRequest Build(AiCapabilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var prompt =
            "下面的用户消息里包含：真实分享树（或候选索引）、当前草稿树、历史对话和本轮指令。"
            + "只能依据真实树里存在的 candidateId 组织草稿树；每一轮都输出修订后的完整草稿树 JSON。"
            + "\r\n\r\n"
            + input.Text;

        return new AiCapabilityRequest(PluginTreeDraftValidator.SystemPrompt, prompt);
    }
}

/// <summary>Describes what a set of real resources actually is.</summary>
public sealed class ResourceAnalyzerCapability : IAiCapability
{
    public AiCapabilityKind Kind => AiCapabilityKind.ResourceAnalyzer;

    public string DisplayName => "资源分析（Resource Analyzer）";

    public string Description =>
        "对已经读取到的真实资源条目做分类：安装包还是压缩包、哪个平台与架构、把握有多大；"
        + "判断不出来时必须写 Unknown，而不是猜一个。";

    internal const string SystemPrompt =
        "你是 EriReborn Resource Analyzer。"
        + "输入是一份 EriReborn 从网盘平台真实读取到的资源清单（candidateId、名字、类型、大小、位置）。"
        + "你的任务是判断每一项属于什么：安装包、压缩包、目录还是无法判断，以及目标平台与架构。"
        + "只依据输入数据。不要编造大小、哈希、下载地址、网盘平台、位置或 Token。"
        + "输入里没有依据的信息一律写 Unknown，并降低 confidence。"
        + "candidateId 必须来自输入。"
        + "只输出 JSON：{\"summary\":\"总体说明\",\"items\":[{\"candidateId\":\"...\","
        + "\"kind\":\"installer|archive|folder|unknown\",\"platform\":\"Windows|Android|Linux|Unknown\","
        + "\"architecture\":\"x64|x86|arm64|Unknown\",\"confidence\":0.0,\"reason\":\"理由\"}]}。";

    public AiCapabilityRequest Build(AiCapabilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new AiCapabilityRequest(SystemPrompt, input.Text ?? string.Empty);
    }
}

/// <summary>
/// Comments on this machine's environment, and proposes reviewable changes.
///
/// <para>
/// The two prompts already existed in <see cref="AiEnvironmentAnalyst"/>; this adapter is what makes
/// them reachable through whichever provider the user configured instead of through one hard-wired
/// client.
/// </para>
/// </summary>
public sealed class EnvironmentAnalystCapability : IAiCapability
{
    /// <summary>The intent that asks for a plan rather than prose.</summary>
    public const string ProposeIntent = "propose";

    public AiCapabilityKind Kind => AiCapabilityKind.EnvironmentAnalyst;

    public string DisplayName => "环境分析（Environment Analyst）";

    public string Description =>
        "读懂这台电脑的真实环境事实，指出缺口与风险；也可以提出最多 10 项待确认的变更建议，"
        + "但它自己无法执行任何一项。";

    public AiCapabilityRequest Build(AiCapabilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var system = string.Equals(input.Intent, ProposeIntent, StringComparison.OrdinalIgnoreCase)
            ? AiEnvironmentAnalyst.ProposalSystemPrompt
            : AiEnvironmentAnalyst.SystemPrompt;

        return new AiCapabilityRequest(system, input.Text ?? string.Empty);
    }
}
