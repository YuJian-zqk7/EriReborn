using CommunityToolkit.Mvvm.ComponentModel;
using EriReborn.Layout;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// One draggable element on the editor canvas. The node itself stays immutable;
/// this wrapper carries the live position while a drag is in progress and is
/// committed back to the document on release.
/// </summary>
public sealed partial class LayoutEditorItem : ObservableObject
{
    public LayoutEditorItem(
        LayoutNode node,
        double absoluteX,
        double absoluteY,
        double? width = null,
        double? height = null,
        bool isManaged = false)
    {
        Node = node;
        _absX = absoluteX;
        _absY = absoluteY;
        _width = width ?? node.Width;
        _height = height ?? node.Height;
        _isVisible = node.Visible;
        _isManaged = isManaged;
    }

    /// <summary>
    /// True when a Stack or Grid parent arranges this node. Dragging it would be
    /// undone by the next layout pass, so the editor refuses instead of letting
    /// the user watch it snap back.
    /// </summary>
    [ObservableProperty]
    private bool _isManaged;

    public LayoutNode Node { get; private set; }

    public string Id => Node.Id;

    public LayoutNodeType Type => Node.Type;

    public string Title => string.IsNullOrWhiteSpace(Node.Text) ? Node.Id : Node.Text!;

    public string Subtitle => $"{Node.Type} · {Node.Id}";

    [ObservableProperty]
    private double _absX;

    [ObservableProperty]
    private double _absY;

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    private double _height;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// True once the pointer has actually moved this element since the last commit.
    ///
    /// <para>
    /// The commit path has to tell a drag from a click: merely clicking a
    /// container-arranged element must select it, not pull it out of its container.
    /// Only a real move may edit the document.
    /// </para>
    /// </summary>
    public bool HasMovedSinceCommit { get; private set; }

    /// <summary>Refreshes from the model after a committed edit.</summary>
    public void Refresh(LayoutNode node, double absoluteX, double absoluteY, double? width = null, double? height = null)
    {
        Node = node;
        AbsX = absoluteX;
        AbsY = absoluteY;
        Width = width ?? node.Width;
        Height = height ?? node.Height;
        IsVisible = node.Visible;
        HasMovedSinceCommit = false;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }

    public void NudgeVisual(double dx, double dy)
    {
        AbsX = Math.Max(0, AbsX + dx);
        AbsY = Math.Max(0, AbsY + dy);
        HasMovedSinceCommit = true;
    }

    public void ResizeVisual(double dw, double dh)
    {
        Width = Math.Max(LayoutNode.MinSize, Width + dw);
        Height = Math.Max(LayoutNode.MinSize, Height + dh);
    }
}
