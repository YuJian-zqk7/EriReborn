using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Layout;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>One component the palette offers, in the user's words rather than the type's.</summary>
public sealed record LayoutPaletteItem(LayoutNodeType Type, string Name);

/// <summary>One shape a node can wear: the word that is stored, and the word that is read.</summary>
public sealed record LayoutShapeChoice(string Value, string Name);

/// <summary>
/// The visual layout editor (spec 38/39). Users drag, resize, show/hide, retype
/// and retext elements; the document is schema JSON that can be saved, imported
/// and exported. XAML is never edited by hand.
/// </summary>
public sealed partial class LayoutEditorViewModel : ObservableObject
{
    /// <summary>How many edits can be undone before the oldest is dropped.</summary>
    private const int HistoryLimit = 100;

    private readonly AppHost _host;
    private readonly List<HistoryEntry> _undo = new();
    private readonly List<HistoryEntry> _redo = new();

    /// <summary>The label of the coalescing run currently open, if any.</summary>
    private string? _activeRunLabel;

    private LayoutDocument _document = LayoutDefaults.Overview();

    public LayoutEditorViewModel(AppHost host)
    {
        _host = host;
        foreach (var type in Enum.GetValues<LayoutNodeType>())
        {
            if (type != LayoutNodeType.Page)
            {
                Palette.Add(new LayoutPaletteItem(type, DisplayNameOf(type)));
            }
        }

        DocumentPath = Path.Combine(host.Paths.UserDataDirectory, "layouts", "home.layout.json");
        Rebuild();
        UpdateHistoryState();
    }

    /// <summary>
    /// The word a user reads for a component. The palette used to show the enum name — "Container",
    /// "Stack", "Toggle" — which is the API talking, not the interface (spec 58).
    /// </summary>
    private static string DisplayNameOf(LayoutNodeType type) => type switch
    {
        LayoutNodeType.Container => "容器",
        LayoutNodeType.Stack => "竖排容器",
        LayoutNodeType.Grid => "网格",
        LayoutNodeType.Text => "文字",
        LayoutNodeType.Button => "按钮",
        LayoutNodeType.Image => "图片",
        LayoutNodeType.List => "列表",
        LayoutNodeType.Input => "输入框",
        LayoutNodeType.Progress => "进度条",
        LayoutNodeType.Toggle => "开关",
        LayoutNodeType.Page => "页面",
        _ => type.ToString(),
    };

    /// <summary>Raised whenever the document changes, so the view can re-render its preview.</summary>
    public event EventHandler<LayoutDocument>? DocumentChanged;

    public ObservableCollection<LayoutEditorItem> CanvasItems { get; } = new();

    public ObservableCollection<LayoutPaletteItem> Palette { get; } = new();

    /// <summary>
    /// The shapes a node may wear. Stored as a word rather than a number so the document says what it
    /// means, and so a node written before shapes existed simply has none.
    /// </summary>
    public IReadOnlyList<LayoutShapeChoice> ShapeChoices { get; } = new[]
    {
        new LayoutShapeChoice("rect", "直角"),
        new LayoutShapeChoice("round", "圆角"),
        new LayoutShapeChoice("circle", "圆形"),
    };

    public ObservableCollection<string> ValidationMessages { get; } = new();

    public LayoutDocument Document => _document;

    [ObservableProperty]
    private LayoutEditorItem? _selectedItem;

    [ObservableProperty]
    private string _status = "拖动元素即可移动；被容器（Stack / Grid）排列的元素一拖就会脱离容器。右下角控制柄调整尺寸。";

    [ObservableProperty]
    private string _documentPath = string.Empty;

    [ObservableProperty]
    private string _documentText = string.Empty;

    [ObservableProperty]
    private int _nodeCount;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _canRedo;

    [ObservableProperty]
    private string _historySummary = "可撤销 0 步";

    [ObservableProperty]
    private double _canvasWidth = 940;

    [ObservableProperty]
    private double _canvasHeight = 640;

