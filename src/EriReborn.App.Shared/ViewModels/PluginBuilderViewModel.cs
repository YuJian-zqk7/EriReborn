using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Validation;
using EriReborn.Engine.Ai;
using EriReborn.Engine.Plugins;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// Builds a resource plugin (.eriplugin.json) and writes it into the plugin folder.
///
/// <para>
/// This replaces a read-only list of "loaded extensions" and a raw asset-sheet dump:
/// those showed the user data and gave them nothing to do with it. Here the user fills
/// in plugin information, adds resources one at a time, and presses 生成插件 — the file
/// lands in the folder the plugin page already reads, so the two pages form one loop.
/// </para>
///
/// <para>
/// The generated JSON is written field-by-field with the exact names
/// <see cref="PluginReader"/> reads (<c>provider</c>, <c>shareUrl</c>, <c>directory</c>,
/// enum names as text). Serializing the record directly would emit <c>providerId</c> and
/// numeric enums, which the reader ignores — the file would look right and import as
/// something else.
/// </para>
/// </summary>
public sealed partial class PluginBuilderViewModel : ObservableObject
{
    private readonly AppHost _host;

    public PluginBuilderViewModel(AppHost host)
    {
        _host = host;
        PluginsDirectory = Path.Combine(host.Paths.UserDataDirectory, "plugins");

        foreach (var provider in host.CloudProviders.Providers)
        {
            Providers.Add(new PluginProviderOption(provider.Id, provider.DisplayName));
        }

        // The official taxonomy is a fixed, registered list (spec 15). A plugin may only
        // point its resources at one of these ids — the reader rejects an unregistered
        // one — so offering "create a category" would produce a file that is guaranteed
        // to fail on import.
        foreach (var category in CategoryTaxonomy.Official.Where(definition => definition.ParentId is null))
        {
            Categories.Add(new PluginCategoryOption(category.Id, category.DisplayName));
        }

        DraftCategory = Categories.FirstOrDefault();
        DraftProviderId = Providers.FirstOrDefault()?.Id ?? string.Empty;
        ValidateIdentity();
        RefreshInstalledPlugins();
    }

    /// <summary>Where a finished plugin is written, i.e. where the plugin page looks.</summary>
    public string PluginsDirectory { get; }

    /// <summary>The registered official first-level categories a resource may point at.</summary>
    public ObservableCollection<PluginCategoryOption> Categories { get; } = new();

    /// <summary>Cloud platforms a share source may name.</summary>
    public ObservableCollection<PluginProviderOption> Providers { get; } = new();

    /// <summary>
    /// Sources the reader can round-trip in full.
    ///
    /// <para>
    /// Winget is on the list because leaving it off was how the official plugin got mangled: its
    /// resources are winget sources, a picker that cannot show "Winget" falls back to its first entry
    /// ("CloudShare"), and the form then asked for a share link and a platform that the source never had
    /// — and refused to save it. A kind the editor can display is a kind it can leave alone.
    /// </para>
    /// </summary>
    public IReadOnlyList<SourceKind> SourceKinds { get; } = new[]
    {
        SourceKind.CloudShare,
        SourceKind.Winget,
        SourceKind.HttpUrl,
        SourceKind.Official,
        SourceKind.Local,
        SourceKind.Manual,
    };

    public IReadOnlyList<SoftwareTier> Tiers { get; } = new[]
    {
        SoftwareTier.Optional,
        SoftwareTier.Recommended,
        SoftwareTier.Core,
    };

    public IReadOnlyList<InstallationMode> Modes { get; } = new[]
    {
        InstallationMode.Install,
        InstallationMode.Portable,
        InstallationMode.Manual,
    };

    // ---------------------------------------------------------------- plugin info

    [ObservableProperty]
    private string _pluginId = "my_plugin";

    [ObservableProperty]
    private string _pluginIdVerdict = string.Empty;

    [ObservableProperty]
    private string _pluginName = "我的插件";

    [ObservableProperty]
    private string _pluginVersion = "1.0.0";

    [ObservableProperty]
    private string _pluginAuthor = string.Empty;

    [ObservableProperty]
    private string _pluginDescription = string.Empty;

    partial void OnPluginIdChanged(string value) => ValidateIdentity();

    /// <summary>The plugin id doubles as the file name, so it obeys the same rule.</summary>
    private void ValidateIdentity()
    {
        var result = DirectoryNameValidator.ValidateName(PluginId);
        PluginIdVerdict = result.IsValid
            ? $"将生成 {PluginId}{ResourcePlugin.FileExtension}"
            : string.Join(" / ", result.Issues.Select(issue => issue.ToString()));
    }

    // ---------------------------------------------------------------- resources

    /// <summary>
    /// schema v2 草稿主结构：group / file / folder 节点树。所有增删改都以树为准。
    /// </summary>
    public ObservableCollection<PluginNodeDraft> Nodes { get; } = new();

    /// <summary>
    /// 由 <see cref="Nodes"/> 前序派生的扁平资源行投影，包含且仅包含全部 file/folder 节点。
    /// 暂供尚未换成 TreeView 的旧列表绑定使用；元素实例与树中持有的是同一份草稿。
    /// </summary>
    public ObservableCollection<PluginResourceDraft> Resources { get; } = new();

    /// <summary>资源树里当前选中的节点；新资源/分组插入到它下面，未选中时进根层。</summary>
    [ObservableProperty]
    private PluginNodeDraft? _selectedNode;

    /// <summary>把树重新前序投影进扁平 <see cref="Resources"/>，保持实例不变、顺序与树一致。</summary>
    private void SyncFlatResources()
    {
        Resources.Clear();
        foreach (var node in Nodes)
        {
            foreach (var resourceNode in node.EnumerateSelfAndDescendants())
            {
                if (resourceNode.Resource is { } draft)
                {
                    Resources.Add(draft);
                }
            }
        }
    }

    [ObservableProperty]
    private string _draftId = string.Empty;

    [ObservableProperty]
    private string _draftName = string.Empty;

    [ObservableProperty]
    private PluginCategoryOption? _draftCategory;

    [ObservableProperty]
    private string _draftDirectoryName = string.Empty;

    [ObservableProperty]
    private string _draftVersion = string.Empty;

    [ObservableProperty]
    private string _draftFileName = string.Empty;

    [ObservableProperty]
    private string _draftShareUrl = string.Empty;

    [ObservableProperty]
    private string _draftUrl = string.Empty;

    [ObservableProperty]
    private string _draftProviderId = string.Empty;

    /// <summary>
    /// Where the item sits inside the share, for example "A/a1/b1/ResourceA.zip". A share link names
    /// a tree, so without this the resource can only be looked for by name in the share's root —
    /// which is where a share that has folders in it keeps nothing.
    /// </summary>
    [ObservableProperty]
    private string _draftLocatorPath = string.Empty;

    /// <summary>The platform's own item id, when the author has one: a rename does not change it.</summary>
    [ObservableProperty]
    private string _draftProviderItemId = string.Empty;

    [ObservableProperty]
    private ResourceLocatorKind _draftLocatorKind = ResourceLocatorKind.File;

    /// <summary>文件 / 文件夹 — the two things a locator can point at.</summary>
    public IReadOnlyList<ResourceLocatorKind> LocatorKinds { get; } = new[]
    {
        ResourceLocatorKind.File,
        ResourceLocatorKind.Folder,
    };

    /// <summary>Only a share source has a tree inside it to point at.</summary>
    public bool DraftNeedsLocator => DraftKind == SourceKind.CloudShare;

    [ObservableProperty]
    private SourceKind _draftKind = SourceKind.CloudShare;

    [ObservableProperty]
    private SoftwareTier _draftTier = SoftwareTier.Optional;

    [ObservableProperty]
    private InstallationMode _draftMode = InstallationMode.Install;

    /// <summary>A share source is the only one that needs a platform, so only then is the
    /// picker meaningful — the form says so instead of showing a dead control.</summary>
    public bool DraftNeedsProvider => DraftKind == SourceKind.CloudShare;

    public bool DraftNeedsShareUrl => DraftKind == SourceKind.CloudShare;

    /// <summary>Both an http source and an official-URL source are addressed by a URL.</summary>
    public bool DraftNeedsUrl => DraftKind is SourceKind.HttpUrl or SourceKind.Official;

    public bool DraftNeedsFileName => DraftKind != SourceKind.Winget;

    /// <summary>The winget package id, the whole address a Winget source needs.</summary>
    [ObservableProperty]
    private string _draftWingetId = string.Empty;

    public bool DraftNeedsWingetId => DraftKind == SourceKind.Winget;

    partial void OnDraftKindChanged(SourceKind value)
    {
        OnPropertyChanged(nameof(DraftNeedsProvider));
        OnPropertyChanged(nameof(DraftNeedsShareUrl));
        OnPropertyChanged(nameof(DraftNeedsUrl));
        OnPropertyChanged(nameof(DraftNeedsFileName));
        OnPropertyChanged(nameof(DraftNeedsLocator));
        OnPropertyChanged(nameof(DraftNeedsWingetId));
    }

    /// <summary>What the last "识别网盘" attempt concluded.</summary>
    [ObservableProperty]
    private string _draftDetectionStatus = string.Empty;

