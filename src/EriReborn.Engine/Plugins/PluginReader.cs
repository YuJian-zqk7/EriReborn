using System.Text.Json;
using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Validation;

namespace EriReborn.Engine.Plugins;

/// <summary>
/// Parses a resource plugin and validates its shape.
///
/// Parsing and validating are separate stages on purpose: an import reports
/// which stage failed, so a malformed file is never reported as a missing
/// signature or the other way round (spec 47).
/// </summary>
public static class PluginReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static (ResourcePlugin? Plugin, IReadOnlyList<ValidationIssue> Issues) Parse(string json)
    {
        var issues = new List<ValidationIssue>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return (null, new[] { new ValidationIssue("plugin.invalid_json", ex.Message) });
        }

        using (document)
        {
            var root = document.RootElement;

            var schema = root.TryGetProperty("schema", out var schemaElement) && schemaElement.TryGetInt32(out var value)
                ? value
                : 1; // 缺省 schema 字段的旧文件按 schema 1 处理

            if (schema > ResourcePlugin.CurrentSchema)
            {
                return (null, new[]
                {
                    new ValidationIssue(
                        "plugin.schema_too_new",
                        $"插件 schema {schema} 高于本构建支持的 {ResourcePlugin.CurrentSchema}。",
                        schema.ToString()),
                });
            }

            // 形状优先：有 nodes 数组就按 schema v2 节点树读取；否则按 schema 1 的扁平
            // resources 读取并迁移为根级 file/folder 节点。
            var hasNodes = root.TryGetProperty("nodes", out var nodesElement)
                && nodesElement.ValueKind == JsonValueKind.Array;

            IReadOnlyList<PluginNode> rootNodes;

            if (hasNodes)
            {
                var autoGroupSeq = 0;
                rootNodes = ReadNodes(nodesElement, depth: 1, ref autoGroupSeq, issues);

                // 节点结构完整性（重复 id / 超深 / 重复挂载 / 空名 / 资源无 source 等）。
                issues.AddRange(PluginTree.Validate(rootNodes));
            }
            else
            {
                if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
                {
                    return (null, new[] { new ValidationIssue("plugin.no_resources", "插件缺少 resources 或 nodes 数组。") });
                }

                var flatResources = ReadResources(resources, issues);
                rootNodes = PluginTree.FromFlatResources(flatResources);
            }

            var plugin = new ResourcePlugin
            {
                Schema = schema,
                Id = Str(root, "id") ?? string.Empty,
                Name = Str(root, "name") ?? string.Empty,
                Version = Str(root, "version") ?? string.Empty,
                Author = Str(root, "author"),
                Description = Str(root, "description"),
                Trust = PluginTrust.Unknown,

                // "trust" in the file is ignored on purpose: a plugin must not be
                // able to declare itself official (spec 45).
                Nodes = rootNodes,
            };

            return (plugin, issues);
        }
    }

    // ----------------------------------------------------------- schema v2 节点树

    private static List<PluginNode> ReadNodes(JsonElement array, int depth, ref int autoGroupSeq, List<ValidationIssue> issues)
    {
        var nodes = new List<PluginNode>();

        foreach (var element in array.EnumerateArray())
        {
            var node = ReadNode(element, depth, ref autoGroupSeq, issues);
            if (node is not null)
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }

    private static PluginNode? ReadNode(JsonElement element, int depth, ref int autoGroupSeq, List<ValidationIssue> issues)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ValidationIssue("plugin.node_not_object", $"第 {depth} 层存在不是对象的节点，已跳过。"));
            return null;
        }

        var nodeType = Str(element, "nodeType")?.Trim().ToLowerInvariant();
        var children = element.TryGetProperty("children", out var childrenElement) && childrenElement.ValueKind == JsonValueKind.Array
            ? ReadNodes(childrenElement, depth + 1, ref autoGroupSeq, issues)
            : new List<PluginNode>();

        switch (nodeType)
        {
            case "group":
            {
                var groupId = Str(element, "id");
                var groupName = Str(element, "name") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(groupId))
                {
                    // 分组没有稳定 id 时给一个读取期兜底 id（仅保证本次树可用），并明示问题。
                    groupId = "g_auto_" + Interlocked.Increment(ref autoGroupSeq);
                    issues.Add(new ValidationIssue("plugin.node_no_id", $"分组 '{groupName}' 缺少 id，已临时分配。", groupId));
                }

                // 分组只负责组织：携带任何资源字段都被忽略而不是偷偷当成可下载资源。
                if (element.TryGetProperty("resource", out _)
                    || element.TryGetProperty("sources", out _)
                    || element.TryGetProperty("locator", out _))
                {
                    issues.Add(new ValidationIssue(
                        "plugin.group_has_resource_fields",
                        $"分组 '{groupName}' 不允许携带 resource/sources/locator，这些字段已被忽略。",
                        groupId));
                }

                return PluginGroupNode.Create(groupId, groupName, children);
            }

            case "file":
            case "folder":
            {
                if (!element.TryGetProperty("resource", out var resourceElement) || resourceElement.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new ValidationIssue(
                        "plugin.node_missing_resource",
                        $"{nodeType} 节点缺少 resource 对象，已跳过。",
                        Str(element, "nodeType")));
                    return null;
                }

                var resource = ReadResourceElement(resourceElement, issues, index: null);
                if (resource is null)
                {
                    return null;
                }

                var nodeKind = nodeType == "folder" ? PluginNodeKind.Folder : PluginNodeKind.File;

                // 文件夹/文件节点的 CloudShare locator 类型一律以节点类型为准：
                // 节点声明 folder 而 locator 写成 file 时修正为 folder，而不是让下载时按文件解析失败。
                var alignedSources = AlignLocatorKinds(resource.Sources, nodeKind, resource.Id, issues);
                resource = resource with { Sources = alignedSources };

                return PluginResourceNode.Create(nodeKind, resource, children);
            }

            default:
                issues.Add(new ValidationIssue(
                    "plugin.node_unknown_type",
                    $"未知节点类型 '{nodeType ?? "(空)"}'，已跳过该节点及其子树。",
                    nodeType));
                return null;
        }
    }

    /// <summary>把资源节点内 CloudShare source 的 locator.Kind 对齐为节点类型，冲突记 issue。</summary>
    private static IReadOnlyList<SoftwareSource> AlignLocatorKinds(
        IReadOnlyList<SoftwareSource> sources,
        PluginNodeKind nodeKind,
        string subject,
        List<ValidationIssue> issues)
    {
        var expected = nodeKind == PluginNodeKind.Folder ? ResourceLocatorKind.Folder : ResourceLocatorKind.File;
        var aligned = new List<SoftwareSource>(sources.Count);

        foreach (var source in sources)
        {
            if (source.Kind == SourceKind.CloudShare
                && source.Locator is { } locator
                && locator.Kind != expected)
            {
                issues.Add(new ValidationIssue(
                    "plugin.node_locator_kind_mismatch",
                    $"资源 '{subject}' 是{(expected == ResourceLocatorKind.Folder ? "文件夹" : "文件")}节点，"
                    + $"但其 locator 声明为 {locator.Kind.ToString().ToLowerInvariant()}，已以节点类型为准修正。",
                    subject));
                aligned.Add(source with { Locator = locator with { Kind = expected } });
            }
            else
            {
                aligned.Add(source);
            }
        }

        return aligned;
    }

    private static List<PluginResource> ReadResources(JsonElement array, List<ValidationIssue> issues)
    {
        var resources = new List<PluginResource>();
        var index = 0;

        foreach (var element in array.EnumerateArray())
        {
            var resource = ReadResourceElement(element, issues, index);
            if (resource is not null)
            {
                resources.Add(resource);
            }

            index++;
        }

        return resources;
    }

    /// <summary>读取一个资源对象（schema 1 数组元素与 schema 2 节点的 resource 载荷共用）。</summary>
    private static PluginResource? ReadResourceElement(JsonElement element, List<ValidationIssue> issues, int? index)
    {
        var id = Str(element, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            issues.Add(new ValidationIssue(
                "plugin.resource_no_id",
                index.HasValue ? $"第 {index} 个资源缺少 id。" : "节点资源缺少 id。",
                index?.ToString()));
            return null;
        }

        var categoryId = Str(element, "categoryId") ?? string.Empty;
        var directory = Str(element, "directory") ?? string.Empty;

        // A plugin may point at a registered official category or invent its own. The id
        // is turned into a folder name on disk, so the rule that has to hold is that it
        // is a legal one. Inventing an id gains no standing: the taxonomy — not the
        // plugin — decides what counts as official, and it does not know this id.
        var categoryResult = DirectoryNameValidator.ValidateName(categoryId);
        if (!categoryResult.IsValid)
        {
            issues.AddRange(categoryResult.Issues.Select(i => i with { Subject = id }));
        }

        var directoryResult = DirectoryNameValidator.ValidateName(directory);
        if (!directoryResult.IsValid)
        {
            // Never silently slugified: the plugin author has to fix it.
            issues.AddRange(directoryResult.Issues.Select(i => i with { Subject = id }));
        }

        return new PluginResource
        {
            Id = id,
            Name = Str(element, "name") ?? id,
            CategoryId = categoryId,
            SubcategoryId = Str(element, "subcategoryId"),
            DirectoryName = directory,
            Version = Str(element, "version"),
            Description = Str(element, "description"),
            Homepage = Str(element, "homepage"),
            Sha256 = Str(element, "sha256"),
            Architecture = Str(element, "architecture"),
            Tier = ParseTier(Str(element, "tier")),
            Mode = ParseMode(Str(element, "mode")),
            Sources = ReadSources(element),
        };
    }

    private static IReadOnlyList<SoftwareSource> ReadSources(JsonElement element)
    {
        if (!element.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SoftwareSource>();
        }

        var list = new List<SoftwareSource>();

        foreach (var source in sources.EnumerateArray())
        {
            var kindText = Str(source, "kind") ?? "CloudShare";
            if (!Enum.TryParse<SourceKind>(kindText, ignoreCase: true, out var kind))
            {
                // An unknown kind is skipped rather than guessed at.
                continue;
            }

            list.Add(new SoftwareSource
            {
                Kind = kind,
                // The plugin format says "provider"; the catalog format says "providerId" for the same
                // thing. Accepting both keeps a source copied from a catalog file from silently losing
                // its platform and failing later as "unknown provider (空)".
                ProviderId = Str(source, "provider") ?? Str(source, "providerId"),
                Url = Str(source, "url"),
                ShareUrl = Str(source, "shareUrl"),
                FileName = Str(source, "fileName"),
                // Without the winget id a Winget source is only half an address: the
                // installer has nothing to pass to the tool, so the source can never
                // resolve. Read it whenever it is declared.
                WingetId = Str(source, "wingetId"),
                // Read as well as written: a source that says its location is still unknown must keep
                // saying so after a load/save, or the entry looks complete and fails at download time.
                NeedsLocatorResolution = source.TryGetProperty("needsLocatorResolution", out var needs)
                    && needs.ValueKind == JsonValueKind.True,
                Sha256 = Str(source, "sha256"),
                Architecture = Str(source, "architecture"),
                Version = Str(source, "version"),
                Locator = ResourceLocatorParser.Parse(source),
            });
        }

        return list;
    }

    private static SoftwareTier ParseTier(string? text) => text?.ToLowerInvariant() switch
    {
        "core" => SoftwareTier.Core,
        "recommended" => SoftwareTier.Recommended,
        _ => SoftwareTier.Optional,
    };

    private static InstallationMode ParseMode(string? text) => text?.ToLowerInvariant() switch
    {
        "portable" => InstallationMode.Portable,
        "manual" => InstallationMode.Manual,
        _ => InstallationMode.Install,
    };

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
