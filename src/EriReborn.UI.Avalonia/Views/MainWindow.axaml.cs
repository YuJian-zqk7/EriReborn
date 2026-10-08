using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Platform.Abstractions;
using EriReborn.Skin;
using WindowStateKind = global::Avalonia.Controls.WindowState;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>
/// Hosts the shell and implements the window operations the platform service
/// delegates to (spec 53). It also re-applies skin resources on every switch.
/// </summary>
public partial class MainWindow : Window, IWindowShellHost
{
    private IWindowService? _windowService;
    private PointerPressedEventArgs? _lastPress;

    public MainWindow()
    {
        InitializeComponent();
        ApplyBrandIcon();
        Opened += OnOpened;

        // The title bar reflects the real window state rather than assuming it.
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty && DataContext is MainViewModel shell)
            {
                shell.WindowMaximized = WindowState == WindowStateKind.Maximized;
                ApplyWindowGlyphs();
            }
        };
    }

    /// <summary>The maximise button shows the restore glyph once the window is maximised.</summary>
    private void ApplyWindowGlyphs()
    {
        if (this.FindControl<ShapePath>("MaximizeGlyph") is { } glyph)
        {
            // Restore is two overlapping rectangles, maximise one. Drawn here rather than fetched
            // from the library for the same reason the other two buttons are: library art cannot
            // take the skin's foreground colour, so on a dark skin it never showed up.
            glyph.Data = StreamGeometry.Parse(WindowState == WindowStateKind.Maximized
                ? "M 3,1 L 11,1 L 11,9 L 3,9 Z M 1,3 L 9,3 L 9,11 L 1,11 Z"
                : "M 1,1 L 11,1 L 11,11 L 1,11 Z");
        }
    }

    /// <summary>The window mark comes from the asset manifest, not a loose file (spec 48).</summary>
    private void ApplyBrandIcon()
    {
        var path = App.Host?.Assets.ResolvePath(SkinArtwork.BrandLogoId);
        if (path is null)
        {
            return;
        }

        try
        {
            Icon = new WindowIcon(path);
        }
        catch (Exception)
        {
            App.Host?.Log.Warn("asset.icon", $"Brand icon could not be loaded: {path}");
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (App.Host is null)
        {
            return;
        }

        _windowService = App.Host.Platform.Windows;
        _windowService.Attach(this);

        // Skin application and SkinChanged wiring live in App so the Android
        // host gets the same behaviour; here we only report startup and the
        // window rules the manifest declares.
        ApplyWindowMetrics(App.Host.Skins.Active);
        FitToWorkingArea();

        if (App.Main is { } shell)
        {
            shell.SkinChanged += (_, manifest) =>
            {
                ApplyWindowMetrics(manifest);
                FitToWorkingArea();
            };

            shell.ReportStartup();
        }
    }

    /// <summary>Window size rules come from the skin manifest (spec 40/53).</summary>
    private void ApplyWindowMetrics(SkinManifest? skin)
    {
        if (skin is null)
        {
            return;
        }

        MinWidth = skin.Window.TryGetValue("minwidth", out var w) && double.TryParse(w, out var minWidth)
            ? minWidth
            : MinWidth;
        MinHeight = skin.Window.TryGetValue("minheight", out var h) && double.TryParse(h, out var minHeight)
            ? minHeight
            : MinHeight;

        // The skin decides the chrome: a borderless skin wants the custom title
        // bar it draws, a system skin wants the platform's own frame (spec 114).
        SystemDecorations = string.Equals(
            skin.Window.TryGetValue("chrome", out var chrome) ? chrome : "system",
            "borderless",
            StringComparison.OrdinalIgnoreCase)
            ? SystemDecorations.BorderOnly
            : SystemDecorations.Full;
    }

    /// <summary>
    /// Keeps the window inside the screen's working area.
    ///
    /// The declared size is in device-independent pixels, so on a 200% display a
    /// 1200x780 window asks for 2400x1560 real pixels. That is larger than the
    /// working area of a 2560x1600 screen, which puts the right and bottom edges —
    /// the status bar, the last controls — off-screen where nothing can reach them.
    /// </summary>
    private void FitToWorkingArea()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1d : screen.Scaling;
        var availableWidth = screen.WorkingArea.Width / scaling;
        var availableHeight = screen.WorkingArea.Height / scaling;

        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        // The minimum is lowered to what the screen can actually show, and that is the part
        // that matters. A skin declares minwidth 960, which is 1200 real pixels at 125%
        // scaling — wider than a 1080-pixel display. A floor the screen cannot satisfy is not
        // a floor, it is a window bigger than the display: the platform re-applies the larger
        // minimum, the window is centred, and both edges lose about 60 pixels. Focusing the
        // content then hides right-aligned buttons and cuts the right-hand column, which reads
        // as "the text is not fully shown" rather than as "the window is too wide".
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);

        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);

        // Shrinking alone is not enough: the platform had already placed the window
        // where the larger size fitted, so the bottom edge stayed off-screen. The
        // window is only moved when it does not fit — a window the user placed
        // themselves is left exactly where they put it.
        var work = screen.WorkingArea;
        var pixelWidth = (int)Math.Round(Width * scaling);
        var pixelHeight = (int)Math.Round(Height * scaling);

        var fits = Position.X >= work.X
            && Position.Y >= work.Y
            && Position.X + pixelWidth <= work.X + work.Width
            && Position.Y + pixelHeight <= work.Y + work.Height;

        if (!fits)
        {
            Position = new PixelPoint(
                work.X + Math.Max(0, (work.Width - pixelWidth) / 2),
                work.Y + Math.Max(0, (work.Height - pixelHeight) / 2));
        }
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        _lastPress = e;

        // A double click on a title bar maximises or restores; that is standard
        // enough that its absence reads as broken (spec 114).
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    public void Minimize() => this.WindowState = WindowStateKind.Minimized;

    public void ToggleMaximize()
        => this.WindowState = this.WindowState == WindowStateKind.Maximized
            ? WindowStateKind.Normal
            : WindowStateKind.Maximized;

    public void Restore() => this.WindowState = WindowStateKind.Normal;

    public new void Close() => base.Close();

    private bool _closeConfirmed;

    /// <summary>
    /// Asks before leaving. Quitting is the one action in the app with no way back, and the title bar's
    /// close button sits a few pixels from the maximise button, so it is confirmed rather than obeyed on
    /// the first click (spec 56).
    /// </summary>
    protected override void OnClosing(global::Avalonia.Controls.WindowClosingEventArgs e)
    {
        if (_closeConfirmed || DataContext is not EriReborn.App.Shared.ViewModels.MainViewModel model)
        {
            // This is the path a shutdown takes — and also the path any unexpected close takes, silently. The
            // log says which one it was, because "Application exited normally" only means the main window
            // closed and says nothing about who closed it (spec 58/69).
            App.Host?.Log.Info(
                "shell.closing",
                $"Main window closing (confirmed={_closeConfirmed}, context={DataContext?.GetType().Name ?? "none"}).");

            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _ = ConfirmThenCloseAsync(model);
        base.OnClosing(e);
    }

    private async System.Threading.Tasks.Task ConfirmThenCloseAsync(
        EriReborn.App.Shared.ViewModels.MainViewModel model)
    {
        if (!await model.AskAsync(
                "退出 EriReborn？",
                "正在进行的下载会停下来；没有保存的改动会留在原样。",
                "退出",
                "继续使用"))
        {
            return;
        }

        _closeConfirmed = true;
        App.Host?.Log.Info("shell.closing", "The user confirmed exiting; closing the main window.");
        Close();
    }

    public void SetTitle(string title) => Title = title;

    /// <summary>Drag can only start from a real pointer event, so the last one is reused.</summary>
    public void BeginDragMove()
    {
        if (_lastPress is not null)
        {
            BeginMoveDrag(_lastPress);
        }
    }
}
