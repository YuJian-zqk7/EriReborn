using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EriReborn.Core.Domain;

namespace EriReborn.Engine.Plugins;

/// <summary>
/// 把插件模型按 schema v2 写成 JSON：根级 <c>nodes</c> 数组递归输出
/// group / file / folder 节点，资源载荷与来源字段沿用 <see cref="PluginReader"/> 读取的名字。
///
/// 与读取器同处 Engine 层，使「写 → 读」往返可以在不碰 UI 的情况下被测。
/// </summary>
public static class PluginWriter
{
    /// <summary>序列化为带缩进、不转义中文的插件文档。</summary>
    public static string Write(ResourcePlugin plugin)
    {
        var nodes = new JsonArray();
        foreach (var node in plugin.Nodes)
        {
            nodes.Add(WriteNode(node));
        }

        var root = new JsonObject
        {
            ["schema"] = ResourcePlugin.CurrentSchema,
            ["id"] = plugin.Id,
            ["name"] = plugin.Name,
            ["version"] = plugin.Version,
            ["author"] = plugin.Author,
            ["description"] = plugin.Description,
            ["nodes"] = nodes,
        };

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,

            // 不转义 CJK：这个文件是给人读、给人手改的。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static JsonObject WriteNode(PluginNode node)
    {
        if (node is PluginGroupNode)
        {
            var group = new JsonObject
            {
                ["nodeType"] = "group",
                ["id"] = node.NodeId,
                ["name"] = node.Name,
            };

            WriteChildren(group, node.Children);
            return group;
        }

        var resourceNode = (PluginResourceNode)node;
        var result = new JsonObject
        {
            ["nodeType"] = node.Kind == PluginNodeKind.Folder ? "folder" : "file",
            ["resource"] = WriteResource(resourceNode.Resource),
        };

        WriteChildren(result, node.Children);
        return result;
    }

    private static void WriteChildren(JsonObject target, IReadOnlyList<PluginNode> children)
    {
        if (children.Count == 0)
        {
            return;
        }

        var array = new JsonArray();
        foreach (var child in children)
        {
            array.Add(WriteNode(child));
        }

        target["children"] = array;
    }

    private static JsonObject WriteResource(PluginResource resource)
    {
        var sources = new JsonArray();
        foreach (var source in resource.Sources)
        {
            sources.Add(WriteSource(source));
        }

        return new JsonObject
        {
            ["id"] = resource.Id,
            ["name"] = resource.Name,
            ["categoryId"] = resource.CategoryId,
            ["subcategoryId"] = resource.SubcategoryId,
            ["directory"] = resource.DirectoryName,
            ["version"] = resource.Version,
            ["description"] = resource.Description,
            ["homepage"] = resource.Homepage,
            ["sha256"] = resource.Sha256,
            ["architecture"] = resource.Architecture,
            ["tier"] = resource.Tier.ToString().ToLowerInvariant(),
            ["mode"] = resource.Mode.ToString().ToLowerInvariant(),
            ["sources"] = sources,
        };
    }

    private static JsonObject WriteSource(SoftwareSource source) => new()
    {
        ["kind"] = source.Kind.ToString(),
        ["provider"] = source.ProviderId,
        ["url"] = source.Url,
        ["shareUrl"] = source.ShareUrl,

        // winget 来源的地址就是包 id，不能漏写（官方插件经编辑器往返时丢 wingetId 的旧坑）。
        ["wingetId"] = source.WingetId,
        ["fileName"] = source.FileName,
        ["sha256"] = source.Sha256,
        ["architecture"] = source.Architecture,
        ["sizeBytes"] = source.SizeBytes,
        ["version"] = source.Version,
        ["needsLocatorResolution"] = source.NeedsLocatorResolution ? true : null,
        ["locator"] = source.Locator is { } locator
            ? new JsonObject
            {
                ["type"] = locator.Kind == ResourceLocatorKind.Folder ? "folder" : "file",
                ["path"] = locator.Path,
                ["name"] = locator.Name,
                ["providerItemId"] = locator.ProviderItemId,
            }
            : null,
    };
}
