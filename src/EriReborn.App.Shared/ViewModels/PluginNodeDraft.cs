using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EriReborn.Core.Domain;
using EriReborn.Engine.Plugins;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// 插件资源树（schema v2）在制作器里的一个草稿节点。
///
/// <para>
/// 分组节点只有组织结构，没有资源载荷；file/folder 节点持有一条
/// <see cref="PluginResourceDraft"/>。身份只用稳定的 <see cref="NodeId"/>，
/// 显示名永远不作为去重键。
/// </para>
/// </summary>
public sealed partial class PluginNodeDraft : ObservableObject
{
    [ObservableProperty]
    private string _nodeId;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroup))]
    [NotifyPropertyChangedFor(nameof(IsFile))]
    [NotifyPropertyChangedFor(nameof(IsFolder))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(KindText))]
    private PluginNodeKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResource))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private PluginResourceDraft? _resource;

    [ObservableProperty]
    private bool _isExpanded = true;

    public PluginNodeDraft(
        string nodeId,
        string name,
        PluginNodeKind kind,
        PluginResourceDraft? resource,
        IEnumerable<PluginNodeDraft>? children = null)
    {
        _nodeId = nodeId;
        _name = name;
        _kind = kind;
        _resource = resource;

        if (children is not null)
        {
            foreach (var child in children)
            {
                Children.Add(child);
            }
        }
    }

    /// <summary>有序子节点；分组和文件夹节点都可以挂子节点。</summary>
    public ObservableCollection<PluginNodeDraft> Children { get; } = new();

    public bool IsGroup => Kind == PluginNodeKind.Group;

    public bool IsFile => Kind == PluginNodeKind.File;

    public bool IsFolder => Kind == PluginNodeKind.Folder;

    /// <summary>只有 file/folder 节点能读进资源表单编辑；分组没有资源载荷。</summary>
    public bool CanEdit => Kind != PluginNodeKind.Group;

    public bool HasResource => Resource is not null;

    public string KindText => Kind switch
    {
        PluginNodeKind.Group => "分组",
        PluginNodeKind.Folder => "文件夹",
        _ => "文件",
    };

    /// <summary>资源树里一行需要看到的概要：分组只显示名字，资源节点沿用资源行的概要。</summary>
    public string Summary => Kind == PluginNodeKind.Group
        ? $"{Name}（分组）"
        : Resource?.Summary ?? Name;

    /// <summary>前序遍历本节点及其全部后代（保持父子顺序）。</summary>
    public IEnumerable<PluginNodeDraft> EnumerateSelfAndDescendants()
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

    /// <summary>按 NodeId 在本子树内查找（包含自身）。</summary>
    public PluginNodeDraft? FindNode(string nodeId)
        => EnumerateSelfAndDescendants()
            .FirstOrDefault(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal));

    /// <summary>本子树深度：叶子为 1。</summary>
    public int SubtreeDepth() => 1 + (Children.Count == 0 ? 0 : Children.Max(c => c.SubtreeDepth()));

    /// <summary>把草稿递归转成不可变领域节点。</summary>
    public PluginNode ToModel()
    {
        var children = Children.Select(child => child.ToModel()).ToArray();

        if (Kind == PluginNodeKind.Group)
        {
            return PluginGroupNode.Create(NodeId, Name, children);
        }

        if (Resource is null)
        {
            // 表单不会造出这种节点；防御性兜底，避免写出无法导入的文档。
            throw new InvalidOperationException($"资源节点 '{NodeId}' 缺少资源载荷。");
        }

        return PluginResourceNode.Create(Kind, Resource.ToResource(), children);
    }

    /// <summary>
    /// 从读回的领域节点建立草稿。资源节点没有任何 source 时（旧脏数据）被丢弃但保留其分组结构，
    /// 返回 null 表示该节点不应进入草稿树。
    /// </summary>
    public static PluginNodeDraft? FromModel(PluginNode node)
    {
        if (node is PluginGroupNode)
        {
            var group = new PluginNodeDraft(node.NodeId, node.Name, PluginNodeKind.Group, null);
            foreach (var childNode in node.Children)
            {
                if (FromModel(childNode) is { } child)
                {
                    group.Children.Add(child);
                }
            }

            return group;
        }

        var resourceNode = (PluginResourceNode)node;
        var resource = resourceNode.Resource;
        if (resource.Sources.Count == 0)
        {
            return null;
        }

        var draft = new PluginNodeDraft(
            resource.Id,
            resource.Name,
            node.Kind,
            new PluginResourceDraft(
                resource.Id,
                resource.Name,
                resource.CategoryId,
                resource.DirectoryName,
                resource.Version ?? string.Empty,
                resource.Tier,
                resource.Mode,
                resource.Sources[0],
                resource.Sources.Skip(1).ToList()));

        foreach (var childNode in node.Children)
        {
            if (FromModel(childNode) is { } child)
            {
                draft.Children.Add(child);
            }
        }

        return draft;
    }

    /// <summary>
    /// 按草稿资源的 locator 判定节点种类：任一 CloudShare source 指向文件夹即为 Folder，
    /// 与 <see cref="PluginTree.ClassifyResourceKind"/> 的口径一致。
    /// </summary>
    public static PluginNodeKind KindOf(PluginResourceDraft draft)
        => draft.AllSources.Any(source =>
               source.Kind == SourceKind.CloudShare
               && source.Locator is { Kind: ResourceLocatorKind.Folder })
            ? PluginNodeKind.Folder
            : PluginNodeKind.File;
}
