using System.Text.Json;
using EriReborn.Engine.Ai;

namespace EriReborn.Engine.Plugins;

/// <summary>
/// Reader for the v2 generator contract (spec v3.1): the model answers each turn with one complete
/// draft <b>tree</b> — <c>{name, nodes:[{nodeType, name?, candidateId?, children?}]}</c> — instead of
/// a flat resource list.
///
/// <para>
/// The safety split is unchanged from v1: a <see cref="PluginDraftNodeTypes.Group"/> may only carry a
/// name, while every file/folder node must cite a candidateId that really exists in the shown tree,
/// must match that candidate's real kind, must be unique across the whole tree, and must not carry a
/// download fact the platform alone owns (hash, url, locator, …). Any node that fails those rules is
/// refused together with its subtree and reported in <see cref="PluginTreeDraftValidation.Rejected"/>.
/// </para>
/// </summary>
public static class PluginTreeDraftValidator
{
    /// <summary>Maximum nesting depth of one draft tree; mirrors the plugin model's own wall.</summary>
    public const int MaxDepth = PluginNode.MaxNodeDepth;

    /// <summary>How many nodes one answer may carry in total, groups included.</summary>
    public const int MaxNodes = 300;

    /// <summary>
    /// Fields that decide where bytes come from — same wall as v1: a model supplying one of these on
    /// a resource node is inventing a source, so the node is refused rather than corrected.
    /// </summary>
    private static readonly string[] ForbiddenFields =
    {
        "sha256", "provider", "providerid", "shareurl", "locator", "url", "links", "token", "cookie",
    };

    /// <summary>
    /// The v2 words the model is held to, shown verbatim. Limits are read from the constants so the
    /// contract text and the parser walls can never disagree.
    /// </summary>
    public static readonly string ContractHint =
        "只输出 JSON：{\"name\":\"插件名\",\"nodes\":["
        + "{\"nodeType\":\"group\",\"name\":\"分组名\",\"children\":[...]},"
        + "{\"nodeType\":\"file\",\"candidateId\":\"必须来自输入 nodes 的真实项\","
        + "\"platform\":\"Windows|Android|Linux|Unknown\",\"architecture\":\"x64|x86|arm64|Unknown\","
        + "\"confidence\":0.0,"
        + "\"children\":[...仅 folder 可再嵌套...]}]}。"
        + "nodeType 只能是 group、file、folder 三个词之一。"
        + "group 只给 name，不许给 candidateId；file/folder 必须给 candidateId，且必须逐字来自输入树真实存在的项，一个都不许编。"
        + "nodeType 必须与真实类型一致：输入里是 folder 的项不能标成 file，反之亦然。"
        + "同一个 candidateId 在整棵树里只能出现一次。"
        + "嵌套深度最多 " + MaxDepth + " 层；整棵树节点总数最多 " + MaxNodes + " 个（含分组）。"
        + "不要输出 sha256、网盘平台标识、分享链接、下载地址、locator、Token 或 Cookie —— 那些由 EriReborn 从真实数据里取。"
        + "不要输出 version：版本必须由用户确认，猜出来的版本比空着更坏。"
        + "不要输出 reason 字段——省下的 token 用来输出更多节点。"
        + "无法判断平台或架构时写 Unknown，并把 confidence 降低，不要猜。"
        + "每一轮都要输出一棵完整、收得了尾的 JSON 草稿树：它是对上一版的整体修订，不是增量片段；"
        + "写不下就少写几个节点，绝不要写到一半停下。";

    /// <summary>System prompt for the multi-turn tree generator (wired up by the builder panel).</summary>
    public static readonly string SystemPrompt =
        "你是 EriReborn Plugin Generator（草稿树模式）。"
        + "你的任务不是普通聊天，而是根据 EriReborn 提供的真实云端资源树，把用户的每一轮指令落实为一棵结构化插件草稿树。"
        + "你只能使用输入 nodes 中真实存在的资源：严格禁止编造不存在的文件或文件夹、编造文件大小、编造 SHA256、"
        + "编造下载 URL、编造网盘平台、编造资源位置、编造 Token/Cookie/账号信息、把多个真实资源合并成不存在的资源、"
        + "或在信息不足时猜测。无法确定时写 Unknown，Unknown 不等于 Missing。"
        + "用户可以连续多轮要求你增删分组、移动节点、整包选择文件夹；每一轮你都返回修订后的完整草稿树。"
        + "你不负责下载、不负责登录云盘、不负责修改系统、不负责执行安装，只生成草稿树。"
        + ContractHint;

