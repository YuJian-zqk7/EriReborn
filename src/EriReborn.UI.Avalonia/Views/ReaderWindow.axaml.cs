using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Extension.Reader;
using EriReborn.UI.Avalonia.Controls;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>
/// The reader in a window of its own. Reading is a room of its own the same way the
/// skin editor is: the shell's page column is too narrow for a book, and a book does
/// not want the shell's chrome around it.
///
/// <para>
/// The window holds no reading logic — commands route through
/// <see cref="ReaderWindowViewModel"/> and its session. What lives here is the
/// frameless chrome, the reading themes, the page keys, and the bridge between the
/// view model and the HTML reading surface: pages are pushed down as JSON, and the
/// page's selection/key messages are turned back into view model commands.
/// </para>
/// </summary>
public partial class ReaderWindow : Window
{
    /// <summary>The reading surfaces, per theme. Bar colors feed the in-page selection toolbar.
    /// Mark/MarkNote are highlights; Search is the jumped-to hit accent.</summary>
    private static readonly IReadOnlyDictionary<string, ReaderThemeColors> Themes =
        new Dictionary<string, ReaderThemeColors>
        {
            ["paper"] = new("#F7F2E7", "#EFE7D6", "#3B3128", "#FFE08A", "#FFD1A3", "#A8D3F5", "#3B3128", "#F7F2E7"),
            ["eye"] = new("#CCE8CF", "#BFDDBF", "#243324", "#FFE08A", "#FFD1A3", "#A8D3F5", "#243324", "#F2F8F2"),
            ["night"] = new("#1E2126", "#262A31", "#C8CCD2", "#5C4A16", "#5C3A16", "#2C4A6E", "#0E1013", "#C8CCD2"),
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 正文是整段中文，不转义成 \uXXXX，推给页面的脚本也短一大截。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private ReaderWindowViewModel? _viewModel;

    private IReaderWebView? _web;

    private bool _pageReady;

    private string? _pendingPageScript;

    public ReaderWindow()
    {
        InitializeComponent();

        // Frameless like the shell, so the window carries its own title bar and its
        // close button never depends on system chrome the skin may switch off.
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = 40;

        InstallReaderSurface();
        ApplyTheme(ReaderSettings.Default.Theme);
        KeyDown += OnKeyDown;
        DataContextChanged += (_, _) => HookViewModel();
    }

    /// <summary>
    /// Creates the browser kernel surface through the platform slot and places it
    /// straight into the reading area. With no kernel installed (non-Windows builds)
    /// a plain fallback note is shown instead.
    /// </summary>
    private void InstallReaderSurface()
    {
        var control = BrowserSlot.CreateReader();
        if (control is null)
        {
            ReaderFallback.IsVisible = true;
            return;
        }

        ReaderHost.Children.Add(control);
        if (control is IReaderWebView web)
        {
            _web = web;
            web.Ready += (_, _) => Dispatcher.UIThread.Post(OnPageReady);
            web.WebMessage += (_, message) => Dispatcher.UIThread.Post(() => OnPageMessage(message));
        }
        else
        {
            ReaderFallback.IsVisible = true;
        }
    }

    private void HookViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as ReaderWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyTheme(_viewModel.Theme);
            PushSettings();
            PushPage();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ReaderWindowViewModel.Theme):
                ApplyTheme(_viewModel.Theme);
                PushSettings();
                break;
            case nameof(ReaderWindowViewModel.FontSize):
            case nameof(ReaderWindowViewModel.LineHeight):
                PushSettings();
                break;
            case nameof(ReaderWindowViewModel.CurrentPage):
            case nameof(ReaderWindowViewModel.SearchEmphasisStart):
            case nameof(ReaderWindowViewModel.SearchEmphasisEnd):
                PushPage();
                break;
        }
    }

    private void ApplyTheme(string theme)
    {
        if (!Themes.TryGetValue(theme, out var colors))
        {
            colors = Themes["paper"];
        }

        Background = Brush(colors.Back);
        TitleBar.Background = Brush(colors.Surface);
        TitleText.Foreground = Brush(colors.Fore);
        ToolbarBar.Background = Brush(colors.Surface);
        StatusBar.Background = Brush(colors.Surface);
        ReaderArea.Background = Brush(colors.Surface);
        ReaderFallback.Foreground = Brush(colors.Fore);
        ShelfDrawer.Background = Brush(colors.Surface);
        TocDrawer.Background = Brush(colors.Surface);
        BookmarksDrawer.Background = Brush(colors.Surface);
        HighlightsDrawer.Background = Brush(colors.Surface);
        SearchDrawer.Background = Brush(colors.Surface);
    }

    private void OnPageReady()
    {
        _pageReady = true;
        BrowserLog.Write("OnPageReady: _pageReady=true, vm=" + (_viewModel is null ? "null" : "set") + ", pending=" + (_pendingPageScript is not null ? "yes" : "no"));
        PushSettings();
        if (_pendingPageScript is not null)
        {
            var script = _pendingPageScript;
            _pendingPageScript = null;
            BrowserLog.Write("OnPageReady: executing pending script, len=" + script.Length);
            _ = _web?.EvaluateAsync(script);
        }
    }

    /// <summary>Pushes typography and theme colors. Pure presentation; the page never picks its own theme.</summary>
    private void PushSettings()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!Themes.TryGetValue(_viewModel.Theme, out var colors))
        {
            colors = Themes["paper"];
        }

        var payload = new
        {
            bg = colors.Surface,
            fg = colors.Fore,
            mark = colors.Mark,
            markNote = colors.MarkNote,
            find = colors.Search,
            barBg = colors.BarBg,
            barFg = colors.BarFg,
            fontSize = _viewModel.FontSize,
            lineHeight = _viewModel.LineHeight,
        };

        var script = "window.applyReaderSettings && window.applyReaderSettings(" + JsonSerializer.Serialize(payload, JsonOptions) + ");";
        if (_pageReady)
        {
            _ = _web?.EvaluateAsync(script);
        }
    }

    /// <summary>
    /// Serializes the current page into the payload the HTML surface renders: text,
    /// highlight ranges in page coordinates, and the optional search-hit emphasis.
    /// </summary>
    private void PushPage()
    {
        if (_viewModel is null)
        {
            BrowserLog.Write("pushPage: _viewModel is null, skip");
            return;
        }

        var page = _viewModel.CurrentPage;
        BrowserLog.Write("pushPage: pageReady=" + _pageReady + " page=" + (page is null ? "null" : ("len=" + page.Text.Length)));
        object payload;
        if (page is null)
        {
            payload = new { text = string.Empty, ranges = Array.Empty<object[]>(), emphasis = (int[]?)null, emptyText = "打开一本书开始阅读。" };
        }
        else
        {
            var ranges = page.HighlightsOnThisPage
                .Select(span => new object[] { span.Start, span.End, span.Note is null ? string.Empty : "note", span.Id })
                .ToArray();

            int[]? emphasis = _viewModel.SearchEmphasisStart is { } s && _viewModel.SearchEmphasisEnd is { } en && en > s
                ? new[] { s, en }
                : null;

            payload = new { page.Text, ranges, emphasis, emptyText = string.Empty };
        }

        _pendingPageScript = "window.renderPage(" + JsonSerializer.Serialize(payload, JsonOptions) + ");";
        if (_pageReady)
        {
            var script = _pendingPageScript;
            _pendingPageScript = null;
            BrowserLog.Write("pushPage: executing script, len=" + script.Length);
            _ = _web?.EvaluateAsync(script).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    BrowserLog.Write("pushPage: EvaluateAsync faulted: " + t.Exception?.Message);
                else
                    BrowserLog.Write("pushPage: EvaluateAsync returned: " + (t.Result ?? "null"));
            }, TaskScheduler.Default);
        }
        else
        {
            BrowserLog.Write("pushPage: page not ready, saved as pending");
        }
    }

    /// <summary>Turns the page's JSON messages into view model actions on the UI thread.</summary>
    private void OnPageMessage(string message)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp))
            {
                return;
            }

            switch (typeProp.GetString())
            {
                case "debug":
                    BrowserLog.Write("page: " + root.GetProperty("msg").GetString());
                    break;
                case "highlight":
                {
                    var start = root.GetProperty("start").GetInt32();
                    var end = root.GetProperty("end").GetInt32();
                    var note = root.TryGetProperty("note", out var noteProp) && noteProp.ValueKind == JsonValueKind.String
                        ? noteProp.GetString()
                        : null;
                    _ = _viewModel.AddHighlightFromSelectionAsync(start, end, string.IsNullOrWhiteSpace(note) ? null : note);
                    break;
                }
                case "removeHighlight":
                {
                    if (root.TryGetProperty("id", out var idProp) && idProp.GetString() is { Length: > 0 } id)
                    {
                        _ = _viewModel.RemoveHighlightCommand.ExecuteAsync(id);
                    }

                    break;
                }
                case "key":
                {
                    if (!root.TryGetProperty("key", out var keyProp))
                    {
                        break;
                    }

                    switch (keyProp.GetString())
                    {
                        case "next":
                            _ = _viewModel.NextPageCommand.ExecuteAsync(null);
                            break;
                        case "prev":
                            _ = _viewModel.PrevPageCommand.ExecuteAsync(null);
                            break;
                        case "search":
                            _viewModel.ShowSearchPanel = true;
                            Dispatcher.UIThread.Post(() => SearchBox.Focus());
                            break;
                        case "escape":
                            _viewModel.ShowSearchPanel = false;
                            break;
                    }

                    break;
                }
            }
        }
        catch (JsonException)
        {
            // A malformed page message must never take the window down.
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Page keys work when focus is still in Avalonia chrome (buttons, panels).
    /// Keys pressed inside the reading surface arrive as page messages instead.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.PageDown or Key.Right or Key.Space:
                _ = _viewModel.NextPageCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Key.PageUp or Key.Left:
                _ = _viewModel.PrevPageCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _viewModel.ShowSearchPanel = true;
                Dispatcher.UIThread.Post(() =>
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                });
                e.Handled = true;
                break;
            case Key.Escape when _viewModel.ShowSearchPanel:
                _viewModel.ShowSearchPanel = false;
                e.Handled = true;
                break;
        }
    }

    private static SolidColorBrush Brush(string hex)
        => new(Color.Parse(hex));

    /// <summary>One theme's surface colors, including the in-page selection toolbar.</summary>
    private sealed record ReaderThemeColors(
        string Back,
        string Surface,
        string Fore,
        string Mark,
        string MarkNote,
        string Search,
        string BarBg,
        string BarFg);
}