    /// <summary>
    /// Reads the platform out of the pasted share link, so the form fills itself in instead of
    /// asking the user which of five peer platforms this particular link came from. A link that
    /// names none of them says so — picking one anyway would put a wrong platform in the file.
    /// </summary>
    [RelayCommand]
    private void DetectProviderFromLink()
    {
        if (string.IsNullOrWhiteSpace(DraftShareUrl))
        {
            DraftDetectionStatus = "先粘一条分享链接，再点「识别网盘」。";
            return;
        }

        var providerId = CloudProviderIds.FromShareUrl(DraftShareUrl);
        if (providerId is null)
        {
            DraftDetectionStatus = "这条链接认不出是哪个网盘。五个平台里是哪个就自己选哪个，不要猜。";
            return;
        }

        var option = Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.Ordinal));

        if (option is null)
        {
            DraftDetectionStatus = $"链接属于「{providerId}」，但该平台的扩展当前没有启用，所以这里还选不了它。";
            return;
        }

        DraftKind = SourceKind.CloudShare;
        DraftProviderId = option.Id;
        DraftDetectionStatus = $"已识别为「{option.DisplayName}」，平台已经填好。";
    }

    // ------------------------------------------------------- AI 多轮对话建插件（spec v3.1）

    /// <summary>
    /// AI 对话读取的分享链接。和手工表单的链接刻意分开：给模型读整棵分享树与给单条资源指向链接
    /// 是两个动作，共用一个框会互相覆盖。链接一旦更换就重建会话并清空草稿树。
    /// </summary>
    [ObservableProperty]
    private string _aiShareUrl = string.Empty;

    [ObservableProperty]
    private string _aiStatus =
        "粘一条网盘分享链接，直接用自然语言告诉 AI 要怎么整理（例如「按 Windows 工具和皮肤分成两组」），"
        + "回车发送。EriReborn 会先通过对应网盘读取真实目录树，模型每轮只能引用其中真实存在的条目；"
        + "它看不到你的账号，也不能编造文件。";

    [ObservableProperty]
    private bool _isAiRunning;

    /// <summary>输入框里的本轮自然语言指令。</summary>
    [ObservableProperty]
    private string _aiInput = string.Empty;

    /// <summary>对话消息流；被拒绝/被忽略的内容写进助手消息，始终可见。</summary>
    public ObservableCollection<AiChatMessage> AiMessages { get; } = new();

    /// <summary>
    /// 最近一次校验通过的草稿树，实时渲染在对话旁边。每一轮被采纳后整体替换，绝不增量拼接。
    /// </summary>
    public ObservableCollection<AiDraftNodeVm> AiDraftTreeRoots { get; } = new();

    /// <summary>模型建议的插件名，仅展示，绝不自动写入。</summary>
    [ObservableProperty]
    private string _aiDraftName = string.Empty;

    public bool HasAiDraftTree => AiDraftTreeRoots.Count > 0;

    /// <summary>首轮前读取的真实分享树；模型每轮可见的 candidateId 全部来自它。</summary>
    private PluginCandidateTree? _aiTree;

    /// <summary>当前会话对应的分享 URL；与输入框不一致即视为新会话。</summary>
    private string? _aiTreeUrl;

    /// <summary>原始对话：用户指令原文 + 被采纳轮次的模型原始 JSON（组装下一轮用）。</summary>
    private readonly List<(AiChatAuthor Author, string Text)> _aiTranscript = new();

    /// <summary>上一轮用户指令，供「重试上一轮」使用。</summary>
    private string? _aiLastInstruction;

    /// <summary>链接变更即作废整个会话：下一条消息按首轮重新读树。</summary>
    partial void OnAiShareUrlChanged(string value)
    {
        if (_aiTreeUrl is { } loaded
            && !string.Equals(loaded, value?.Trim(), StringComparison.Ordinal))
        {
            ResetAiConversation(resetTree: true);
            AiStatus = "分享链接已更换，会话与草稿树已清空；发送下一条消息时会重新读取新链接的真实目录树。";
        }
    }

    private bool CanSendAi => !IsAiRunning;

    private bool CanRetryAi => !IsAiRunning && _aiLastInstruction is { Length: > 0 };

    /// <summary>
    /// 发送一轮指令（参数为空时取输入框）。首轮会先读真实分享树；每轮一次 completion，
    /// 回复经 <see cref="PluginTreeDraftValidator"/> 校验后整体替换草稿树。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendAi))]
    private async Task SendAiMessageAsync(string? text)
    {
        var instruction = (text ?? AiInput)?.Trim();
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return;
        }

        await RunAiTurnAsync(instruction).ConfigureAwait(true);
    }

    /// <summary>
    /// 重试上一轮：撤掉消息流里最后一轮（用户指令及其后的助手/系统消息）与原始记录，
    /// 再用同一条指令按当前草稿树重新请求一次。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRetryAi))]
    private async Task RetryAiTurnAsync()
    {
        if (_aiLastInstruction is not { Length: > 0 } instruction)
        {
            return;
        }

        RollbackLastTurn();
        await RunAiTurnAsync(instruction).ConfigureAwait(true);
    }

    /// <summary>清空对话与草稿树（保留已读取的分享树，同链接下一轮仍按首轮生成）。</summary>
    [RelayCommand]
    private void ClearAiConversation()
    {
        AiMessages.Clear();
        AiDraftTreeRoots.Clear();
        AiDraftName = string.Empty;
        _aiTranscript.Clear();
        _aiLastInstruction = null;
        OnPropertyChanged(nameof(HasAiDraftTree));
        SendAiMessageCommand.NotifyCanExecuteChanged();
        RetryAiTurnCommand.NotifyCanExecuteChanged();
        AiStatus = "对话与草稿树已清空；分享树仍保留，下一条消息将按首轮重新生成完整草稿。";
    }

    /// <summary>从草稿树里移除一个节点（只影响 AI 草稿，不影响插件树）。</summary>
    [RelayCommand]
    private void RemoveAiDraftNode(AiDraftNodeVm? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.Parent is not null)
        {
            row.Parent.Children.Remove(row);
        }
        else
        {
            AiDraftTreeRoots.Remove(row);
        }

        OnPropertyChanged(nameof(HasAiDraftTree));
        AiStatus = "已从草稿树移除该节点；不影响插件树。继续对话时模型会以当前（移除后的）草稿树为准。";
    }

    /// <summary>
    /// 「改为分组」：把一个资源节点变成纯组织分组（保留其子树，丢弃资源绑定）。
    /// 当模型把一个不该下载的条目选成资源时使用；下一轮提示以调整后的草稿树为准。
    /// </summary>
    [RelayCommand]
    private void ConvertAiDraftNodeToGroup(AiDraftNodeVm? row)
    {
        if (row is null || row.Node.IsGroup)
        {
            return;
        }

        var collection = row.Parent?.Children ?? AiDraftTreeRoots;
        var index = collection.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        var children = row.Children.ToList();
        var replacement = new AiDraftNodeVm(new DraftGroupNode(row.Name, row.Node.Children), children);
        collection[index] = replacement;
        AiStatus = $"已把「{row.Name}」改为分组：它不再指向可下载资源，其子树保留。继续对话时模型会以调整后的草稿树为准。";
    }

    /// <summary>
    /// 「取消分组」：把分组的直接子节点提升到分组所在位置，然后删除这个空分组。
    /// 只动结构，不碰子节点指向的真实资源。
    /// </summary>
    [RelayCommand]
    private void UngroupAiDraftNode(AiDraftNodeVm? row)
    {
        if (row is null || !row.Node.IsGroup)
        {
            return;
        }

        var collection = row.Parent?.Children ?? AiDraftTreeRoots;
        var index = collection.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        var children = row.Children.ToList();
        collection.RemoveAt(index);

        for (var i = 0; i < children.Count; i++)
        {
            children[i].SetParent(row.Parent);
            collection.Insert(index + i, children[i]);
        }

        AiStatus = $"已取消分组「{row.Name}」：其 {children.Count} 个子节点已上移，资源绑定不变。继续对话时模型会以调整后的草稿树为准。";
    }

    private void ResetAiConversation(bool resetTree)
    {
        AiMessages.Clear();
        AiDraftTreeRoots.Clear();
        AiDraftName = string.Empty;
        AiInput = string.Empty;
        _aiTranscript.Clear();
        _aiLastInstruction = null;
        if (resetTree)
        {
            _aiTree = null;
            _aiTreeUrl = null;
        }

        OnPropertyChanged(nameof(HasAiDraftTree));
        SendAiMessageCommand.NotifyCanExecuteChanged();
        RetryAiTurnCommand.NotifyCanExecuteChanged();
    }

    private void RollbackLastTurn()
    {
        // 原始记录：先剥掉尾部已采纳的助手 JSON，再剥掉最后一条用户指令。
        while (_aiTranscript.Count > 0 && _aiTranscript[^1].Author == AiChatAuthor.Assistant)
        {
            _aiTranscript.RemoveAt(_aiTranscript.Count - 1);
        }

        if (_aiTranscript.Count > 0 && _aiTranscript[^1].Author == AiChatAuthor.User)
        {
            _aiTranscript.RemoveAt(_aiTranscript.Count - 1);
        }

        // 可见消息：从最后一条用户消息起整段删除（含其后的助手/系统消息）。
        var lastUser = -1;
        for (var i = AiMessages.Count - 1; i >= 0; i--)
        {
            if (AiMessages[i].IsUser)
            {
                lastUser = i;
                break;
            }
        }

        if (lastUser >= 0)
        {
            while (AiMessages.Count > lastUser)
            {
                AiMessages.RemoveAt(AiMessages.Count - 1);
            }
        }
    }

    /// <summary>一轮对话的完整流程：确保真实树 → 组装单次 completion → 校验 → 整体替换草稿树。</summary>
    private async Task RunAiTurnAsync(string instruction)
    {
        if (IsAiRunning)
        {
            return;
        }

        IsAiRunning = true;
        SendAiMessageCommand.NotifyCanExecuteChanged();
        RetryAiTurnCommand.NotifyCanExecuteChanged();

        try
        {
            if (await EnsureAiTreeAsync().ConfigureAwait(true) is not { } tree)
            {
                return;
            }

            AiMessages.Add(new AiChatMessage(AiChatAuthor.User, instruction));
            _aiTranscript.Add((AiChatAuthor.User, instruction));
            _aiLastInstruction = instruction;
            AiInput = string.Empty;
            AiStatus = "模型正在修订草稿树…";

            var preferences = _host.UserConfig.Current;
            var key = _host.Platform.Credentials.IsAvailable
                ? await _host.Platform.Credentials.GetAsync(AiSettingsKeys.ApiKey).ConfigureAwait(true)
                : null;
            var endpoint = new AiEndpointSettings(
                preferences.AiProviderId,
                preferences.AiBaseUrl,
                preferences.AiModel,
                key);

            var prompt = BuildAiTurnPrompt(tree, instruction);

            var result = await AiService.Default
                .RunAsync(
                    AiCapabilityKind.PluginTreeGenerator,
                    endpoint,
                    new AiCapabilityInput(prompt),
                    _host.Platform.Network.Client,
                    CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                AiMessages.Add(new AiChatMessage(
                    AiChatAuthor.Assistant,
                    "这一轮没拿到模型的有效回复：" + result.Message + "草稿树保持不变，可以改写要求后再发，或点「重试上一轮」。"));
                AiStatus = "模型调用失败，草稿树未改动。";
                return;
            }

            var validation = PluginTreeDraftValidator.Parse(result.Text, tree);

            // 截断自动重试：模型输出被 max_tokens 切断时，自动用更精简的指令再试，
            // 最多重试 2 次（逐级加约束），而不是让用户手动分批。
            var retryCount = 0;
            while (validation.Draft is null && IsTruncated(validation) && retryCount < 2)
            {
                retryCount++;
                // 截断重试：精简 reason 和 reason 数量来省 JSON 体积，
                // 绝不能去掉 folder 节点的 children——那会让树变扁平，文件夹打不开。
                var constrained = retryCount == 1
                    ? instruction + "\n\n注意：上一轮回复被输出长度上限截断了。这次请精简输出："
                        + "reason 每个只写 2 个字以内；folder 节点的 children 仍然要写完整，不能省略。"
                    // 第二次还截断：进一步去掉 reason、限制节点数，但仍然保留 folder children
                    : instruction + "\n\n注意：上一轮回复又被截断。这次极其精简："
                        + "去掉所有 reason 字段（或只写\"ok\"），整个草稿树不超过 30 个节点。"
                        + "folder 节点的 children 必须完整写出，不能省略子节点。";

                AiMessages.Add(new AiChatMessage(
                    AiChatAuthor.System,
                    "模型回复被输出长度上限截断了，正在自动精简重试"
                    + (retryCount == 1 ? "（精简理由，保留子节点）" : "（无理由，限 30 节点，保留子节点）")
                    + "…"));

                var retryPrompt = BuildAiTurnPrompt(tree, constrained);
                var retryResult = await AiService.Default
                    .RunAsync(
                        AiCapabilityKind.PluginTreeGenerator,
                        endpoint,
                        new AiCapabilityInput(retryPrompt),
                        _host.Platform.Network.Client,
                        CancellationToken.None)
                    .ConfigureAwait(true);

                if (!retryResult.Success)
                {
                    break;
                }

                validation = PluginTreeDraftValidator.Parse(retryResult.Text, tree);
                if (validation.Draft is not null)
                {
                    // 重试成功：用重试结果替换，后续校验通过分支会处理。
                    result = retryResult;
                    break;
                }
            }

            if (validation.Draft is not { } draft)
            {
                var reason = DescribeTreeValidation(validation);
                AiMessages.Add(new AiChatMessage(
                    AiChatAuthor.Assistant,
                    "这一轮的回复没有通过校验，整份草稿未被采纳，草稿树保持不变。"
                    + (reason.Length == 0 ? string.Empty : " " + reason)
                    + "可以改写要求后再发，或点「重试上一轮」。"));
                AiStatus = "模型回复未通过校验，草稿树未改动。";
                return;
            }

            // 校验通过：整体替换，绝不与上一轮草稿增量拼接。
            AiDraftTreeRoots.Clear();
            foreach (var node in draft.Nodes)
            {
                AiDraftTreeRoots.Add(new AiDraftNodeVm(node));
            }

            AiDraftName = draft.Name;
            OnPropertyChanged(nameof(HasAiDraftTree));

            // 只有被采纳的回复才进入下一轮的历史，避免坏 JSON 锚定后续轮次。
            _aiTranscript.Add((AiChatAuthor.Assistant, result.Text ?? string.Empty));

            var all = draft.EnumerateAll().ToList();
            var groups = all.Count(node => node.IsGroup);
            var summary = $"草稿树已更新：共 {all.Count} 个节点（{groups} 个分组、{all.Count - groups} 个文件/文件夹）。";
            var detail = DescribeTreeValidation(validation);
            AiMessages.Add(new AiChatMessage(
                AiChatAuthor.Assistant,
                summary + (detail.Length == 0 ? string.Empty : " " + detail)
                + " 可以在右侧勾选/移除节点后继续对话，或点「应用勾选到插件树」。"));
            AiStatus = $"草稿树已更新：{all.Count} 个节点。继续对话可逐轮修订；满意后点「应用勾选到插件树」，再自行点「生成插件」。";
        }
        catch (Exception ex)
        {
            AiMessages.Add(new AiChatMessage(AiChatAuthor.System, $"本轮处理出错：{ex.GetType().Name}：{ex.Message}"));
            AiStatus = "本轮处理出错：" + ex.Message;
        }
        finally
        {
            IsAiRunning = false;
            SendAiMessageCommand.NotifyCanExecuteChanged();
            RetryAiTurnCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 首轮前（或换链接后）通过对应网盘读取真实分享树。失败时在对话里留下系统消息并返回 null。
    /// 顺序是硬性要求：先读平台，模型永远只看到平台返回的内容。
    /// </summary>
    private async Task<PluginCandidateTree?> EnsureAiTreeAsync()
    {
        var url = AiShareUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            AiStatus = "先粘一条网盘分享链接，再发送你的整理要求。";
            return null;
        }

        if (_aiTree is { } existing && string.Equals(_aiTreeUrl, url, StringComparison.Ordinal))
        {
            return existing;
        }

        var providerId = CloudProviderIds.FromShareUrl(url);
        if (providerId is null)
        {
            NoteAiProblem("这条链接认不出是哪个网盘。请使用受支持平台（123 / 百度 / 夸克 / 蓝奏 / 迅雷）的分享链接。");
            return null;
        }

        var provider = _host.CloudProviders.Resolve(new SoftwareSource
        {
            Kind = SourceKind.CloudShare,
            ProviderId = providerId,
            ShareUrl = url,
        });

        if (provider is null)
        {
            NoteAiProblem($"没有接入这条链接写明的平台「{providerId}」。");
            return null;
        }

        var preferences = _host.UserConfig.Current;
        if (string.IsNullOrWhiteSpace(preferences.AiProviderId)
            || string.IsNullOrWhiteSpace(preferences.AiBaseUrl))
        {
            NoteAiProblem("还没有配置 AI 服务。先到「AI 接口」页选好服务商、填好 Base URL 与模型并保存，再回到这里。");
            return null;
        }

        try
        {
            AiStatus = $"{provider.DisplayName}：正在读取真实分享目录树…";

            var credential = provider is CloudProviderBase providerBase
                ? await providerBase.LoadCredentialAsync().ConfigureAwait(true)
                : null;

            var collection = await PluginCandidateCollector
                .CollectAsync(provider, url, credential, cancellationToken: CancellationToken.None)
                .ConfigureAwait(true);

            if (!collection.Success || collection.Tree is null)
            {
                NoteAiProblem("读取分享失败：" + (collection.Error ?? "未知原因") + "（没有向模型发送任何内容。）");
                return null;
            }

            var tree = collection.Tree;
            if (tree.Items.Count == 0)
            {
                NoteAiProblem("这个分享里没有可列为资源的条目（目录为空，或平台没有返回内容）。");
                return null;
            }

            _aiTree = tree;
            _aiTreeUrl = url;

            var note = $"已通过{provider.DisplayName}读取真实分享树：{tree.Items.Count} 个真实条目。"
                + (tree.IsTruncated ? $"（目录树较大，省略了 {tree.OmittedCount} 项，模型同样看不到这些项）" : string.Empty)
                + (tree.Unreadable is { Count: > 0 } unreadable
                    ? $"另有 {unreadable.Count} 个文件夹无法进入，其内容不在候选内。"
                    : string.Empty);
            AiMessages.Add(new AiChatMessage(AiChatAuthor.System, note));
            AiStatus = $"已读取 {tree.Items.Count} 项真实条目，等待你的整理要求。";
            return tree;
        }
        catch (Exception ex)
        {
            NoteAiProblem($"读取分享时出错：{ex.GetType().Name}：{ex.Message}");
            return null;
        }
    }

    private void NoteAiProblem(string text)
    {
        AiMessages.Add(new AiChatMessage(AiChatAuthor.System, text));
        AiStatus = text;
    }

    /// <summary>
    /// 组装一轮的单次 completion 用户消息：真实树全文 + 当前草稿树 JSON + 历史对话 + 本轮指令。
    /// </summary>
    private string BuildAiTurnPrompt(PluginCandidateTree tree, string instruction)
    {
        var builder = new System.Text.StringBuilder();

        builder.AppendLine("【真实分享树 JSON】");
        builder.AppendLine("下面每个 file/folder 候选都带真实 candidateId；草稿里的资源节点只能引用这里存在的 id，不得编造。");
        builder.AppendLine(tree.ToPromptJson());
        builder.AppendLine();

        builder.AppendLine("【当前草稿树 JSON】");
        builder.AppendLine("这是本轮要修订的对象；保留你认同的部分，输出修订后的完整草稿树（不是补丁）。");
        builder.AppendLine(AiDraftTreeRoots.Count == 0
            ? "（还没有草稿树，请按本轮指令从头建立完整草稿树。）"
            : DraftFromRows().ToContractJson());
        builder.AppendLine();

        // 最后一条就是本轮刚入列的用户指令，历史只取它之前的部分。
        if (_aiTranscript.Count > 1)
        {
            builder.AppendLine("【历史对话】");
            foreach (var (author, text) in _aiTranscript.Take(_aiTranscript.Count - 1))
            {
                builder.AppendLine(author == AiChatAuthor.User ? "用户：" + text : "助手（草稿树 JSON）：" + text);
            }

            builder.AppendLine();
        }

        builder.AppendLine("【本轮指令】");
        builder.Append(instruction);

        return builder.ToString();
    }

    /// <summary>按当前（可能被用户手动移除过节点的）草稿树行重建不可变草稿树。</summary>
    private PluginDraftTree DraftFromRows()
        => new(AiDraftName, AiDraftTreeRoots.Select(BuildDraftNodeFromRow).ToList());

    private static PluginDraftNode BuildDraftNodeFromRow(AiDraftNodeVm row)
    {
        var children = row.Children.Select(BuildDraftNodeFromRow).ToList();

        if (row.Node is DraftResourceNode resource)
        {
            return new DraftResourceNode(
                resource.Candidate,
                resource.Platform,
                resource.Architecture,
                resource.Confidence,
                resource.Reason,
                children);
        }

        return new DraftGroupNode(row.Name, children);
    }

    /// <summary>判断校验失败是否由输出截断引起（用于自动重试）。</summary>
    private static bool IsTruncated(PluginTreeDraftValidation validation)
        => validation.Draft is null
            && validation.Rejected.Count == 1
            && validation.Rejected[0].Reason.Contains("截断", StringComparison.Ordinal);

    private static string DescribeTreeValidation(PluginTreeDraftValidation validation)
    {
        var parts = new List<string>();

        if (validation.Rejected.Count > 0)
        {
            parts.Add("拒绝 " + validation.Rejected.Count + " 个节点："
                + string.Join("；", validation.Rejected.Take(6).Select(issue => $"{issue.Subject}（{issue.Reason}）")));
        }

        if (validation.Issues.Count > 0)
        {
            parts.Add("忽略 " + validation.Issues.Count + " 处不可信字段："
                + string.Join("；", validation.Issues.Take(4).Select(issue => issue.Reason)));
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// 把草稿树里勾选的子树合并进插件树：分组按名字就地复用、递归合并；
    /// 资源按「同分享 + 同平台 itemId」幂等去重，重复应用不会插入第二份。
    /// 目录名仍按真实文件名派生，无 ASCII 时给临时名；模型给的版本一律不采用。
    /// </summary>
    [RelayCommand]
    private void ApplyAiDraft()
    {
        if (AiDraftTreeRoots.Count == 0)
        {
            AiStatus = "还没有可应用的草稿树：先发送整理要求，拿到草稿后再应用。";
            return;
        }

        var category = DraftCategory?.Id ?? Categories.FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(category))
        {
            AiStatus = "没有可用的分区，无法把草稿加入插件树。";
            return;
        }

        var added = 0;
        var skipped = 0;
        var renamed = 0;

        MergeDraftRowsInto(Nodes, AiDraftTreeRoots, category, ref added, ref skipped, ref renamed);

        if (added > 0)
        {
            SyncFlatResources();
        }

        AiStatus = $"已把 {added} 个勾选节点合并进插件树。"
            + (skipped > 0 ? $" {skipped} 个重复或为空的节点已跳过（可重复应用，不会重复插入）。" : string.Empty)
            + (renamed > 0
                ? $"其中 {renamed} 个文件名不含英文字符，用了临时目录名，请在资源表单里改成官方目录名。"
                : string.Empty)
            + "版本没有填：模型给出的版本不会被采用，请确认真实版本后自己填。对话与草稿仍保留，可继续修订；检查无误再点「生成插件」。";
    }

    /// <summary>递归把勾选的草稿行合并进目标节点集合；同层分组按名字复用，资源按候选 id 去重。</summary>
    private void MergeDraftRowsInto(
        ObservableCollection<PluginNodeDraft> target,
        IEnumerable<AiDraftNodeVm> rows,
        string category,
        ref int added,
        ref int skipped,
        ref int renamed)
    {
        foreach (var row in rows)
        {
            if (!row.IsSelected)
            {
                continue;
            }

            if (row.Node is DraftGroupNode groupNode)
            {
                var existing = target.FirstOrDefault(node =>
                    node.IsGroup && string.Equals(node.Name, groupNode.Name, StringComparison.Ordinal));

                var created = false;
                if (existing is null)
                {
                    existing = new PluginNodeDraft(
                        UniqueNodeId("ai_group"),
                        groupNode.Name,
                        PluginNodeKind.Group,
                        null);
                    created = true;
                }

                var before = added;
                MergeDraftRowsInto(existing.Children, row.Children, category, ref added, ref skipped, ref renamed);

                if (created)
                {
                    // 新建分组里没有任何新增资源（全未勾选或全重复）就不插空分组。
                    if (added == before)
                    {
                        skipped++;
                    }
                    else
                    {
                        target.Add(existing);
                    }
                }

                continue;
            }

            var resourceNode = (DraftResourceNode)row.Node;

            // 幂等：同一分享里同一平台 itemId 已存在则不重复插入，但其勾选的子节点继续合并进去。
            if (FindExistingResourceByCandidate(resourceNode.CandidateId) is { } existingResource)
            {
                skipped++;
                MergeDraftRowsInto(existingResource.Children, row.Children, category, ref added, ref skipped, ref renamed);
                continue;
            }

            var candidate = resourceNode.Candidate;
            var derived = DeriveDirectory(candidate.Name, Resources.Count + added + 1);
            if (derived.WasDerivedFromNothing)
            {
                renamed++;
            }

            var tree = _aiTree ?? throw new InvalidOperationException("应用草稿时真实分享树已丢失。");
            var draft = new PluginResourceDraft(
                UniqueResourceId("ai_" + derived.Directory),
                candidate.Name,
                category,
                derived.Directory,
                string.Empty,
                SoftwareTier.Optional,
                InstallationMode.Install,
                new SoftwareSource
                {
                    Kind = SourceKind.CloudShare,
                    ProviderId = tree.ProviderId,
                    ShareUrl = tree.ShareUrl,
                    FileName = candidate.Name,

                    // locator 全部取自真实候选（平台自己的 id + 可读路径），绝不取模型的话。
                    Locator = new ResourceLocator
                    {
                        Kind = resourceNode.IsFolder ? ResourceLocatorKind.Folder : ResourceLocatorKind.File,
                        Name = candidate.Name,
                        Path = candidate.Path,
                        ProviderItemId = candidate.CandidateId,
                    },
                    NeedsLocatorResolution = false,
                });

            var node = new PluginNodeDraft(
                draft.Id,
                draft.Name,
                resourceNode.IsFolder ? PluginNodeKind.Folder : PluginNodeKind.File,
                draft);

            MergeDraftRowsInto(node.Children, row.Children, category, ref added, ref skipped, ref renamed);

            target.Add(node);
            added++;
        }
    }

    /// <summary>在整棵插件树里查找同一分享、同一平台 itemId 的资源节点（幂等去重用）。</summary>
    private PluginNodeDraft? FindExistingResourceByCandidate(string candidateId)
    {
        var url = _aiTreeUrl;
        return Nodes
            .SelectMany(root => root.EnumerateSelfAndDescendants())
            .FirstOrDefault(node =>
                !node.IsGroup
                && node.Resource is { } resource
                && resource.Source is { Kind: SourceKind.CloudShare } source
                && string.Equals(source.ShareUrl, url, StringComparison.Ordinal)
                && string.Equals(source.Locator?.ProviderItemId, candidateId, StringComparison.Ordinal));
    }

    /// <summary>
    /// A legal official directory name from a real file name, plus whether it had to be invented
    /// because the name carried no usable ASCII at all.
    /// </summary>
    private static (string Directory, bool WasDerivedFromNothing) DeriveDirectory(string fileName, int index)
    {
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);

        var builder = new System.Text.StringBuilder();
        foreach (var ch in withoutExtension)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (ch is '_' or '-' or ' ')
            {
                builder.Append('_');
            }
        }

        var slug = builder.ToString().Trim('_');

        // Collapse runs so "Tool  v2" does not become "Tool__v2".
        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        if (slug.Length > 64)
        {
            slug = slug[..64].TrimEnd('_');
        }

        return slug.Length > 0 ? (slug, false) : ($"resource_{index}", true);
    }

    /// <summary>An id no other row in the list already uses.</summary>
    private string UniqueResourceId(string baseId)
    {
        var candidate = baseId;
        var counter = 2;

        while (Resources.Any(resource => string.Equals(resource.Id, candidate, StringComparison.OrdinalIgnoreCase))
               || AllNodeIds().Any(nodeId => string.Equals(nodeId, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = baseId + "_" + counter;
            counter++;
        }

        return candidate;
    }

    // ------------------------------------------------------- 从分享树里点选资源

    /// <summary>
    /// 打开的分享的根级条目。树是懒加载的：只有作者展开某个文件夹时才按平台真实 itemId
    /// 读它的直接子级，绝不预加载整棵分享树，也绝不枚举「选中的文件夹」。
    /// </summary>
    public ObservableCollection<ShareTreeRow> ShareTreeRoots { get; } = new();

    [ObservableProperty]
    private string _shareBrowseStatus = "粘贴分享链接后点「读取分享」，就能在真实目录树里点选文件或整个文件夹，不用手写路径。";

    private ICloudProvider? _shareProvider;
    private CloudCredential? _shareCredential;
    private string? _shareUrl;

    /// <summary>Opens the share the draft names and lists its root.</summary>
    [RelayCommand]
    private async Task ReadShareAsync()
    {
        var url = DraftShareUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShareBrowseStatus = "先把分享链接粘到上面，再点「读取分享」。";
            return;
        }

        // The platform comes from the form (or from 识别网盘), never from the domain here.
        var provider = _host.CloudProviders.Resolve(new SoftwareSource
        {
            Kind = SourceKind.CloudShare,
            ProviderId = DraftProviderId,
            ShareUrl = url,
        });

        if (provider is null)
        {
            ShareTreeRoots.Clear();
            ShareBrowseStatus = $"没有接入这条来源写明的平台「{DraftProviderId}」。";
            return;
        }

        var credential = provider is CloudProviderBase providerBase
            ? await providerBase.LoadCredentialAsync().ConfigureAwait(true)
            : null;

        _shareProvider = provider;
        _shareCredential = credential;
        _shareUrl = url;
        ShareTreeRoots.Clear();

        ShareBrowseStatus = provider.DisplayName + "：正在读取分享根目录…";
        var listed = await provider.ListShareFolderAsync(url, null, credential).ConfigureAwait(true);

        if (!listed.Success)
        {
            ShareBrowseStatus = provider.DisplayName + "：" + (listed.Message ?? "读取失败。");
            return;
        }

        foreach (var file in listed.Files)
        {
            ShareTreeRoots.Add(new ShareTreeRow(this, file, parent: null, supportsFolderPackage: provider.SupportsFolderPackage));
        }

        var packageNote = provider.SupportsFolderPackage
            ? "该平台支持整个文件夹打包下载，可直接「选择此文件夹」。"
            : "该平台暂不支持整个文件夹打包下载：选文件夹会照常记录，但下载端会提示不支持，请展开选择里面的文件。";

        ShareBrowseStatus = $"分享根目录：共 {listed.Files.Count} 项。文件夹可展开逐层选，也可整夹选择。{packageNote}";
    }

    /// <summary>
    /// 懒加载一个文件夹行的直接子级。每个文件夹只读一次：收起/再展开不产生第二次请求，
    /// 失败后允许重试（由行上的 LoadState 决定按钮形态）。
    /// </summary>
    internal async Task EnsureShareChildrenAsync(ShareTreeRow row)
    {
        if (_shareProvider is null || _shareUrl is null || !row.IsFolder || row.LoadState == ShareLoadState.Loading)
        {
            return;
        }

        row.LoadState = ShareLoadState.Loading;
        ShareBrowseStatus = $"{_shareProvider.DisplayName}：正在读取 {row.Path} …";

        var listed = await _shareProvider
            .ListShareFolderAsync(_shareUrl, row.File.Id, _shareCredential)
            .ConfigureAwait(true);

        if (!listed.Success)
        {
            row.LoadState = ShareLoadState.Failed;
            row.LoadError = listed.Message ?? "读取失败。";
            ShareBrowseStatus = $"{row.Path}：{row.LoadError}";
            return;
        }

        row.Children.Clear();
        foreach (var file in listed.Files)
        {
            row.Children.Add(new ShareTreeRow(
                this,
                file,
                parent: row,
                supportsFolderPackage: _shareProvider.SupportsFolderPackage));
        }

        row.LoadState = ShareLoadState.Loaded;
        ShareBrowseStatus = $"{row.Path}：共 {listed.Files.Count} 项（按需读取，未预加载更深层级）。";
    }

    /// <summary>
    /// 把分享树里点中的条目直接建为一个资源节点：平台真实 itemId 与祖先名拼成的路径组成
    /// 独立 locator。不枚举文件夹内容；落点是制作器树当前选中的分组/文件夹，未选中则进根层。
    /// </summary>
    internal void SelectShareItem(ShareTreeRow? row)
    {
        if (row is null)
        {
            return;
        }

        var file = row.File;
        var category = DraftCategory?.Id ?? Categories.FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(category))
        {
            ShareBrowseStatus = "没有可用的分区，无法把选中项加入资源树。";
            return;
        }

        // 稳定 id 去重：同一个真实条目（跨层级/跨分享）不允许进树两次。
        if (TryFindNodeByProviderItemId(file.Id) is { } existing)
        {
            ShareBrowseStatus = $"「{file.Name}」已经在资源树里了（节点 {existing.NodeId}），没有重复添加。";
            return;
        }

        var derived = DeriveDirectory(file.Name, Resources.Count + 1);
        var draft = new PluginResourceDraft(
            UniqueResourceId("share_" + derived.Directory),
            file.Name,
            category,
            derived.Directory,
            string.Empty,
            SoftwareTier.Optional,
            InstallationMode.Install,
            new SoftwareSource
            {
                Kind = SourceKind.CloudShare,
                ProviderId = _shareProvider?.Id ?? DraftProviderId,
                ShareUrl = _shareUrl ?? DraftShareUrl?.Trim(),
                FileName = file.Name,

                // Locator 全部来自真实遍历：平台 itemId 为主、祖先名路径为可读的另一半；
                // 文件夹就是 folder kind —— 不依赖展开它读了什么。
                Locator = new ResourceLocator
                {
                    Kind = file.IsFolder ? ResourceLocatorKind.Folder : ResourceLocatorKind.File,
                    Name = file.Name,
                    Path = row.Path,
                    ProviderItemId = file.Id,
                },
                NeedsLocatorResolution = false,
            });

        var node = new PluginNodeDraft(
            draft.Id,
            draft.Name,
            file.IsFolder ? PluginNodeKind.Folder : PluginNodeKind.File,
            draft);

        try
        {
            InsertNode(node, SelectedNode);
        }
        catch (InvalidOperationException ex)
        {
            ShareBrowseStatus = ex.Message;
            return;
        }

        SyncFlatResources();

        var where = SelectedNode is null ? "资源树根层" : $"「{SelectedNode.Name}」之下";
        var kindText = file.IsFolder ? "文件夹" : "文件";
        var supportNote = file.IsFolder && _shareProvider is { SupportsFolderPackage: false }
            ? " 注意：该平台暂不支持整夹打包下载。"
            : string.Empty;
        ShareBrowseStatus = $"已把{kindText}「{row.Path}」作为独立资源加入{where}（共 {Resources.Count} 条）。{supportNote}";
        Status = ShareBrowseStatus;
    }

    /// <summary>按平台真实 itemId 全树查找已存在的资源节点（去重用）。</summary>
    private PluginNodeDraft? TryFindNodeByProviderItemId(string providerItemId)
        => Nodes
            .SelectMany(root => root.EnumerateSelfAndDescendants())
            .FirstOrDefault(node =>
                node.Resource is { } draft
                && draft.AllSources.Any(source =>
                    string.Equals(source.Locator?.ProviderItemId, providerItemId, StringComparison.Ordinal)));

    [ObservableProperty]
    private string _newCategory = string.Empty;

    /// <summary>
    /// A plugin may invent a category. The official taxonomy is the registered vocabulary,
    /// not the only permitted one: the id only has to be a legal folder name, because that
    /// is what it becomes under the install root. Inventing an id never makes it official.
    /// </summary>
    [RelayCommand]
    private void AddCategory()
    {
        var category = NewCategory.Trim();
        if (!DirectoryNameValidator.ValidateName(category).IsValid)
        {
            Status = $"分区 '{category}' 不合法：只能是小写 ASCII、无空格。";
            return;
        }

        var option = Categories.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, category, StringComparison.Ordinal));

        if (option is null)
        {
            option = new PluginCategoryOption(category, "自定义");
            Categories.Add(option);
        }

        DraftCategory = option;
        NewCategory = string.Empty;
        Status = $"已选中分区 {category}（自定义分区，不是官方分区）。";
    }

    /// <summary>
    /// The line of the resource list the form is currently opened from, if any. The list is not a
    /// receipt: a line that cannot be opened again cannot be fixed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingResource))]
    [NotifyPropertyChangedFor(nameof(IsAddingResource))]
    [NotifyPropertyChangedFor(nameof(ResourceFormTitle))]
    private PluginResourceDraft? _editingResource;

    public bool IsEditingResource => EditingResource is not null;

    public bool IsAddingResource => EditingResource is null;

    public string ResourceFormTitle => IsEditingResource ? "编辑这条资源" : "添加一条资源";

    [RelayCommand]
    private void AddResource()
    {
        var id = DraftId.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            Status = "资源 id 不能为空。";
            return;
        }

        if (AllNodeIds().Any(nodeId => string.Equals(nodeId, id, StringComparison.Ordinal)))
        {
            Status = $"节点 id '{id}' 已经在资源树里了。";
            return;
        }

        if (TryBuildDraft(id) is not { } draft)
        {
            return;
        }

        var node = new PluginNodeDraft(id, draft.Name, PluginNodeDraft.KindOf(draft), draft);
        InsertNode(node, SelectedNode);
        node.IsExpanded = true;
        SyncFlatResources();
        ClearDraft();
        Status = $"已添加 {id}（{Resources.Count} 条资源）。";
    }

    /// <summary>
    /// 插入一个新节点：选中分组/文件夹时作为它的最后一个子节点；选中文件或未选中时进根层。
    /// 禁止把节点挂到会超过深度上限的位置。
    /// </summary>
    private void InsertNode(PluginNodeDraft node, PluginNodeDraft? target)
    {
        if (target is { IsGroup: false, IsFolder: false })
        {
            target = null;
        }

        // 目标层深度（根层记 1，未选目标时新节点落在根层）加上新节点自身的子树高度。
        var parentDepth = target is null ? 0 : NodeDepthFromRoot(target);
        var deepest = parentDepth + node.SubtreeDepth();
        if (deepest > PluginNode.MaxNodeDepth)
        {
            throw new InvalidOperationException($"插入后节点 '{node.NodeId}' 会超过 {PluginNode.MaxNodeDepth} 层的树深上限。");
        }

        if (target is null)
        {
            Nodes.Add(node);
        }
        else
        {
            target.Children.Add(node);
            target.IsExpanded = true;
        }
    }

    /// <summary>从树中移除一个节点（连同整棵子树）；返回是否找到。</summary>
    private bool RemoveNodeFromTree(PluginNodeDraft node)
    {
        if (Nodes.Contains(node))
        {
            Nodes.Remove(node);
            return true;
        }

        foreach (var root in Nodes)
        {
            if (TryRemoveChild(root, node))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryRemoveChild(PluginNodeDraft parent, PluginNodeDraft target)
    {
        if (parent.Children.Contains(target))
        {
            parent.Children.Remove(target);
            return true;
        }

        return parent.Children.Any(child => TryRemoveChild(child, target));
    }

    /// <summary>找到持有某条资源草稿的节点。</summary>
    private PluginNodeDraft? FindResourceOwner(PluginResourceDraft draft)
        => Nodes
            .SelectMany(root => root.EnumerateSelfAndDescendants())
            .FirstOrDefault(node => ReferenceEquals(node.Resource, draft));

    /// <summary>全树（含分组）稳定 id 枚举。</summary>
    private IEnumerable<string> AllNodeIds()
        => Nodes.SelectMany(root => root.EnumerateSelfAndDescendants().Select(node => node.NodeId));

    /// <summary>节点在当前树中的深度：根层为 1；不在树中时返回 0。</summary>
    private int NodeDepthFromRoot(PluginNodeDraft target)
    {
        foreach (var root in Nodes)
        {
            var depth = DepthWalk(root, target, 1);
            if (depth > 0)
            {
                return depth;
            }
        }

        return 0;
    }

    private static int DepthWalk(PluginNodeDraft node, PluginNodeDraft target, int depth)
    {
        if (ReferenceEquals(node, target))
        {
            return depth;
        }

        return node.Children
            .Select(child => DepthWalk(child, target, depth + 1))
            .FirstOrDefault(found => found > 0);
    }

    /// <summary>
    /// Validates the form and builds one resource from it. Shared by 「添加到列表」 and 「保存修改」,
    /// so an edited line is held to exactly the rules a new one is.
    /// </summary>
    private PluginResourceDraft? TryBuildDraft(string id)
    {
        var category = DraftCategory?.Id ?? string.Empty;

        // Official or invented: the id becomes a folder name under the install root, so that
        // is the rule that has to hold. An invented id never claims to be official.
        if (!DirectoryNameValidator.ValidateName(category).IsValid)
        {
            Status = $"分区 '{category}' 不合法：只能是小写 ASCII、无空格。";
            return null;
        }

        var directory = DraftDirectoryName.Trim();
        if (!DirectoryNameValidator.ValidateName(directory).IsValid)
        {
            Status = $"目录名 '{directory}' 不合法（ASCII、无保留名）。";
            return null;
        }

        var source = BuildSource();
        if (source is null)
        {
            return null;
        }

        return new PluginResourceDraft(
            id,
            string.IsNullOrWhiteSpace(DraftName) ? id : DraftName.Trim(),
            category,
            directory,
            DraftVersion.Trim(),
            DraftTier,
            DraftMode,
            source);
    }

    /// <summary>Opens one line of the resource list in the form so it can be changed.</summary>
    [RelayCommand]
    private void EditResource(PluginResourceDraft? draft)
    {
        if (draft is null)
        {
            return;
        }

        EditingResource = draft;

        DraftId = draft.Id;
        DraftName = draft.Name;
        DraftDirectoryName = draft.DirectoryName;
        DraftVersion = draft.Version;
        DraftTier = draft.Tier;
        DraftMode = draft.Mode;

        DraftKind = draft.Source.Kind;
        DraftShareUrl = draft.Source.ShareUrl ?? string.Empty;
        DraftUrl = draft.Source.Url ?? string.Empty;
        DraftWingetId = draft.Source.WingetId ?? string.Empty;
        DraftFileName = draft.Source.FileName ?? string.Empty;
        DraftLocatorPath = draft.Source.Locator?.Path ?? string.Empty;
        DraftProviderItemId = draft.Source.Locator?.ProviderItemId ?? string.Empty;
        DraftLocatorKind = draft.Source.Locator?.Kind ?? ResourceLocatorKind.File;
        DraftDetectionStatus = string.Empty;

        if (!string.IsNullOrWhiteSpace(draft.Source.ProviderId))
        {
            DraftProviderId = draft.Source.ProviderId;
        }

        var category = Categories.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, draft.CategoryId, StringComparison.Ordinal));

        if (category is not null)
        {
            DraftCategory = category;
        }

        Status = $"正在编辑 {draft.Id}：改完点「保存修改」写回这一条。";
    }

    /// <summary>Writes the form back over the line it was opened from.</summary>
    [RelayCommand]
    private void SaveResourceEdit()
    {
        if (EditingResource is not { } editing)
        {
            Status = "先从资源列表里点「编辑」挑一条。";
            return;
        }

        var id = DraftId.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            Status = "资源 id 不能为空。";
            return;
        }

        // The id is the key the install folder and the catalog entry use, so it may not collide
        // with another node. The node being edited keeps its own id legitimately.
        if (AllNodeIds().Any(nodeId => !string.Equals(nodeId, editing.Id, StringComparison.Ordinal)
                                       && string.Equals(nodeId, id, StringComparison.Ordinal)))
        {
            Status = $"节点 id '{id}' 已经在资源树里了。";
            return;
        }

        if (TryBuildDraft(id) is not { } draft)
        {
            return;
        }

        var owner = FindResourceOwner(editing);
        if (owner is null)
        {
            EditingResource = null;
            Status = "那条资源已经不在树里了。";
            return;
        }

        owner.Resource = draft;
        owner.NodeId = draft.Id;
        owner.Name = draft.Name;
        owner.Kind = PluginNodeDraft.KindOf(draft);
        SyncFlatResources();
        EditingResource = null;
        ClearDraft();
        Status = $"已保存 {id}（{Resources.Count} 条资源）。";
    }

    [RelayCommand]
    private void CancelResourceEdit()
    {
        EditingResource = null;
        ClearDraft();
        DraftShareUrl = string.Empty;
        DraftUrl = string.Empty;
        DraftWingetId = string.Empty;
        DraftFileName = string.Empty;
        DraftLocatorPath = string.Empty;
        DraftProviderItemId = string.Empty;
        DraftDetectionStatus = string.Empty;
        Status = "已取消编辑。";
    }

    /// <summary>Empties the fields that describe one resource; the plugin-level ones stay.</summary>
    private void ClearDraft()
    {
        DraftId = string.Empty;
        DraftName = string.Empty;
        DraftDirectoryName = string.Empty;
    }

    /// <summary>Builds the one source the form describes, refusing rather than guessing.</summary>
    private SoftwareSource? BuildSource()
    {
        switch (DraftKind)
        {
            case SourceKind.CloudShare:
                if (string.IsNullOrWhiteSpace(DraftProviderId))
                {
                    Status = "云盘分享来源必须选一个网盘平台。";
                    return null;
                }

                if (string.IsNullOrWhiteSpace(DraftShareUrl))
                {
                    Status = "云盘分享来源必须填分享链接。";
                    return null;
                }

                var shareLocator = BuildLocator();

                return new SoftwareSource
                {
                    Kind = SourceKind.CloudShare,
                    ProviderId = DraftProviderId.Trim(),
                    ShareUrl = DraftShareUrl.Trim(),
                    FileName = Null(DraftFileName),
                    Version = Null(DraftVersion),
                    Locator = shareLocator,

                    // A share link with no location inside it is a source that can only be looked for by
                    // name at the share root. Recording that is what lets the entry say it needs locating
                    // instead of failing later as if the file were gone.
                    NeedsLocatorResolution = shareLocator is null,
                };

            case SourceKind.Winget:
                if (string.IsNullOrWhiteSpace(DraftWingetId))
                {
                    Status = "winget 来源必须填包 id（例如 Microsoft.VisualStudioCode）。";
                    return null;
                }

                return new SoftwareSource
                {
                    Kind = SourceKind.Winget,
                    WingetId = DraftWingetId.Trim(),
                    Version = Null(DraftVersion),
                };

            case SourceKind.HttpUrl:
            case SourceKind.Official:
                if (string.IsNullOrWhiteSpace(DraftUrl))
                {
                    Status = (DraftKind == SourceKind.HttpUrl ? "直链来源" : "官方地址来源") + "必须填下载地址。";
                    return null;
                }

                return new SoftwareSource
                {
                    Kind = DraftKind,
                    Url = DraftUrl.Trim(),
                    FileName = Null(DraftFileName),
                    Version = Null(DraftVersion),
                };

            case SourceKind.Manual:
                // A manual source is an instruction to the user, not an address, so it needs no field:
                // demanding one would invent a requirement the model does not have.
                return new SoftwareSource
                {
                    Kind = SourceKind.Manual,
                    FileName = Null(DraftFileName),
                    Version = Null(DraftVersion),
                };

            default:
                if (string.IsNullOrWhiteSpace(DraftFileName))
                {
                    Status = "本地文件来源必须填文件名。";
                    return null;
                }

                return new SoftwareSource
                {
                    Kind = SourceKind.Local,
                    FileName = DraftFileName.Trim(),
                    Version = Null(DraftVersion),
                };
        }
    }

    /// <summary>
    /// The part of the share this resource lives in. Left out entirely when nothing was filled in, so
    /// a plugin that only names a file in the share root is written exactly the way it always was.
    /// </summary>
    private ResourceLocator? BuildLocator()
    {
        var locator = new ResourceLocator
        {
            Kind = DraftLocatorKind,
            Path = Null(DraftLocatorPath),
            Name = Null(DraftFileName),
            ProviderItemId = Null(DraftProviderItemId),
        };

        return locator.IsEmpty ? null : locator;
    }

    private static string? Null(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [RelayCommand]
    private void RemoveResource(PluginResourceDraft? draft)
    {
        if (draft is null)
        {
            return;
        }

        var node = FindResourceOwner(draft);
        if (node is null)
        {
            return;
        }

        RemoveNode(node);
        Status = $"已移除 {draft.Id}。";
    }

    /// <summary>从树中删掉一个节点（连同它的全部子节点），并同步扁平投影。</summary>
    private void RemoveNode(PluginNodeDraft node)
    {
        if (ReferenceEquals(SelectedNode, node) || node.EnumerateSelfAndDescendants().Contains(SelectedNode))
        {
            SelectedNode = null;
        }

        if (EditingResource is not null
            && node.EnumerateSelfAndDescendants().Any(descendant => ReferenceEquals(descendant.Resource, EditingResource)))
        {
            EditingResource = null;
        }

        RemoveNodeFromTree(node);
        SyncFlatResources();
    }

    /// <summary>
    /// 新增一个纯组织分组节点（不对应任何云盘资源）。名字为空时按序号兜底；
    /// 分组 id 使用 g_ 前缀且全树唯一。Task 5 的 TreeView 会绑定本命令。
    /// </summary>
    [RelayCommand]
    private void AddGroup(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            trimmed = "分组 " + (Nodes.Count + 1);
        }

        var nodeId = UniqueNodeId("g_group");
        var group = new PluginNodeDraft(nodeId, trimmed, PluginNodeKind.Group, null);

        try
        {
            InsertNode(group, SelectedNode);
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            return;
        }

        SyncFlatResources();
        SelectedNode = group;
        Status = $"已添加分组「{trimmed}」。";
    }

    /// <summary>分组 id 生成：与全树（含资源 id）不冲突。</summary>
    private string UniqueNodeId(string baseId)
    {
        var candidate = baseId;
        var counter = 2;

        while (AllNodeIds().Any(nodeId => string.Equals(nodeId, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = baseId + "_" + counter;
            counter++;
        }

        return candidate;
    }

    /// <summary>从树中移除指定节点（Task 5 TreeView 用）；分组/文件夹含子树时先弹确认。</summary>
    [RelayCommand]
    private async Task RemoveTreeNodeAsync(PluginNodeDraft? node)
    {
        if (node is null)
        {
            return;
        }

        // 子树里的节点（分组与资源）数；0 表示叶子，直接删不打扰。
        var descendantCount = node.EnumerateSelfAndDescendants().Skip(1).Count();
        if (descendantCount > 0)
        {
            var kindText = node.IsGroup ? "分组" : "文件夹资源";
            var ok = await _host.ConfirmAsync(
                "移除整个分支？",
                $"「{node.Name}」是一个{kindText}，下面还有 {descendantCount} 个节点。移除会把整支一起删掉，界面上没有撤销，确定吗？",
                "移除整支",
                "取消").ConfigureAwait(true);

            if (!ok)
            {
                Status = "已取消移除。";
                return;
            }
        }

        var wasGroup = node.IsGroup;
        var name = node.Name;
        RemoveNode(node);
        Status = wasGroup
            ? $"已移除分组「{name}」及其整支子树。"
            : descendantCount > 0
                ? $"已移除文件夹资源「{name}」及其整支子树。"
                : $"已移除 {name}。";
    }

    // ---------------------------------------------------------------- output

    [ObservableProperty]
    private string _status = "填好插件信息，再加至少一条资源，然后点「生成插件」。";

    [ObservableProperty]
    private string _generatedJson = string.Empty;

    [ObservableProperty]
    private string _generatedPath = string.Empty;

    /// <summary>Serializes the form into the plugin document, or null with a reason.</summary>
    public string? TryBuildJson(out string reason)
    {
        if (!DirectoryNameValidator.ValidateName(PluginId).IsValid)
        {
            reason = $"插件 id '{PluginId}' 不合法，文件名就是它。";
            return null;
        }

        if (string.IsNullOrWhiteSpace(PluginName))
        {
            reason = "插件名称不能为空。";
            return null;
        }

        if (string.IsNullOrWhiteSpace(PluginVersion))
        {
            reason = "插件版本不能为空。";
            return null;
        }

        if (Resources.Count == 0)
        {
            reason = "至少要有一条资源，否则导入会被判为「插件没有声明任何资源」。";
            return null;
        }

        var rootNodes = Nodes.Select(node => node.ToModel()).ToArray();

        // 写出前再跑一遍结构校验（表单已挡住常见情况，这里是防脏数据的第二道闸）。
        var treeIssues = PluginTree.Validate(rootNodes);
        if (treeIssues.Count > 0)
        {
            reason = "资源树有问题：" + treeIssues[0].Message;
            return null;
        }

        var plugin = ResourcePlugin.FromNodes(
            PluginId.Trim(),
            PluginName.Trim(),
            PluginVersion.Trim(),
            rootNodes,
            author: Null(PluginAuthor),
            description: Null(PluginDescription));

        reason = string.Empty;
        return PluginWriter.Write(plugin);
    }

    [RelayCommand]
    private void PreviewJson()
    {
        var json = TryBuildJson(out var reason);
        if (json is null)
        {
            Status = reason;
            return;
        }

        GeneratedJson = json;
        Status = "已在下方生成 JSON（可复制分享）。";
    }

    [RelayCommand]
    private void GeneratePlugin()
    {
        var json = TryBuildJson(out var reason);
        if (json is null)
        {
            Status = reason;
            return;
        }

        try
        {
            Directory.CreateDirectory(PluginsDirectory);
            var path = Path.Combine(PluginsDirectory, PluginId.Trim() + ResourcePlugin.FileExtension);
            File.WriteAllText(path, json);

            GeneratedJson = json;
            GeneratedPath = path;
            RefreshInstalledPlugins();
            Status = $"已生成 {Path.GetFileName(path)}（{Resources.Count} 条资源）。到「插件」页点「导入文件夹里的插件」即可生效。";
        }
        catch (Exception ex)
        {
            Status = $"生成失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Reads back a plugin document into the form so an existing file can be edited.</summary>
    [RelayCommand]
    private void ImportJson()
    {
        if (string.IsNullOrWhiteSpace(GeneratedJson))
        {
            Status = "先粘一段插件 JSON 到下面的框里。";
            return;
        }

        LoadFromJson(GeneratedJson);
    }

    /// <summary>The plugin files already in the folder, so one of them can be opened and changed.</summary>
    public ObservableCollection<PluginFileEntry> InstalledPlugins { get; } = new();

    /// <summary>Re-reads the folder: called when the builder is created and whenever a file is written.</summary>
    public void RefreshInstalledPlugins()
    {
        InstalledPlugins.Clear();

        if (!Directory.Exists(PluginsDirectory))
        {
            return;
        }

        foreach (var path in Directory.GetFiles(PluginsDirectory, "*" + ResourcePlugin.FileExtension)
                     .OrderBy(candidate => candidate, StringComparer.Ordinal))
        {
            try
            {
                var (plugin, _) = PluginReader.Parse(File.ReadAllText(path));
                InstalledPlugins.Add(new PluginFileEntry(
                    Path.GetFileName(path),
                    path,
                    plugin?.Id ?? Path.GetFileNameWithoutExtension(path),
                    plugin?.Name ?? "-",
                    plugin?.Resources.Count ?? 0));
            }
            catch (Exception ex)
            {
                Status = $"读不出 {Path.GetFileName(path)}：{ex.Message}";
            }
        }
    }

    /// <summary>
    /// Opens a plugin that is already in the folder in the form, so it can be changed and written
    /// back — the same path as pasting JSON, without the copy and paste.
    /// </summary>
    [RelayCommand]
    private void EditInstalledPlugin(PluginFileEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            GeneratedJson = File.ReadAllText(entry.Path);
            LoadFromJson(GeneratedJson);
            Status = $"已把 {entry.FileName} 读进表单；改完点「生成插件」就写回同一个文件。";
        }
        catch (Exception ex)
        {
            Status = $"读取失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Fills the form from a plugin document. One path for the paste box and for the folder list, so
    /// the two cannot drift apart.
    /// </summary>
    private void LoadFromJson(string json)
    {
        var (plugin, issues) = PluginReader.Parse(json);
        if (plugin is null)
        {
            Status = issues.Count > 0
                ? $"读入失败：{issues[0].Message}"
                : "读入失败：JSON 不是插件文档。";
            return;
        }

        PluginId = plugin.Id;
        PluginName = plugin.Name;
        PluginVersion = plugin.Version;
        PluginAuthor = plugin.Author ?? string.Empty;
        PluginDescription = plugin.Description ?? string.Empty;

        Nodes.Clear();
        Resources.Clear();
        SelectedNode = null;

        // schema 2 直接读树；schema 1 已由 PluginReader 迁移成根级资源节点。
        foreach (var modelNode in plugin.Nodes)
        {
            if (PluginNodeDraft.FromModel(modelNode) is { } draftNode)
            {
                Nodes.Add(draftNode);
            }
        }

        SyncFlatResources();

        if (issues.Count > 0)
        {
            Status = $"已读入插件 '{plugin.Id}'（{Resources.Count} 条资源），但文档有 {issues.Count} 处问题：{issues[0].Message} 保存时会按新结构重写。";
            return;
        }

        Status = $"已读入插件 '{plugin.Id}'（{Resources.Count} 条资源，schema {plugin.Schema}，保存后升级为 schema {ResourcePlugin.CurrentSchema}）。";
    }

    [RelayCommand]
    private void OpenPluginsFolder()
    {
        if (!Directory.Exists(PluginsDirectory))
        {
            Directory.CreateDirectory(PluginsDirectory);
        }

        var error = _host.Platform.Shell.OpenFolder(PluginsDirectory);
        Status = error ?? $"已打开 {PluginsDirectory}";
    }

    [RelayCommand]
    private void Reset()
    {
        PluginId = "my_plugin";
        PluginName = "我的插件";
        PluginVersion = "1.0.0";
        PluginAuthor = string.Empty;
        PluginDescription = string.Empty;
        Nodes.Clear();
        Resources.Clear();
        SelectedNode = null;
        DraftLocatorPath = string.Empty;
        DraftProviderItemId = string.Empty;
        DraftLocatorKind = ResourceLocatorKind.File;
        GeneratedJson = string.Empty;
        GeneratedPath = string.Empty;
        Status = "已清空。";
    }
}

/// <summary>One resource row in the builder, kept as the form entered it.</summary>
public sealed record PluginResourceDraft(
    string Id,
    string Name,
    string CategoryId,
    string DirectoryName,
    string Version,
    SoftwareTier Tier,
    InstallationMode Mode,
    SoftwareSource Source,
    IReadOnlyList<SoftwareSource>? AdditionalSources = null)
{
    public string Summary => AdditionalSources is { Count: > 0 } extra
        ? $"{Name} · {CategoryId} · {Source.Kind} +{extra.Count}"
        : $"{Name} · {CategoryId} · {Source.Kind}";

    /// <summary>The source the form edits, followed by the ones it does not touch.</summary>
    public IReadOnlyList<SoftwareSource> AllSources { get; } =
        new[] { Source }.Concat(AdditionalSources ?? Array.Empty<SoftwareSource>()).ToList();

    public PluginResource ToResource() => new()
    {
        Id = Id,
        Name = Name,
        CategoryId = CategoryId,
        DirectoryName = DirectoryName,
        Version = string.IsNullOrWhiteSpace(Version) ? null : Version,
        Tier = Tier,
        Mode = Mode,
        Sources = AllSources,
    };
}

/// <summary>A cloud platform a share source may name.</summary>
public sealed record PluginProviderOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>One registered official category a resource may point at (spec 15).</summary>
public sealed record PluginCategoryOption(string Id, string DisplayName)
{
    public override string ToString() => $"{DisplayName}（{Id}）";
}

/// <summary>One plugin file that is already in the plugin folder, offered for editing.</summary>
public sealed record PluginFileEntry(string FileName, string Path, string Id, string Name, int ResourceCount)
{
    public string Summary => $"{Name} · {Id} · {ResourceCount} 条资源";
}

/// <summary>
/// 分享树行的懒加载状态。子级只在第一次展开时读取一次；失败后可重试。
/// </summary>
public enum ShareLoadState
{
    NotLoaded,
    Loading,
    Loaded,
    Failed,
}

/// <summary>
/// 打开的分享树里的一行：文件夹行可以懒展开（按平台真实 itemId 读直接子级），
/// 也可以整夹选择；文件行只能选择。路径由祖先名实时拼成，不依赖任何预枚举。
/// </summary>
public sealed partial class ShareTreeRow : ObservableObject
{
    private readonly PluginBuilderViewModel _owner;

    public ShareTreeRow(
        PluginBuilderViewModel owner,
        CloudFile file,
        ShareTreeRow? parent,
        bool supportsFolderPackage)
    {
        _owner = owner;
        File = file;
        Parent = parent;
        SupportsFolderPackage = supportsFolderPackage;
    }

    public CloudFile File { get; }

    /// <summary>父行；根级行为 null。用于拼 locator 的可读路径。</summary>
    public ShareTreeRow? Parent { get; }

    public bool IsFolder => File.IsFolder;

    public bool IsFile => !File.IsFolder;

    /// <summary>当前平台是否支持整夹打包（只影响提示，不影响能否选择）。</summary>
    public bool SupportsFolderPackage { get; }

    /// <summary>懒加载出来的直接子级。文件行永远为空。</summary>
    public ObservableCollection<ShareTreeRow> Children { get; } = new();

    [ObservableProperty]
    private ShareLoadState _loadState = ShareLoadState.NotLoaded;

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandText))]
    private bool _isExpanded;

    public bool CanExpand => IsFolder;

    public bool IsLoading => LoadState == ShareLoadState.Loading;

    public bool ShowRetry => IsFolder && LoadState == ShareLoadState.Failed;

    public string ExpandText => IsExpanded ? "收起" : "展开";

    /// <summary>从分享根到本节点的可读路径（locator.Path 的口径）。</summary>
    public string Path
    {
        get
        {
            var names = new List<string>();
            for (var node = this; node is not null; node = node.Parent)
            {
                names.Add(node.File.Name);
            }

            names.Reverse();
            return string.Join('/', names);
        }
    }

    public string KindMark => IsFolder ? "[目录]" : "[文件]";

    /// <summary>文件夹行的整夹能力提示。</summary>
    public string FolderSupportText => IsFolder
        ? SupportsFolderPackage
            ? "可整夹打包下载"
            : "该平台暂不支持整夹打包，请展开选里面的文件"
        : string.Empty;

    public bool ShowFolderSupport => IsFolder;

    /// <summary>
    /// 展开/收起：只翻状态；真正的懒加载由 <see cref="OnIsExpandedChanged"/> 统一触发，
    /// 这样点 TreeView 自带箭头和点行内「展开」按钮走的是同一条路。
    /// </summary>
    [RelayCommand]
    private void ToggleExpand()
    {
        if (!IsFolder)
        {
            return;
        }

        IsExpanded = !IsExpanded;
    }

    /// <summary>失败后重试读取。</summary>
    [RelayCommand]
    private async Task RetryAsync()
    {
        if (!IsFolder)
        {
            return;
        }

        IsExpanded = true;
        await _owner.EnsureShareChildrenAsync(this).ConfigureAwait(true);
    }

    /// <summary>把这一条（文件或整个文件夹）作为独立资源加入插件树。</summary>
    [RelayCommand]
    private void Select() => _owner.SelectShareItem(this);

    partial void OnIsExpandedChanged(bool value)
    {
        // 首次展开（或之前失败）才按真实 itemId 读直接子级；收起/重复展开不产生请求。
        if (value && IsFolder && LoadState is ShareLoadState.NotLoaded or ShareLoadState.Failed)
        {
            _ = _owner.EnsureShareChildrenAsync(this);
        }
    }

    partial void OnLoadStateChanged(ShareLoadState value)
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowRetry));
    }
}