    /// <summary>
    /// Parses one model answer into a validated draft tree.
    /// </summary>
    /// <param name="json">What the model returned this turn.</param>
    /// <param name="tree">The real share contents it was shown.</param>
    public static PluginTreeDraftValidation Parse(string? json, PluginCandidateTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (string.IsNullOrWhiteSpace(json))
        {
            return PluginTreeDraftValidation.Fail("(整份草稿)", "模型没有返回内容。");
        }

        // Same truncated-answer handling as v1: a cut-off object must not be salvaged into a draft.
        var extraction = AiActionPlanner.ExtractObject(json);
        if (extraction.Source == JsonObjectSource.CutOff)
        {
            return PluginTreeDraftValidation.Fail(
                "(整份草稿)",
                "模型的回答被输出长度上限截断了：草稿树 JSON 没有收尾就停住了，"
                + "所以里面即使写着内容也不能用。请让它减少节点数（或把理由写得更短）后重试。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(extraction.Text);
        }
        catch (JsonException ex)
        {
            return PluginTreeDraftValidation.Fail("(整份草稿)", "模型的回答不是可解析的 JSON：" + ex.Message);
        }

        var issues = new List<PluginDraftIssue>();
        var rejected = new List<PluginDraftIssue>();

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return PluginTreeDraftValidation.Fail("(整份草稿)", "模型的回答不是一个 JSON 对象。");
            }

            var name = Text(root, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return PluginTreeDraftValidation.Fail("(整份草稿)", "草稿没有给出插件名。");
            }

            if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            {
                return PluginTreeDraftValidation.Fail("(整份草稿)", "草稿没有 nodes 数组。");
            }

            var state = new ParseState(tree, issues, rejected);

            foreach (var entry in nodes.EnumerateArray())
            {
                var parsed = ReadNode(entry, depth: 1, state);
                if (parsed is not null)
                {
                    state.Roots.Add(parsed);
                }
            }

            if (state.AcceptedCount == 0)
            {
                return new PluginTreeDraftValidation(null, issues, rejected);
            }

            return new PluginTreeDraftValidation(new PluginDraftTree(name.Trim(), state.Roots), issues, rejected);
        }
    }

    private sealed class ParseState(
        PluginCandidateTree tree,
        List<PluginDraftIssue> issues,
        List<PluginDraftIssue> rejected)
    {
        public PluginCandidateTree Tree { get; } = tree;

        public List<PluginDraftIssue> Issues { get; } = issues;

        public List<PluginDraftIssue> Rejected { get; } = rejected;

        public List<PluginDraftNode> Roots { get; } = new();

        /// <summary>A candidateId may survive once in the whole tree.</summary>
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        public int AcceptedCount { get; set; }
    }

    /// <summary>
    /// Validates one JSON node and its subtree. Returns null when the node is refused; a refusal
    /// also skips the subtree, because children cannot attach to a node that did not survive.
    /// </summary>
    private static PluginDraftNode? ReadNode(JsonElement entry, int depth, ParseState state)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            state.Rejected.Add(new PluginDraftIssue("(一个节点)", "不是一个对象。"));
            return null;
        }

        var nodeType = Text(entry, "nodeType");
        return nodeType switch
        {
            PluginDraftNodeTypes.Group => ReadGroup(entry, depth, state),
            PluginDraftNodeTypes.File or PluginDraftNodeTypes.Folder => ReadResource(entry, nodeType!, depth, state),
            _ => Refuse(entry, "无法识别的 nodeType「" + (nodeType ?? "(空)") + "」，只允许 group/file/folder。", state),
        };
    }

    private static PluginDraftNode? ReadGroup(JsonElement entry, int depth, ParseState state)
    {
        var subject = Text(entry, "name") is { Length: > 0 } given ? given : "(缺少 name 的分组)";

        var name = Text(entry, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Refuse(entry, "group 节点没有 name。", state, subject);
        }

        name = name.Trim();
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return Refuse(entry, "分组名「" + name + "」含有路径分隔符（/ 或 \\），分组名必须是一个单纯的名字。", state, name);
        }

        if (state.AcceptedCount >= MaxNodes)
        {
            return Refuse(entry, $"整棵树最多 {MaxNodes} 个节点。", state, name);
        }

        var children = ReadChildren(entry, depth, state, allowOnResource: false, isResource: false);
        state.AcceptedCount++;
        return new DraftGroupNode(name, children);
    }

    private static PluginDraftNode? ReadResource(
        JsonElement entry,
        string nodeType,
        int depth,
        ParseState state)
    {
        var candidateId = Text(entry, "candidateId");
        if (string.IsNullOrWhiteSpace(candidateId))
        {
            return Refuse(entry, nodeType + " 节点没有 candidateId，无法指向真实条目。", state);
        }

        var candidate = state.Tree.Find(candidateId);
        if (candidate is null)
        {
            return Refuse(entry, "输入的真实资源树里没有这个 candidateId，已拒绝。", state, candidateId);
        }

        // The model must label a real folder "folder" and a real file "file"; the wrong word is not
        // a salvageable typo, it would download the wrong thing (a folder-as-file is not a file).
        var statedFolder = string.Equals(nodeType, PluginDraftNodeTypes.Folder, StringComparison.Ordinal);
        if (statedFolder != candidate.IsFolder)
        {
            return Refuse(
                entry,
                $"节点类型标成 {nodeType}，但该项在真实树里是 {(candidate.IsFolder ? "folder" : "file")}，类型不匹配，已拒绝。",
                state,
                candidateId);
        }

        var fabricated = ForbiddenFields
            .Where(field => entry.TryGetProperty(field, out _))
            .ToArray();
        if (fabricated.Length > 0)
        {
            return Refuse(
                entry,
                "节点自己给出了下载事实（" + string.Join("、", fabricated) + "），这些只能由真实数据决定，已拒绝。",
                state,
                candidateId);
        }

        if (!state.Seen.Add(candidateId))
        {
            return Refuse(entry, "同一个条目在草稿树里被重复列入。", state, candidateId);
        }

        if (state.AcceptedCount >= MaxNodes)
        {
            return Refuse(entry, $"整棵树最多 {MaxNodes} 个节点。", state, candidateId);
        }

        // Field-only mistakes survive: the label is dropped and the real fact is kept (same as v1).
        var statedName = Text(entry, "name");
        if (!string.IsNullOrWhiteSpace(statedName)
            && !string.Equals(statedName, candidate.Name, StringComparison.Ordinal))
        {
            state.Issues.Add(new PluginDraftIssue(
                candidateId,
                $"模型给出的名字「{statedName}」与分享里的真实名字「{candidate.Name}」不一致，已采用真实名字。"));
        }

        var statedPath = Text(entry, "path");
        if (!string.IsNullOrWhiteSpace(statedPath)
            && !string.Equals(statedPath, candidate.Path, StringComparison.Ordinal))
        {
            state.Issues.Add(new PluginDraftIssue(
                candidateId,
                "模型给出的位置与真实记录不一致，已采用真实位置。"));
        }

        var statedVersion = Text(entry, "version");
        if (!string.IsNullOrWhiteSpace(statedVersion)
            && !string.Equals(statedVersion, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            state.Issues.Add(new PluginDraftIssue(
                candidateId,
                $"模型给出的版本「{statedVersion}」未被采用（版本必须由真实数据或用户确认）。"));
        }

        // Only a folder node may carry children in the draft; a file's children make no sense and
        // are dropped (reported) rather than silently attached or used to refuse the real file.
        var children = ReadChildren(entry, depth, state, allowOnResource: candidate.IsFolder, isResource: true);

        state.AcceptedCount++;
        return new DraftResourceNode(
            candidate,
            Text(entry, "platform"),
            Text(entry, "architecture"),
            Number(entry, "confidence"),
            Text(entry, "reason") ?? string.Empty,
            children);
    }

    /// <summary>
    /// Reads a "children" array when the parent kind allows it. Children past <see cref="MaxDepth"/>
    /// are refused as subtrees; children on a file node are dropped with an issue.
    /// </summary>
    private static IReadOnlyList<PluginDraftNode> ReadChildren(
        JsonElement entry,
        int depth,
        ParseState state,
        bool allowOnResource,
        bool isResource)
    {
        if (!entry.TryGetProperty("children", out var childElement))
        {
            return Array.Empty<PluginDraftNode>();
        }

        if (childElement.ValueKind != JsonValueKind.Array)
        {
            state.Issues.Add(new PluginDraftIssue(SubjectOf(entry), "children 不是数组，已忽略。"));
            return Array.Empty<PluginDraftNode>();
        }

        if (isResource && !allowOnResource)
        {
            state.Issues.Add(new PluginDraftIssue(SubjectOf(entry), "file 节点不能包含 children，其下内容已忽略。"));
            return Array.Empty<PluginDraftNode>();
        }

        if (depth >= MaxDepth)
        {
            foreach (var _ in childElement.EnumerateArray())
            {
                state.Rejected.Add(new PluginDraftIssue(
                    SubjectOf(entry),
                    $"草稿嵌套超过 {MaxDepth} 层，更深的子树已忽略。"));
            }

            return Array.Empty<PluginDraftNode>();
        }

        var children = new List<PluginDraftNode>();
        foreach (var child in childElement.EnumerateArray())
        {
            var parsed = ReadNode(child, depth + 1, state);
            if (parsed is not null)
            {
                children.Add(parsed);
            }
        }

        return children;
    }

    /// <summary>Refuses a node and, crucially, does not walk its subtree.</summary>
    private static PluginDraftNode? Refuse(JsonElement entry, string reason, ParseState state, string? subject = null)
    {
        state.Rejected.Add(new PluginDraftIssue(subject ?? SubjectOf(entry), reason));
        return null;
    }

    private static string SubjectOf(JsonElement entry)
        => Text(entry, "candidateId")
           ?? Text(entry, "name")
           ?? "(一个节点)";

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;
}
