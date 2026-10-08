using System.Text.Encodings.Web;
using System.Text.Json;
using EriReborn.Engine.Ai;

namespace EriReborn.Engine.Plugins;

/// <summary>
/// One item that really exists inside a share, read from the platform itself.
///
/// <para>
/// This is the only source of truth a generated plugin is allowed to point at. The id is the
/// platform's own item id when it has one, because that survives a rename or a move; the path is
/// carried beside it as the readable half (spec 30).
/// </para>
/// </summary>
public sealed record PluginCandidate(
    string CandidateId,
    string Name,
    bool IsFolder,
    long? SizeBytes,
    string Path)
{
    /// <summary>How the prompt names the two kinds, so the model is shown the real shape.</summary>
    public string TypeText => IsFolder ? "folder" : "file";
}

/// <summary>
/// One node of the real share tree shown to the model (spec v3.1): a candidate plus the candidates
/// nested directly under it. Built from the flat BFS list by <see cref="PluginCandidateNesting"/>;
/// nothing here is invented beyond filling an intermediate folder the flat list happened to omit
/// (its id falls back to its path, the same fallback the collector uses).
/// </summary>
public sealed record PluginCandidateNode(
    PluginCandidate Candidate,
    IReadOnlyList<PluginCandidateNode> Children)
{
    public string CandidateId => Candidate.CandidateId;

    public string Name => Candidate.Name;

    public bool IsFolder => Candidate.IsFolder;

    public string Path => Candidate.Path;

    /// <summary>The node and everything beneath it, depth first.</summary>
    public IEnumerable<PluginCandidateNode> EnumerateSelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.EnumerateSelfAndDescendants())
            {
                yield return descendant;
            }
        }
    }
}

/// <summary>
/// Rebuilds the nested share tree from the collector's flat, path-bearing list.
///
/// <para>
/// The collector walks breadth first and emits every folder it enters, so a parent normally already
/// exists before its children; the builder still tolerates a missing intermediate (and arbitrary
/// ordering) rather than dropping the subtree beneath it (spec v3.1 树铁律：不得扁平化重挂/丢节点).
/// </para>
/// </summary>
public static class PluginCandidateNesting
{
    public static IReadOnlyList<PluginCandidateNode> Build(IReadOnlyList<PluginCandidate> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var byPath = new Dictionary<string, PluginCandidate>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                byPath.TryAdd(item.Path, item);
            }
        }

        var nodes = new Dictionary<string, MutableNode>(StringComparer.Ordinal);
        var roots = new List<MutableNode>();

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            var segments = Split(item.Path);

            // A folder whose child appeared first in the input was already created from this same
            // candidate while ensuring the chain; creating it again would duplicate a real node.
            if (nodes.ContainsKey(item.Path))
            {
                continue;
            }

            var parent = EnsureChain(segments, segments.Length - 1, byPath, nodes, roots);
            var node = new MutableNode(item);
            nodes[item.Path] = node;
            if (parent is null)
            {
                roots.Add(node);
            }
            else
            {
                parent.Children.Add(node);
            }
        }

        return roots.Select(Freeze).ToList();
    }

    /// <summary>
    /// Ensures every folder prefix above <paramref name="depth"/> exists, returning the immediate
    /// parent node (null when the item itself sits at the share root).
    /// </summary>
    private static MutableNode? EnsureChain(
        string[] segments,
        int depth,
        Dictionary<string, PluginCandidate> byPath,
        Dictionary<string, MutableNode> nodes,
        List<MutableNode> roots)
    {
        MutableNode? parent = null;
        var prefix = string.Empty;

        for (var i = 0; i < depth; i++)
        {
            prefix = prefix.Length == 0 ? segments[i] : prefix + "/" + segments[i];
            if (nodes.TryGetValue(prefix, out var existing))
            {
                parent = existing;
                continue;
            }

            // Real BFS output always carries the folder itself; this fallback only covers a list
            // that skipped it. The synthesised id is its path — exactly the collector's id fallback.
            var candidate = byPath.TryGetValue(prefix, out var real)
                ? real
                : new PluginCandidate(prefix, segments[i], IsFolder: true, SizeBytes: null, prefix);

            var created = new MutableNode(candidate);
            nodes[prefix] = created;
            if (parent is null)
            {
                roots.Add(created);
            }
            else
            {
                parent.Children.Add(created);
            }

            parent = created;
        }

        return parent;
    }

    private static string[] Split(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static PluginCandidateNode Freeze(MutableNode node)
        => new(node.Candidate, node.Children.Select(Freeze).ToList());

    private sealed class MutableNode(PluginCandidate candidate)
    {
        public PluginCandidate Candidate { get; } = candidate;

        public List<MutableNode> Children { get; } = new();
    }
}

