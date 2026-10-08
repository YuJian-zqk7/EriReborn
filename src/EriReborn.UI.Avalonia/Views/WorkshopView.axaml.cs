using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Layout;
using EriReborn.Skin;
using EriReborn.UI.Avalonia.Controls;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>
/// One page the skin editor can show, in its own words.
///
/// <para>
/// A page can have children — the software page has the categories the navigation lists under it — and
/// a child is the parent's page plus the slice it names. That is why the editor's page list is a tree
/// rather than a flat picker: clicking a parent shows the whole page, clicking a child shows the same
/// page filtered to that child.
/// </para>
/// </summary>
public sealed record WorkshopPreviewPage(string Key, string Name, string? Filter = null)
{
    /// <summary>The pages listed under this one, when it has any.</summary>
    public IReadOnlyList<WorkshopPreviewPage>? Children { get; init; }

    /// <summary>The page this one belongs to, with everything after the colon removed.</summary>
    public string BaseKey => Key.Split(':')[0];
}

/// <summary>
/// Hosts the skin editor and the visual layout editor.
///
/// <para>
/// The skin editor shows the real pages rather than a drawing of them, so "what you replace is what
/// you were looking at" holds; the layout editor keeps its own schema canvas and preview (spec
/// 38/39).
/// </para>
/// </summary>
public partial class WorkshopView : UserControl
{
    /// <summary>
    /// The pages worth editing a skin against: the ones a user actually lives in. A skin change
    /// repaints all of them, and the picker decides which one is on screen while editing.
    /// </summary>
    private static readonly WorkshopPreviewPage[] PreviewPages =
    {
        new("home", "概览"),
        new("software", "软件"),
        new("environment", "环境"),
        new("cloud", "来源"),
        new("ai", "AI 接口"),
        new("extensions", "扩展"),
        new("marketplace", "商城"),
        new("plugins", "插件"),
        new("settings", "设置"),
        new("jobs", "任务"),
        new("update", "更新公告"),
        new("blog", "博客"),
    };

    private LayoutEditorItem? _dragItem;
    private Point _lastPoint;
    private bool _resizing;

    public WorkshopView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Preview Mode: the press is taken on the way down, before the control it landed on sees it,
        // so clicking 「安装」 in the preview selects the button instead of installing anything. A
        // bubbled handler could only do this after the button had already acted.
        AddHandler(InputElement.PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);

