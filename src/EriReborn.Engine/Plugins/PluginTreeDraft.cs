namespace EriReborn.Engine.Plugins;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// One node of a model-produced draft tree (spec v3.1 contract v2).
///
/// <para>
/// Three kinds, matching the plugin tree itself: a <b>group</b> carries only a name the model made
/// up; a <b>folder</b> and a <b>file</b> are bound to a real <see cref="PluginCandidate"/> the app
/// read from the platform. Nothing about a resource node's identity ever comes from the model.
/// </para>
/// </summary>
public abstract record PluginDraftNode(string Name, IReadOnlyList<PluginDraftNode> Children)
{
    /// <summary>The contract word: "group", "file" or "folder".</summary>
    public abstract string NodeType { get; }

    public bool IsGroup => string.Equals(NodeType, PluginDraftNodeTypes.Group, StringComparison.Ordinal);

    /// <summary>The node and everything beneath it, depth first.</summary>
    public IEnumerable<PluginDraftNode> EnumerateSelfAndDescendants()
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

/// <summary>The contract words for node types, in one place so prompt and reader cannot drift.</summary>
public static class PluginDraftNodeTypes
{
    public const string Group = "group";
    public const string File = "file";
    public const string Folder = "folder";
}

/// <summary>A classification group the model invented. It has a name and children, nothing else.</summary>
public sealed record DraftGroupNode(
    string Name,
    IReadOnlyList<PluginDraftNode> Children)
    : PluginDraftNode(Name, Children)
{
    public override string NodeType => PluginDraftNodeTypes.Group;
}

/// <summary>
/// A real file or folder the model picked. Bound to the candidate by id during validation, so the
/// name, size, path and any later locator all come from the share, never from the answer.
/// The model only contributes the annotations.
/// </summary>
public sealed record DraftResourceNode(
    PluginCandidate Candidate,
    string? Platform,
    string? Architecture,
    double? Confidence,
    string Reason,
    IReadOnlyList<PluginDraftNode> Children)
    : PluginDraftNode(Candidate.Name, Children)
{
    public override string NodeType => Candidate.IsFolder ? PluginDraftNodeTypes.Folder : PluginDraftNodeTypes.File;

    public string CandidateId => Candidate.CandidateId;

    public bool IsFolder => Candidate.IsFolder;

    public long? SizeBytes => Candidate.SizeBytes;
}

/// <summary>The full validated draft: a plugin name plus a tree of nodes.</summary>
public sealed record PluginDraftTree(
    string Name,
    IReadOnlyList<PluginDraftNode> Nodes)
{
    /// <summary>Every node across the whole tree, depth first.</summary>
    public IEnumerable<PluginDraftNode> EnumerateAll() => Nodes.SelectMany(node => node.EnumerateSelfAndDescendants());

    /// <summary>
    /// The current draft re-serialised in the v2 contract shape, so the next turn's prompt can show
    /// the model exactly the tree it is being asked to revise. Resource names come from the bound
    /// candidates (real data), never from anything the model said.
    /// </summary>
    public string ToContractJson()
    {
        var payload = new
        {
            name = Name,
            nodes = Nodes.Select(DraftNodeDto.From),
        };

        return JsonSerializer.Serialize(payload, DraftJsonOptions);
    }

    private sealed record DraftNodeDto
    {
        [JsonPropertyName("nodeType")]
        public required string NodeType { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("candidateId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CandidateId { get; init; }

        [JsonPropertyName("platform")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Platform { get; init; }

        [JsonPropertyName("architecture")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Architecture { get; init; }

        [JsonPropertyName("confidence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Confidence { get; init; }

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; init; }

        [JsonPropertyName("children")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<DraftNodeDto>? Children { get; init; }

        public static DraftNodeDto From(PluginDraftNode node)
        {
            var dto = new DraftNodeDto
            {
                NodeType = node.NodeType,
                Name = node.Name,
                Children = node.Children.Count == 0 ? null : node.Children.Select(From).ToList(),
            };

            if (node is DraftResourceNode resource)
            {
                dto = dto with
                {
                    CandidateId = resource.CandidateId,
                    Platform = resource.Platform,
                    Architecture = resource.Architecture,
                    Confidence = resource.Confidence,
                    Reason = resource.Reason,
                };
            }

            return dto;
        }
    }

    /// <summary>Names stay readable; null fields disappear so the model sees the same shape it must return.</summary>
    private static readonly JsonSerializerOptions DraftJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// What survived a v2 parse: the draft tree, dropped-but-survived field issues, and nodes refused
/// outright (with the reason). A node is refused when it would point at something other than real
/// data — a fabricated id, a type mismatch, a forbidden download fact, a duplicate, or a malformed
/// subtree beyond the depth/node walls.
/// </summary>
public sealed record PluginTreeDraftValidation(
    PluginDraftTree? Draft,
    IReadOnlyList<PluginDraftIssue> Issues,
    IReadOnlyList<PluginDraftIssue> Rejected)
{
    public bool HasNodes => Draft is { Nodes.Count: > 0 };

    public static PluginTreeDraftValidation Fail(string subject, string reason)
        => new(null, Array.Empty<PluginDraftIssue>(), new[] { new PluginDraftIssue(subject, reason) });
}