/// <summary>
/// The real contents of one share, as handed to a model.
///
/// <para>
/// Bounded on purpose, and truncation is stated rather than silent: a tree the model does not know
/// was cut would be reasoned about as if it were complete (spec 151).
/// </para>
/// </summary>
public sealed record PluginCandidateTree(
    string ProviderId,
    string ShareUrl,
    IReadOnlyList<PluginCandidate> Items,
    int OmittedCount = 0,
    IReadOnlyList<string>? UnreadableFolders = null)
{
    public bool IsTruncated => OmittedCount > 0;

    /// <summary>Folders the platform would not open, so a gap in the tree has a name.</summary>
    public IReadOnlyList<string> Unreadable => UnreadableFolders ?? Array.Empty<string>();

    private IReadOnlyList<PluginCandidateNode>? _roots;

    /// <summary>
    /// The flat list rebuilt into a real nested tree (spec v3.1). Built lazily once; the flat
    /// <see cref="Items"/> stays available for id lookup and for callers that still want the BFS view.
    /// </summary>
    public IReadOnlyList<PluginCandidateNode> Roots => _roots ??= PluginCandidateNesting.Build(Items);

    /// <summary>The candidate with this id, or null. Never a fuzzy match: a wrong pick is a wrong file.</summary>
    public PluginCandidate? Find(string? candidateId)
        => string.IsNullOrWhiteSpace(candidateId)
            ? null
            : Items.FirstOrDefault(item => string.Equals(item.CandidateId, candidateId, StringComparison.Ordinal));

    /// <summary>
    /// The tree as the model receives it: nested <c>nodes</c>, and only what the platform reported.
    ///
    /// <para>
    /// v3.1 changed the shape from a flat item list to nested nodes (folders carry their children),
    /// because the draft the model must answer with is itself a tree — answering in one shape while
    /// being shown another cost both tokens and mistakes. The flat <see cref="Items"/> list is still
    /// the backing data; <c>path</c> is kept on every node so nothing readable is lost.
    /// </para>
    ///
    /// <para>
    /// Written as readable UTF-8 rather than the default escaped form: a share full of Chinese names
    /// would otherwise arrive as <c>\u5DE5\u5177</c>, which costs tokens and reads far worse to the
    /// model that has to reason about it.
    /// </para>
    /// </summary>
    public string ToPromptJson()
    {
        var payload = new
        {
            provider = ProviderId,
            shareUrl = ShareUrl,
            truncated = IsTruncated,
            omittedCount = OmittedCount,
            unreadableFolders = Unreadable,
            nodes = Roots.Select(NodeDto.From),
        };

        return JsonSerializer.Serialize(payload, PromptJsonOptions);
    }

    /// <summary>Wire shape for one prompt node; leaves carry no children property at all.</summary>
    private sealed class NodeDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("candidateId")]
        public required string CandidateId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public required string Name { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public required string Type { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("size")]
        public long? Size { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("path")]
        public required string Path { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("children")]
        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<NodeDto>? Children { get; init; }

        public static NodeDto From(PluginCandidateNode node) => new()
        {
            CandidateId = node.CandidateId,
            Name = node.Name,
            Type = node.Candidate.TypeText,
            Size = node.Candidate.SizeBytes,
            Path = node.Path,
            Children = node.Children.Count == 0 ? null : node.Children.Select(From).ToList(),
        };
    }

    /// <summary>Readable names, and no HTML escaping: this text goes to a model, not to a browser.</summary>
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// One resource of a generated draft, after it has been tied back to a real item.
///
/// <para>
/// The name, the size, the path and the item id come from <see cref="Candidate"/> — never from the
/// model. What the model contributes is classification: which platform and architecture it thinks
/// this is, how sure it is, and why. That split is the whole point: a model cannot fabricate a file
/// that is not there, because nothing it says about the file's identity is used.
/// </para>
/// </summary>
public sealed record PluginDraftResource(
    PluginCandidate Candidate,
    string? Platform,
    string? Architecture,
    double? Confidence,
    string Reason)
{
    public string CandidateId => Candidate.CandidateId;

    public string Name => Candidate.Name;

    public bool IsFolder => Candidate.IsFolder;

    public long? SizeBytes => Candidate.SizeBytes;
}

/// <summary>A generated plugin draft: proposed metadata plus resources tied to real items.</summary>
public sealed record PluginDraft(
    string Name,
    IReadOnlyList<PluginDraftResource> Resources);