        // App.Main may not exist yet when the view is built, so the page tree is loaded once the
        // control is in the tree.
        AttachedToVisualTree += (_, _) => LoadPreviewTree();
    }

    private void OnPreviewPageSelected(object? sender, SelectionChangedEventArgs e)
        => LoadPreviewPage(PreviewPageTree?.SelectedItem as WorkshopPreviewPage);

    /// <summary>
    /// Fills the page list, with each page's children from the real navigation.
    ///
    /// <para>
    /// The children are the navigation's own children, so a child the app can navigate to is a child the
    /// editor can preview, with its own words. Nothing is invented here: a page with no children simply
    /// has none.
    /// </para>
    /// </summary>
    private void LoadPreviewTree()
    {
        if (PreviewPageTree is null)
        {
            return;
        }

        var previous = PreviewPageTree.SelectedItem as WorkshopPreviewPage;
        var main = App.Main;
        var roots = new List<WorkshopPreviewPage>(PreviewPages.Length);

        foreach (var page in PreviewPages)
        {
            var nav = main?.NavigationItems.FirstOrDefault(
                item => string.Equals(item.Key, page.Key, StringComparison.OrdinalIgnoreCase));

            var children = nav?.Children is { Count: > 0 } kids
                ? kids.Select(child => new WorkshopPreviewPage(child.Key, child.Title, child.FilterValue)).ToList()
                : null;

            roots.Add(page with { Name = nav?.Title ?? page.Name, Children = children });
        }

        PreviewPageTree.ItemsSource = roots;

        var wanted = (previous is null ? null : FindPage(roots, previous.Key)) ?? roots[0];
        PreviewPageTree.SelectedItem = wanted;

        // Setting the selection may not raise the event when it lands on the value already there, and
        // the preview has to match whatever is selected either way.
        LoadPreviewPage(wanted);
    }

    private static WorkshopPreviewPage? FindPage(IReadOnlyList<WorkshopPreviewPage> pages, string key)
    {
        foreach (var page in pages)
        {
            if (string.Equals(page.Key, key, StringComparison.Ordinal))
            {
                return page;
            }

            if (page.Children is { Count: > 0 } children && FindPage(children, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Clicking a skin card enters edit mode and opens the editor window.
    ///
    /// <para>
    /// The command runs on the same click, so the window is opened once the page is already editing;
    /// otherwise a fresh window would open on the gallery and the user would have to pick the skin
    /// twice.
    /// </para>
    /// </summary>
    private void OnEditSkinInWindowClick(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() => OnOpenEditorWindowClick(sender, e), DispatcherPriority.Background);

    /// <summary>
    /// Opens the editor in a window of its own, which is the answer to an editor squeezed into the
    /// shell's page column: the same view over the same page, with the room an editor needs.
    ///
    /// <para>
    /// Only a desktop shell has windows. On a single-view shell (Android) there is nothing to open, and
    /// the button simply leaves the user where they are rather than pretending.
    /// </para>
    /// </summary>
    private void OnOpenEditorWindowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkshopViewModel workshop)
        {
            return;
        }

        // Nothing to open an editor on. A shipped pack is read-only and the command has already
        // refused to enter the editor, so opening a window here would only offer edits that cannot be
        // saved — the same reason the page itself stays on the gallery for it.
        if (!workshop.IsEditingSkin)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            workshop.SkinVerdict = "这个平台没有独立窗口，就用当前页面继续编辑。";
            return;
        }

        var window = new SkinEditorWindow { DataContext = workshop };
        window.Show(owner);
    }

    // The 布局 tool is a tool now: it swaps the editor's middle for the layout canvas, which the page
    // shows by itself. Whether that is on is the page's own business, so there is nothing to handle here.

    /// <summary>
    /// Puts the real page into the editor.
    ///
    /// <para>
    /// It is the real view over the real view model, so the preview <em>is</em> the interface: what
    /// the user replaces is what they were already looking at. Only the pages are hosted, never the
    /// shell — the shell contains this page, so hosting it would nest the editor inside itself.
    /// </para>
    /// </summary>
    private void LoadPreviewPage(WorkshopPreviewPage? page)
    {
        if (RealPreviewHost is null || PreviewNavRail is null || App.Main is not { } main)
        {
            return;
        }

        // The rail is built from the real navigation data: real icon ids, real skin words.
        PreviewNavRail.ItemsSource = main.NavigationItems;

        if (PreviewPageLabel is not null)
        {
            PreviewPageLabel.Text = page?.Name ?? string.Empty;
        }

        try
        {
            (Control View, object Model) target = page?.BaseKey switch
            {
                "software" => (new SoftwareView(), main.Software),
                "environment" => (new EnvironmentView(), main.Environment),
                "cloud" => (new CloudView(), main.Cloud),
                "ai" => (new AiView(), main.Ai),
                "extensions" => (new ExtensionsView(), main.Extensions),
                "marketplace" => (new MarketplaceView(), main.Marketplace),
                "plugins" => (new PluginsView(), main.Plugins),
                "settings" => (new SettingsView(), main.Settings),
                "jobs" => (new JobsView(), main.Jobs),
                "update" => (new UpdateView(), main.Update),
                "blog" => (new BlogView(), main.Blog),
                _ => (new HomeView(), main.Home),
            };

            target.View.DataContext = target.Model;
            RealPreviewHost.Content = target.View;

            // A child page is its parent's page seen through the child's own filter — that is what the
            // navigation's children mean, so the preview shows the same slice the app would.
            if (string.Equals(page?.BaseKey, "software", StringComparison.OrdinalIgnoreCase)
                && page?.Filter is { Length: > 0 } filter)
            {
                main.Software.FocusCategory(filter);
            }

            // A different page means the previous selection's element is gone.
            ClearSelectionOutline();
        }
        catch (Exception ex)
        {
            // A page that cannot be previewed must not take the editor down with it: the user is
            // here to change a skin, and the message says what happened.
            App.Host?.Log.Warn("workshop.preview", $"预览页面 '{page?.Key}' 失败：{ex.Message}");
            RealPreviewHost.Content = new TextBlock
            {
                Text = $"这个页面现在预览不了：{ex.Message}",
                TextWrapping = TextWrapping.Wrap,
            };
        }
    }

    /// <summary>
    /// Turns a click inside the preview into "edit this", and nothing else.
    ///
    /// <para>
    /// The clicked control says what it is: anything carrying <see cref="SkinElement.Id"/> names its
    /// own slot, an <see cref="AssetImage"/> knows the picture it draws, a <see cref="UiText"/>
    /// knows the word's key. Walking up from the pointer lets a click on an icon inside a button
    /// reach the icon rather than the button. Because this runs on the tunnel, marking the press
    /// handled is what keeps the preview a preview: no navigation, no install, no download happens
    /// while a skin is being edited.
    /// </para>
    /// </summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not WorkshopViewModel workshop || PreviewFrame is null)
        {
            return;
        }

        // Only presses inside the real preview belong to the editor; the tool buttons beside it keep
        // working, and so do the preview's own scrollbars. The frame covers the sidebar as well as the
        // page, so a click on a real navigation entry is an element selection too.
        var insidePreview = false;
        var onScrollBar = false;
        for (var node = e.Source as Visual; node is not null; node = node.GetVisualParent())
        {
            if (node is ScrollBar or Thumb)
            {
                onScrollBar = true;
            }

            if (ReferenceEquals(node, PreviewFrame))
            {
                insidePreview = true;
                break;
            }
        }

        if (!insidePreview || onScrollBar)
        {
            return;
        }

        e.Handled = true;

        SkinElementDescriptor? declared = null;
        Control? declaredOwner = null;
        string? assetId = null;
        Control? assetOwner = null;
        string? textKey = null;
        Control? textOwner = null;

        for (var node = e.Source as Visual;
             node is not null && !ReferenceEquals(node, PreviewFrame);
             node = node.GetVisualParent())
        {
            if (node is not Control control)
            {
                continue;
            }

            if (declared is null && SkinElement.Describe(control) is { } descriptor)
            {
                declared = descriptor;
                declaredOwner = control;
            }

            if (assetId is null && control is AssetImage { ResolvedAssetId: { Length: > 0 } resolved })
            {
                assetId = resolved;
                assetOwner = control;
            }

            if (textKey is null && control is UiText { Key: { Length: > 0 } key })
            {
                textKey = key;
                textOwner = control;
            }
        }

        if (declared is not null)
        {
            workshop.SelectElement(declared);
            ShowSelectionOutline(declaredOwner);
            return;
        }

        if (textKey is not null)
        {
            workshop.SelectElement(null, textKey);
            ShowSelectionOutline(textOwner);
            return;
        }

        if (assetId is not null)
        {
            workshop.SelectElement(assetId, null);
            ShowSelectionOutline(assetOwner);
            return;
        }

        // Empty space: nothing was picked, and saying so beats leaving the previous outline up.
        workshop.ClearSelection();
        ClearSelectionOutline();
    }

    private Control? _outlined;

    private void OnPreviewScrollChanged(object? sender, ScrollChangedEventArgs e) => PositionOutline();

    /// <summary>Draws the selection frame over the element that was picked.</summary>
    private void ShowSelectionOutline(Control? control)
    {
        _outlined = control;
        PositionOutline();
    }

    private void ClearSelectionOutline()
    {
        _outlined = null;
        if (SelectionOutline is not null)
        {
            SelectionOutline.IsVisible = false;
        }
    }

    /// <summary>
    /// Puts the frame where the element currently is. It is re-measured rather than remembered,
    /// because scrolling and a page change move the element without telling the editor.
    /// </summary>
    private void PositionOutline()
    {
        if (SelectionOutline is null || SelectionOverlay is null)
        {
            return;
        }

        if (_outlined is null || !_outlined.IsAttachedToVisualTree())
        {
            SelectionOutline.IsVisible = false;
            return;
        }

        var size = _outlined.Bounds.Size;
        if (_outlined.TranslatePoint(new Point(0, 0), SelectionOverlay) is not { } topLeft
            || size.Width <= 0
            || size.Height <= 0)
        {
            SelectionOutline.IsVisible = false;
            return;
        }

        Canvas.SetLeft(SelectionOutline, topLeft.X - 2);
        Canvas.SetTop(SelectionOutline, topLeft.Y - 2);
        SelectionOutline.Width = size.Width + 4;
        SelectionOutline.Height = size.Height + 4;
        SelectionOutline.IsVisible = true;
    }

    private LayoutEditorViewModel? Editor => (DataContext as WorkshopViewModel)?.LayoutEditor;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        var editor = Editor;
        if (editor is null)
        {
            return;
        }

        editor.DocumentChanged -= OnDocumentChanged;
        editor.DocumentChanged += OnDocumentChanged;
        RefreshPreview(editor.Document);
    }

    private void OnDocumentChanged(object? sender, LayoutDocument document) => RefreshPreview(document);

    private void RefreshPreview(LayoutDocument document)
    {
        if (PreviewHost is not null)
        {
            // The editor keeps the coloured node outlines: they are how a stack is
            // told from a grid while editing.
            PreviewHost.Content = LayoutRenderer.Render(document, LayoutBindings.Empty, editorGuides: true);
        }
    }

    private Control? _draggedControl;

    /// <summary>
    /// Hovering an element is what tells the user it can be dragged. The shape comes
    /// from the active skin via CursorManager; a literal shape in the view is banned
    /// (spec 112), and a frozen arrow would ignore the user's cursor theme.
    /// </summary>
    private void OnItemPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control control)
        {
            control.Cursor = CursorManager.Resolve(App.Host?.Skins.Active, CursorRole.Drag);
        }
    }

    private void OnItemPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control control)
        {
            control.Cursor = null;
        }
    }

    /// <summary>Hovering the grip is what tells the user it can be resized.</summary>
    private void OnGripPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control control)
        {
            control.Cursor = CursorManager.Resolve(App.Host?.Skins.Active, CursorRole.ResizeCorner);
        }
    }

    private void OnGripPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control control)
        {
            control.Cursor = null;
        }
    }

    /// <summary>Replacing the slot this row stands for, from the precise-picker list.</summary>
    private async void OnReplaceArtClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WorkshopAssetSlot slot } && DataContext is WorkshopViewModel workshop)
        {
            await PickArtForAsync(workshop, slot);
        }
    }

    /// <summary>
    /// Replacing the element picked in the real interface. The name comes from the page, not from
    /// this button: the detail panel is not part of any one element's row.
    /// </summary>
    private async void OnReplaceElementClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkshopViewModel { SelectedElement: { IsArt: true } element } workshop
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"给「{element.Name}」选一张图片",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" },
                    },
                },
            });

            if (files.Count == 0)
            {
                return;
            }

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                // Some sources hand over a stream and no path; saying so beats doing nothing.
                workshop.SkinVerdict = "这个来源给不出本地文件路径，换一张试试。";
                return;
            }

            workshop.ReplaceElementArt(path);
        }
        catch (Exception ex)
        {
            workshop.SkinVerdict = $"选图失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Picks the picture a layout node draws. The picker lives in the view because only a view has a
    /// TopLevel to ask; the document is changed through the page's own property, so the edit is one
    /// undoable step like every other one.
    /// </summary>
    private async void OnPickLayoutImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkshopViewModel { LayoutEditor: { HasSelection: true } editor }
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "给这个元素选一张图片",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" },
                    },
                },
            });

            if (files.Count == 0)
            {
                return;
            }

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                editor.Status = "这个来源给不出本地文件路径，换一张试试。";
                return;
            }

            editor.SelectedImageSource = path;
        }
        catch (Exception ex)
        {
            editor.Status = $"选图失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void OnRestoreArtClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WorkshopAssetSlot slot } && DataContext is WorkshopViewModel workshop)
        {
            workshop.RestoreSlotArt(slot.Slot);
        }
    }

    /// <summary>
    /// Asks for a picture and hands the path to the page.
    ///
    /// <para>
    /// The picker lives here because only a view has a TopLevel to ask. Everything after the choice
    /// — copying the file into the skin, declaring it, pointing the slot at it — belongs to the
    /// page's store, so this method deliberately does none of it.
    /// </para>
    /// </summary>
    private async Task PickArtForAsync(WorkshopViewModel workshop, WorkshopAssetSlot slot)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"给「{slot.Title}」选一张图片",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" },
                    },
                },
            });

            if (files.Count == 0)
            {
                return;
            }

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                // Some sources hand over a stream and no path; saying so beats doing nothing.
                workshop.SkinVerdict = "这个来源给不出本地文件路径，换一张试试。";
                return;
            }

            workshop.ReplaceSlotArt(slot.Slot, path);
        }
        catch (Exception ex)
        {
            workshop.SkinVerdict = $"选图失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Pointing one pointer role at a picture of the user's own. It lives in the view because choosing
    /// a file needs a top level; copying it into the skin and registering it is the view model's job,
    /// the same as for every other picture the editor replaces.
    /// </summary>
    private async void OnImportCursorArtClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WorkshopCursor row }
            || DataContext is not WorkshopViewModel workshop
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"给「{row.Title}」挑一张指针图片",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" },
                    },
                },
            });

            if (files.Count == 0)
            {
                return;
            }

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                workshop.SkinVerdict = "这个来源给不出本地文件路径，换一张试试。";
                return;
            }

            workshop.ImportCursorArt(row, path);
        }
        catch (Exception ex)
        {
            workshop.SkinVerdict = $"选图失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// The one way out of the layout tool. In the editor's own window this closes it and hands the
    /// user back to the shell; embedded in the shell there is no window of its own to close, so it
    /// returns to the picture tool rather than leaving a canvas they have to hunt their way out of.
    /// </summary>
    private void OnExitLayoutToolClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is SkinEditorWindow editor)
        {
            editor.Close();
            return;
        }

        if (DataContext is WorkshopViewModel workshop)
        {
            workshop.SelectPaletteCommand.Execute("图片");
        }
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not LayoutEditorItem item)
        {
            return;
        }

        var editor = Editor;
        if (editor is null)
        {
            return;
        }

        editor.Select(item);
        _dragItem = item;
        _resizing = e.Source is Control source && source.Name == "ResizeGrip";
        _lastPoint = e.GetPosition(this);

        // The pointer says which gesture is in progress; both roles come from the
        // active skin rather than a hard-coded arrow (spec 112).
        _draggedControl = control;
        control.Cursor = CursorManager.Resolve(
            App.Host?.Skins.Active,
            _resizing ? CursorRole.ResizeCorner : CursorRole.Drag);

        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null)
        {
            return;
        }

        var point = e.GetPosition(this);
        var dx = point.X - _lastPoint.X;
        var dy = point.Y - _lastPoint.Y;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
        {
            return;
        }

        if (_resizing)
        {
            _dragItem.ResizeVisual(dx, dy);
        }
        else
        {
            _dragItem.NudgeVisual(dx, dy);
        }

        _lastPoint = point;
        e.Handled = true;
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var item = _dragItem;
        var editor = Editor;
        _dragItem = null;

        if (_draggedControl is { } dragged)
        {
            dragged.Cursor = null;
        }

        _draggedControl = null;

        e.Pointer.Capture(null);

        if (item is null || editor is null)
        {
            return;
        }

        if (_resizing)
        {
            editor.CommitResize(item);
        }
        else
        {
            editor.CommitMove(item);
        }

        _resizing = false;
        e.Handled = true;
    }
}