    partial void OnSelectedItemChanged(LayoutEditorItem? oldValue, LayoutEditorItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(SelectedX));
        OnPropertyChanged(nameof(SelectedY));
        OnPropertyChanged(nameof(SelectedWidth));
        OnPropertyChanged(nameof(SelectedHeight));
        OnPropertyChanged(nameof(SelectedFontSize));
        OnPropertyChanged(nameof(SelectedColor));
        OnPropertyChanged(nameof(SelectedVisible));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedLink));
        OnPropertyChanged(nameof(SelectedImageSource));
        OnPropertyChanged(nameof(SelectedShapeChoice));
    }

    public bool HasSelection => SelectedItem is not null;

    public string? SelectedText
    {
        get => SelectedItem?.Node.Text;
        set => Apply(SelectedItem, n => n with { Text = value }, "编辑文本");
    }

    public double SelectedX
    {
        get => SelectedItem?.Node.X ?? 0;
        set => Apply(SelectedItem, n => n with { X = Math.Max(0, value) }, "修改 X");
    }

    public double SelectedY
    {
        get => SelectedItem?.Node.Y ?? 0;
        set => Apply(SelectedItem, n => n with { Y = Math.Max(0, value) }, "修改 Y");
    }

    public double SelectedWidth
    {
        get => SelectedItem?.Node.Width ?? 0;
        set => Apply(SelectedItem, n => n with { Width = Math.Max(LayoutNode.MinSize, value) }, "修改宽度");
    }

    public double SelectedHeight
    {
        get => SelectedItem?.Node.Height ?? 0;
        set => Apply(SelectedItem, n => n with { Height = Math.Max(LayoutNode.MinSize, value) }, "修改高度");
    }

    public double? SelectedFontSize
    {
        get => SelectedItem?.Node.FontSize;
        set => Apply(SelectedItem, n => n with { FontSize = value }, "修改字号");
    }

    public string? SelectedColor
    {
        get => SelectedItem?.Node.Color;
        set => Apply(SelectedItem, n => n with { Color = value }, "修改颜色");
    }

    public bool SelectedVisible
    {
        get => SelectedItem?.Node.Visible ?? false;
        set => Apply(SelectedItem, n => n with { Visible = value }, "显示/隐藏");
    }

    /// <summary>
    /// The selected node's name. An imported document brings machine-made names, and the name is what
    /// every other panel and the document itself refer to, so it has to be editable (and refused when it
    /// would collide).
    /// </summary>
    public string? SelectedName
    {
        get => SelectedItem?.Node.Id;
        set => Rename(value);
    }

    /// <summary>The page this document describes, as written in the document.</summary>
    public string? PageName
    {
        get => _document.Page;
        set => SetPageName(value);
    }

    /// <summary>Where clicking the element should take the user, when it is somewhere.</summary>
    public string? SelectedLink
    {
        get => Property("link");
        set => WriteProperty("link", value);
    }

    /// <summary>The picture the node draws, when it is an image.</summary>
    public string? SelectedImageSource
    {
        get => Property("src");
        set => WriteProperty("src", value);
    }

    /// <summary>The node's shape, as one of <see cref="ShapeChoices"/>.</summary>
    public LayoutShapeChoice? SelectedShapeChoice
    {
        get
        {
            var stored = Property("shape");
            return ShapeChoices.FirstOrDefault(choice => string.Equals(choice.Value, stored, StringComparison.Ordinal));
        }

        set
        {
            // Null means "back to the default shape", which is stored as absent rather than as a word.
            WriteProperty("shape", value?.Value);
        }
    }

    private string? Property(string key)
        => SelectedItem?.Node.Properties.TryGetValue(key, out var value) == true ? value : null;

    private void WriteProperty(string key, string? value)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        // Property boxes fire per keystroke, so these runs are coalesced.
        SetDocument(LayoutEditor.SetProperty(_document, item.Id, key, value), item.Id, $"修改 {key}", coalesce: true);
    }

    private void Rename(string? newName)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var wanted = newName?.Trim();
        if (string.IsNullOrEmpty(wanted) || string.Equals(wanted, item.Id, StringComparison.Ordinal))
        {
            return;
        }

        var updated = LayoutEditor.Rename(_document, item.Id, wanted);
        if (ReferenceEquals(updated, _document))
        {
            Status = $"「{wanted}」已经被另一个元素用了，换一个名字。";
            OnPropertyChanged(nameof(SelectedName));
            return;
        }

        SetDocument(updated, wanted, $"重命名 {item.Id}");
        Status = $"已把 {item.Id} 改名为 {wanted}。";
    }

    private void SetPageName(string? page)
    {
        var wanted = page?.Trim();
        if (string.IsNullOrEmpty(wanted) || string.Equals(wanted, _document.Page, StringComparison.Ordinal))
        {
            return;
        }

        SetDocument(_document with { Page = wanted }, SelectedItem?.Id, $"页面名改为 {wanted}", coalesce: true);
    }

    public void Select(LayoutEditorItem? item) => SelectedItem = item;

    private void Apply(LayoutEditorItem? item, Func<LayoutNode, LayoutNode> update, string label)
    {
        if (item is null)
        {
            return;
        }

        // Property boxes fire per keystroke, so these runs are coalesced.
        SetDocument(LayoutEditor.Update(_document, item.Id, update), item.Id, label, coalesce: true);
    }

    /// <summary>Commits a completed drag: the visual offset becomes model geometry.</summary>
    public void CommitMove(LayoutEditorItem item)
    {
        // A click selects; only a real pointer move may edit the document. Without
        // this, clicking a container-arranged element would yank it out of its card.
        if (!item.HasMovedSinceCommit)
        {
            return;
        }

        if (item.IsManaged)
        {
            // Dragging an element a Stack or Grid arranges used to be refused, which
            // read as "this editor does not drag". The drag is a deliberate gesture, so
            // it now detaches the element and drops it exactly where the pointer left
            // it — what a design tool does when you pull a child out of an auto-layout
            // frame. One undoable step, like every other edit.
            var targetX = Math.Max(0, item.AbsX);
            var targetY = Math.Max(0, item.AbsY);
            var detached = LayoutEditor.MoveToRoot(_document, item.Id);
            var placed = LayoutEditor.Update(detached, item.Id, node => node with { X = targetX, Y = targetY });

            SetDocument(placed, item.Id, "移出容器并移动");
            Status = $"已把 {item.Id} 从容器中移出，放到 ({targetX:0}, {targetY:0})。";
            return;
        }

        var dx = item.AbsX - item.Node.X;
        var dy = item.AbsY - item.Node.Y;
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            item.Refresh(item.Node, item.Node.X, item.Node.Y);
            return;
        }

        SetDocument(LayoutEditor.Move(_document, item.Id, dx, dy), item.Id, "移动");
        Status = $"已移动 {item.Id} → ({LayoutEditor.Find(_document, item.Id)!.X:0}, {LayoutEditor.Find(_document, item.Id)!.Y:0})";
    }

    /// <summary>
    /// Writes the edited page size into the document as one undoable step.
    ///
    /// <para>
    /// The sliders only move the editing canvas until this runs. That is why it is
    /// a command: a slider that silently resized the document would add an undo
    /// entry for every pixel the pointer crossed.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void ApplyCanvasSize()
    {
        var page = LayoutEditor.Flatten(_document).FirstOrDefault(node => node.Type == LayoutNodeType.Page);
        if (page is null)
        {
            Status = "当前文档没有页面节点，无法设置尺寸。";
            return;
        }

        var width = Math.Max(320, Math.Round(CanvasWidth));
        var height = Math.Max(240, Math.Round(CanvasHeight));

        if (Math.Abs(page.Width - width) < 0.5 && Math.Abs(page.Height - height) < 0.5)
        {
            Status = $"页面尺寸没有变化（{width:0}×{height:0}）。";
            return;
        }

        SetDocument(
            LayoutEditor.Update(_document, page.Id, node => node with { Width = width, Height = height }),
            page.Id,
            "页面尺寸");

        Status = $"页面尺寸 → {width:0}×{height:0}";
    }

    /// <summary>Commits a completed resize.</summary>
    public void CommitResize(LayoutEditorItem item)
    {
        if (item.IsManaged)
        {
            Rebuild(item.Id);
            Status = $"{item.Id} 的尺寸由容器决定，无法直接调整。";
            return;
        }

        var dw = item.Width - item.Node.Width;
        var dh = item.Height - item.Node.Height;
        if (Math.Abs(dw) < 0.01 && Math.Abs(dh) < 0.01)
        {
            item.Refresh(item.Node, item.AbsX, item.AbsY);
            return;
        }

        SetDocument(LayoutEditor.Resize(_document, item.Id, dw, dh), item.Id, "调整尺寸");
        var node = LayoutEditor.Find(_document, item.Id)!;
        Status = $"已调整 {item.Id} → {node.Width:0}×{node.Height:0}";
    }

    [RelayCommand]
    private void AddNode(LayoutNodeType type)
    {
        var id = LayoutEditor.SuggestId(_document, type);
        var node = LayoutNode.CreateDefault(id, type) with { X = 24, Y = 24 };
        SetDocument(LayoutEditor.Add(_document, null, node), id, $"添加 {type}");
        Status = $"已添加 {type}（{id}）";
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var id = SelectedItem.Id;
        SetDocument(LayoutEditor.Remove(_document, id), null, $"删除 {id}");
        Status = $"已删除 {id}";
    }

    /// <summary>
    /// Puts the selected node inside a Stack or Grid, which is the only way to
    /// reach flow arrangement — nothing else could nest a node, so the container
    /// types had no observable behaviour.
    /// </summary>
    [RelayCommand]
    private void MoveIntoContainer()
    {
        if (SelectedItem is null)
        {
            Status = "请先选择一个元素。";
            return;
        }

        var id = SelectedItem.Id;
        var container = LayoutEditor
            .Flatten(_document)
            .FirstOrDefault(n => n.Type is LayoutNodeType.Stack or LayoutNodeType.Grid
                && !string.Equals(n.Id, id, StringComparison.Ordinal)

                // Its own ancestor would detach the subtree. Its own descendant is the
                // same impossible move seen from the other end — and picking one meant
                // "move into container" silently did nothing once the default document
                // started nesting contents inside their cards.
                && !LayoutEditor.IsAncestorOf(_document, n.Id, id)
                && !LayoutEditor.IsAncestorOf(_document, id, n.Id));

        if (container is null)
        {
            Status = LayoutEditor.Flatten(_document).Any(n => n.Type is LayoutNodeType.Stack or LayoutNodeType.Grid)
                ? "该元素已经在容器里，或没有可用的容器。"
                : "布局中没有 Stack 或 Grid 容器；请先从「组件」中添加一个。";
            return;
        }

        var updated = LayoutEditor.Reparent(_document, id, container.Id);
        if (ReferenceEquals(updated, _document))
        {
            Status = $"无法把 {id} 移入 {container.Id}。";
            return;
        }

        SetDocument(updated, id, $"移入 {container.Id}");
        Status = $"已把 {id} 移入 {container.Id}（{container.Type}），它现在由容器排列。";
    }

    /// <summary>Moves the selected node back out so it can be positioned freely.</summary>
    [RelayCommand]
    private void MoveOutOfContainer()
    {
        if (SelectedItem is null)
        {
            Status = "请先选择一个元素。";
            return;
        }

        var id = SelectedItem.Id;
        if (!LayoutFlow.IsManagedByParent(_document, id))
        {
            Status = $"{id} 已经在顶层，可以直接拖动。";
            return;
        }

        SetDocument(LayoutEditor.MoveToRoot(_document, id), id, "移出容器");
        Status = $"已把 {id} 移出容器。";
    }

    [RelayCommand]
    private void ToggleSelectedVisibility()
    {
        if (SelectedItem is null)
        {
            return;
        }

        SetDocument(
            LayoutEditor.SetVisible(_document, SelectedItem.Id, !SelectedItem.Node.Visible),
            SelectedItem.Id,
            "显示/隐藏");
    }

    [RelayCommand]
    private void ResetDocument()
    {
        // Reachable by mistake, so reset is undoable rather than destructive.
        SetDocument(LayoutDefaults.Overview(), null, "重置为默认布局");
        Status = "已重置为默认布局（可撤销）。";
    }

    [RelayCommand]
    private void SaveDocument()
    {
        try
        {
            LayoutSerializer.Save(_document, DocumentPath);
            Status = $"已保存到 {DocumentPath}";
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void LoadDocument()
    {
        try
        {
            if (!File.Exists(DocumentPath))
            {
                Status = $"没有已保存的布局：{DocumentPath}";
                return;
            }

            SetDocument(LayoutSerializer.Load(DocumentPath), null, "载入布局文件");
            Status = $"已从 {DocumentPath} 载入。";
        }
        catch (Exception ex)
        {
            Status = $"载入失败：{ex.Message}";
        }
    }

    /// <summary>Export/import is the same JSON, staged through a text box so it can be shared.</summary>
    [RelayCommand]
    private void ExportDocument()
    {
        DocumentText = LayoutSerializer.Serialize(_document);
        Status = "已导出为 JSON（可复制分享）。";
    }

    [RelayCommand]
    private void ImportDocument()
    {
        try
        {
            var document = LayoutSerializer.Deserialize(DocumentText);
            SetDocument(document, null, "导入 JSON");
            Status = "已从 JSON 导入。";
        }
        catch (Exception ex)
        {
            Status = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>
    /// The single commit point for every document change, and therefore the only
    /// place history has to be recorded.
    /// </summary>
    /// <param name="label">Names the operation in the undo message.</param>
    /// <param name="coalesce">
    /// True for continuous edits such as typing in a numeric box. Consecutive
    /// coalescing edits sharing a label collapse into one history entry, so
    /// undoing a spinner drag does not take thirty presses.
    /// </param>
    private void SetDocument(LayoutDocument document, string? selectedId, string label, bool coalesce = false)
    {
        // LayoutEditor returns the same instance for a no-op edit.
        if (ReferenceEquals(document, _document))
        {
            Rebuild(selectedId);
            return;
        }

        if (!coalesce || !string.Equals(_activeRunLabel, label, StringComparison.Ordinal))
        {
            PushUndo(label, selectedId);
            _activeRunLabel = coalesce ? label : null;
        }

        _redo.Clear();
        _document = document;
        Rebuild(selectedId);
        UpdateHistoryState();
    }

    private void PushUndo(string label, string? selectedId)
    {
        // The entry keeps the state as it was BEFORE the edit, so undo is a
        // restore rather than an inverse operation that has to be derived.
        _undo.Add(new HistoryEntry(label, _document, SelectedItem?.Id, selectedId));
        if (_undo.Count > HistoryLimit)
        {
            _undo.RemoveAt(0);
        }
    }

    /// <summary>Restores the state before the most recent edit.</summary>
    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0)
        {
            Status = "没有可撤销的操作。";
            return;
        }

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        // Redo needs to come back to where we are now, so the current state is
        // recorded on the way out.
        _redo.Add(entry with { Document = _document });

        _document = entry.Document;
        _activeRunLabel = null;
        Rebuild(entry.SelectedBefore ?? entry.SelectedAfter);
        Status = $"已撤销：{entry.Label}";
        UpdateHistoryState();
    }

    /// <summary>Reapplies an undone edit.</summary>
    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0)
        {
            Status = "没有可重做的操作。";
            return;
        }

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        _undo.Add(entry with { Document = _document });

        _document = entry.Document;
        _activeRunLabel = null;
        Rebuild(entry.SelectedAfter ?? entry.SelectedBefore);
        Status = $"已重做：{entry.Label}";
        UpdateHistoryState();
    }

    /// <summary>How many edits can still be undone.</summary>
    public int UndoDepth => _undo.Count;

    /// <summary>How many undone edits can still be redone.</summary>
    public int RedoDepth => _redo.Count;

    private void UpdateHistoryState()
    {
        OnPropertyChanged(nameof(UndoDepth));
        OnPropertyChanged(nameof(RedoDepth));

        CanUndo = _undo.Count > 0;
        CanRedo = _redo.Count > 0;
        HistorySummary = _undo.Count == 0 && _redo.Count == 0
            ? "可撤销 0 步"
            : $"可撤销 {_undo.Count} 步，可重做 {_redo.Count} 步";
    }

    public sealed record HistoryEntry(
        string Label,
        LayoutDocument Document,
        string? SelectedBefore,
        string? SelectedAfter);

    private void Rebuild(string? selectedId = null)
    {
        var previous = selectedId ?? SelectedItem?.Id;

        CanvasItems.Clear();

        // The Page node frames the canvas rather than sitting on it as a draggable
        // box, which would swallow every drag into empty space.
        var page = _document.Nodes.FirstOrDefault(n => n.Type == LayoutNodeType.Page);
        var layout = LayoutFlow.Arrange(_document);

        // Every node, not just the top level: children of a Stack or Grid are
        // drawn where their parent arranged them.
        foreach (var node in LayoutEditor.Flatten(_document).Where(n => n.Type != LayoutNodeType.Page))
        {
            var rect = layout.TryGetValue(node.Id, out var computed)
                ? computed
                : new LayoutRect(node.X, node.Y, node.Width, node.Height);

            CanvasItems.Add(new LayoutEditorItem(
                node,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                LayoutFlow.IsManagedByParent(_document, node.Id)));
        }

        NodeCount = CanvasItems.Count;
        CanvasWidth = Math.Max(320, page?.Width ?? 940);
        CanvasHeight = Math.Max(240, page?.Height ?? 640);

        ValidationMessages.Clear();
        var validation = LayoutValidator.Validate(_document);
        if (!validation.IsValid)
        {
            foreach (var issue in validation.Issues)
            {
                ValidationMessages.Add(issue.ToString());
            }
        }

        SelectedItem = previous is null ? null : CanvasItems.FirstOrDefault(i => i.Id == previous);
        DocumentChanged?.Invoke(this, _document);
    }
}