/// <summary>Something the model said that was not used, and why. Carried so nothing shrinks quietly.</summary>
public sealed record PluginDraftIssue(string Subject, string Reason);

/// <summary>
/// What survived validation: the draft, the fields that were dropped but kept the resource, and the
/// resources that were refused outright.
///
/// <para>
/// A resource is refused when it would point somewhere other than real data — an id that is not in
/// the tree, a download fact the model invented (a hash, a platform, a share link, a download
/// address), or a repeat of the same item. A field that only misdescribes the item (a wrong
/// display name, a version nobody verified) is dropped and reported instead, because throwing away
/// a real resource over a label would lose more than it protects.
/// </para>
/// </summary>
public sealed record PluginDraftValidation(
    PluginDraft? Draft,
    IReadOnlyList<PluginDraftIssue> Issues,
    IReadOnlyList<PluginDraftIssue> Rejected)
{
    public bool HasResources => Draft is { Resources.Count: > 0 };

    public static PluginDraftValidation Fail(string subject, string reason)
        => new(null, Array.Empty<PluginDraftIssue>(), new[] { new PluginDraftIssue(subject, reason) });
}

/// <summary>The contract the model is told to answer with, and the reader that holds it to it.</summary>
public static class PluginDraftValidator
{
    /// <summary>
    /// How many resources one draft may carry. A wall of entries is not a review — but 60 cut real
    /// shares down to a fraction of their files, and the point of generation is coverage, so the
    /// cap now matches what one 8192-token answer can actually finish writing.
    /// </summary>
    public const int MaxResources = 150;

    /// <summary>
    /// Fields that decide where the bytes come from. A model that supplies one of these is not
    /// describing a resource it was shown, it is inventing where to fetch it from — so the resource
    /// is refused rather than quietly corrected (spec 213.3/6, "AI 不能自己生成 Locator/SHA256").
    /// </summary>
    private static readonly string[] ForbiddenFields =
    {
        "sha256", "provider", "providerid", "shareurl", "locator", "url", "links", "token", "cookie",
    };

    /// <summary>
    /// The words the model is held to. Shown to it verbatim.
    /// </summary>
    /// <remarks>
    /// Not <c>const</c>: the entry limit is part of the contract and is read from <see cref="MaxResources"/>
    /// rather than typed again here, so the number the model is given and the number it is held to cannot
    /// drift apart.
    /// </remarks>
    public static readonly string ContractHint =
        "只输出 JSON：{\"name\":\"插件名\","
        + "\"resources\":[{\"candidateId\":\"必须来自输入数据\",\"platform\":\"Windows|Android|Linux|Unknown\","
        + "\"architecture\":\"x64|x86|arm64|Unknown\",\"confidence\":0.0,\"reason\":\"理由\"}]}。"
        + "candidateId 只能取自输入 items 里真实存在的项，一个都不许编。"
        + "不要输出 sha256、网盘平台、分享链接、下载地址、位置、Token 或 Cookie —— 那些由 EriReborn 从真实数据里取。"
        + "不要输出 version：版本必须由用户确认，猜出来的版本比空着更坏。"
        + "无法判断平台或架构时写 Unknown，并把 confidence 降低，不要猜。"
        // The answer has to fit in one reply. Asked for 60 entries with a paragraph each, models write
        // until the output limit stops them mid-string, and a draft that never closes is worth nothing —
        // the budget is part of the contract so the model spends it on entries rather than on prose.
        + "一次最多 " + MaxResources + " 条资源；reason 写成一句话、不超过 20 个字。"
        + "整个回答必须是一个完整、收得了尾的 JSON：写不下就少写几条，绝不要写到一半停下。";

    /// <summary>
    /// Reads a model's answer and refuses everything that is not backed by the tree.
    /// </summary>
    /// <param name="json">What the model returned.</param>
    /// <param name="tree">The real share contents it was shown.</param>
    /// <param name="maxResources">Override for tests and for callers that want a smaller review.</param>
    public static PluginDraftValidation Parse(
        string? json,
        PluginCandidateTree tree,
        int maxResources = MaxResources)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (string.IsNullOrWhiteSpace(json))
        {
            return PluginDraftValidation.Fail("(整份草稿)", "模型没有返回内容。");
        }

