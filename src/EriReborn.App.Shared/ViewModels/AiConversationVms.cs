using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EriReborn.Engine.Plugins;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>多轮 AI 面板里一条消息是谁发的。系统消息用于状态/错误，不是模型输出。</summary>
public enum AiChatAuthor
{
    User,
    Assistant,
    System,
}

/// <summary>
/// AI 对话面板中的一条消息。助手消息在模型没给出合法 JSON 时也要留下一句说明，
/// 被拒绝/被忽略的内容写进文本里，保证用户看得到。
/// </summary>
public sealed partial class AiChatMessage : ObservableObject
{
    public AiChatMessage(AiChatAuthor author, string text)
    {
        Author = author;
        _text = text;
        At = DateTime.Now;
    }

    public AiChatAuthor Author { get; }

    [ObservableProperty]
    private string _text;

    public DateTime At { get; }

    public string AuthorText => Author switch
    {
        AiChatAuthor.User => "我",
        AiChatAuthor.Assistant => "AI 助手",
        _ => "系统",
    };

    public bool IsUser => Author == AiChatAuthor.User;

    public bool IsAssistant => Author == AiChatAuthor.Assistant;

    public bool IsSystem => Author == AiChatAuthor.System;
}

/// <summary>
/// 实时草稿树里的一行：由校验通过的 <see cref="PluginDraftNode"/> 转换。
///
/// <para>
/// 每行都可以勾选；应用时只把勾选的子树写进插件树。分组行只有名字，资源行带着真实候选
/// （candidateId、路径）和模型给出的平台/架构判断——下载身份永远取自候选，不取模型的话。
/// </para>
/// </summary>
public sealed partial class AiDraftNodeVm : ObservableObject
{
    public AiDraftNodeVm(PluginDraftNode node)
    {
        Node = node;

        foreach (var child in node.Children)
        {
            var row = new AiDraftNodeVm(child) { Parent = this };
            Children.Add(row);
        }
    }

    /// <summary>
    /// 「改为分组 / 取消分组」微调时用已有子行重建（保留勾选状态），只换节点载荷与父子关系。
    /// </summary>
    internal AiDraftNodeVm(PluginDraftNode node, IReadOnlyList<AiDraftNodeVm> children)
    {
        Node = node;

        foreach (var child in children)
        {
            child.SetParent(this);
            Children.Add(child);
        }
    }

    /// <summary>对应的不可变草稿节点；应用时从它（及其 Candidate）取真实 locator。</summary>
    public PluginDraftNode Node { get; }

    internal void SetParent(AiDraftNodeVm? parent) => Parent = parent;

    public AiDraftNodeVm? Parent { get; private set; }

    public ObservableCollection<AiDraftNodeVm> Children { get; } = new();

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private bool _isExpanded = true;

    public string Name => Node.Name;

    public string KindText => Node switch
    {
        DraftGroupNode => "分组",
        DraftResourceNode resource => resource.IsFolder ? "文件夹" : "文件",
        _ => Node.NodeType,
    };

    public bool IsGroup => Node.IsGroup;

    public bool IsResource => !Node.IsGroup;

    /// <summary>资源行才显示候选 id；分组行没有。</summary>
    public string? CandidateId => (Node as DraftResourceNode)?.Candidate.CandidateId;

    /// <summary>资源在真实分享里的路径，方便用户核对模型有没有挂错位置。</summary>
    public string? CandidatePath => (Node as DraftResourceNode)?.Candidate.Path;

    /// <summary>模型给的判断（平台/架构/把握/理由）；它不参与下载身份，仅作展示。</summary>
    public string Detail
    {
        get
        {
            if (Node is not DraftResourceNode resource)
            {
                return Children.Count == 0 ? string.Empty : $"{Children.Count} 个子节点";
            }

            var parts = new List<string> { resource.Candidate.Path };
            parts.Add($"模型判断：{resource.Platform ?? "Unknown"} / {resource.Architecture ?? "Unknown"}");

            if (resource.Confidence is { } confidence)
            {
                parts.Add($"把握 {confidence:0.00}");
            }

            if (!string.IsNullOrWhiteSpace(resource.Reason))
            {
                parts.Add(resource.Reason);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>前序遍历本行及全部后代。</summary>
    public IEnumerable<AiDraftNodeVm> EnumerateSelfAndDescendants()
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