        // Read first, parse second, and say why when the answer is a fragment: an answer cut off at the
        // output limit leaves an object that never closes, and reading the first object that happens to
        // parse would hand this method a nested entry — which parses, has no name, and turns a truncated
        // answer into "草稿没有给出插件名", a sentence about the wrong problem (it hides the real fix:
        // ask for fewer entries).
        var extraction = AiActionPlanner.ExtractObject(json);
        if (extraction.Source == JsonObjectSource.CutOff)
        {
            return PluginDraftValidation.Fail(
                "(整份草稿)",
                "模型的回答被输出长度上限截断了：草稿 JSON 没有收尾就停住了，"
                + "所以里面即使写着内容也不能用。请减少一次生成的条目数（或让它把理由写得更短）后重试。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(extraction.Text);
        }
        catch (JsonException ex)
        {
            return PluginDraftValidation.Fail("(整份草稿)", "模型的回答不是可解析的 JSON：" + ex.Message);
        }

        var issues = new List<PluginDraftIssue>();
        var rejected = new List<PluginDraftIssue>();

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return PluginDraftValidation.Fail("(整份草稿)", "模型的回答不是一个 JSON 对象。");
            }

            var name = Text(root, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return PluginDraftValidation.Fail("(整份草稿)", "草稿没有给出插件名。");
            }

            if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            {
                return PluginDraftValidation.Fail("(整份草稿)", "草稿没有 resources 数组。");
            }

            var accepted = new List<PluginDraftResource>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var limit = Math.Max(0, maxResources);

            foreach (var entry in resources.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    rejected.Add(new PluginDraftIssue("(一项资源)", "不是一个对象。"));
                    continue;
                }

                var candidateId = Text(entry, "candidateId");
                if (string.IsNullOrWhiteSpace(candidateId))
                {
                    rejected.Add(new PluginDraftIssue("(缺少 candidateId)", "资源没有指向任何真实条目。"));
                    continue;
                }

                // The rule the whole design exists for: the model may only choose from what it was
                // shown. An id it invented is refused, not corrected (spec 213.6).
                var candidate = tree.Find(candidateId);
                if (candidate is null)
                {
                    rejected.Add(new PluginDraftIssue(
                        candidateId,
                        "输入的真实资源树里没有这个 candidateId，已拒绝。"));
                    continue;
                }

                var fabricated = ForbiddenFields
                    .Where(field => entry.TryGetProperty(field, out _))
                    .ToArray();

                if (fabricated.Length > 0)
                {
                    rejected.Add(new PluginDraftIssue(
                        candidateId,
                        "资源自己给出了下载事实（" + string.Join("、", fabricated) + "），"
                        + "这些只能由真实数据决定，已拒绝。"));
                    continue;
                }

                if (!seen.Add(candidateId))
                {
                    rejected.Add(new PluginDraftIssue(candidateId, "同一个条目被重复列入。"));
                    continue;
                }

                if (accepted.Count >= limit)
                {
                    rejected.Add(new PluginDraftIssue(candidateId, $"一次最多 {limit} 条资源。"));
                    continue;
                }

                var statedName = Text(entry, "name");
                if (!string.IsNullOrWhiteSpace(statedName)
                    && !string.Equals(statedName, candidate.Name, StringComparison.Ordinal))
                {
                    // Reported, then ignored: the real name is what the share says.
                    issues.Add(new PluginDraftIssue(
                        candidateId,
                        $"模型给出的名字「{statedName}」与分享里的真实名字「{candidate.Name}」不一致，已采用真实名字。"));
                }

                var statedPath = Text(entry, "path");
                if (!string.IsNullOrWhiteSpace(statedPath)
                    && !string.Equals(statedPath, candidate.Path, StringComparison.Ordinal))
                {
                    issues.Add(new PluginDraftIssue(
                        candidateId,
                        "模型给出的位置与真实记录不一致，已采用真实位置。"));
                }

                var statedVersion = Text(entry, "version");
                if (!string.IsNullOrWhiteSpace(statedVersion)
                    && !string.Equals(statedVersion, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    // Not used at all: a version nobody verified is worse than an empty one (spec 6).
                    issues.Add(new PluginDraftIssue(
                        candidateId,
                        $"模型给出的版本「{statedVersion}」未被采用（版本必须由真实数据或用户确认）。"));
                }

                accepted.Add(new PluginDraftResource(
                    candidate,
                    Text(entry, "platform"),
                    Text(entry, "architecture"),
                    Number(entry, "confidence"),
                    Text(entry, "reason") ?? string.Empty));
            }

            if (accepted.Count == 0)
            {
                return new PluginDraftValidation(null, issues, rejected);
            }

            return new PluginDraftValidation(new PluginDraft(name.Trim(), accepted), issues, rejected);
        }
    }

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
