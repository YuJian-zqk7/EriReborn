using EriReborn.App.Shared.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EriReborn.Platform.Abstractions;
using EriReborn.Skin;
using EriReborn.UI.Avalonia.Controls;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>
/// The application shell, shared by the desktop window and the Android single
/// view. Layout adapts to the active skin: sidebar on desktop, bottom
/// navigation on mobile (spec 8/42/55).
///
/// It also owns the one global pointer statement: while any job is running the
/// pointer says so, instead of a page quietly freezing (spec 10/112).
/// </summary>
public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            WatchJobs();
            WatchSafeArea();
        };
    }

    /// <summary>
    /// Keeps content out from under the status bar and the gesture bar.
    ///
    /// <para>
    /// A desktop window reserves nothing, so this is a no-op there — but it is the
    /// same code path, which is why the behaviour can be tested without a phone.
    /// </para>
    /// </summary>
    private void WatchSafeArea()
    {
        if (App.Host is not { } host)
        {
            return;
        }

        host.Platform.SafeArea.Changed += (_, insets) => Dispatcher.UIThread.Post(() => ApplySafeArea(insets));
        ApplySafeArea(host.Platform.SafeArea.Current);
    }

    private void ApplySafeArea(SafeAreaInsets insets)
    {
        // Sanitised before it reaches the layout: a negative padding is an error in
        // most layout systems, and an enormous one looks like a blank window.
        var safe = insets.Sanitized();

        Padding = safe.IsEmpty
            ? default
            : new Thickness(safe.Left, safe.Top, safe.Right, safe.Bottom);
    }

    private bool _cursorWatching;

    private void WatchJobs()
    {
        if (App.Host is not { } host)
        {
            return;
        }

        if (!_cursorWatching)
        {
            _cursorWatching = true;

            // The manager raises this from worker threads, so the pointer is only ever
            // changed on the UI thread. A skin change has to re-resolve it as well.
            host.Jobs.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyCursor);
            if (App.Main is { } shell)
            {
                shell.SkinChanged += (_, _) => Dispatcher.UIThread.Post(ApplyCursor);

            // A page that scrolls its own panes has to be told how tall its viewport is. Left
            // to itself inside a scrolling parent it is measured with unlimited room, so its
            // inner scrollers never scroll: the page looks right and hides its own lower half.
            PageScroller.LayoutUpdated += (_, _) => ApplyPageHostHeight();
            }
        }

        ApplyCursor();
    }

    private void ApplyCursor()
    {
        if (App.Host is not { } host)
        {
            return;
        }

        // The idle pointer is the skin's too. Setting null here is what made a skin's cursor
        // art invisible: the Default role was declared, shipped, and never resolved.
        Cursor = CursorManager.Resolve(
            host.Skins.Active,
            host.Jobs.RunningCount > 0 ? CursorRole.Busy : CursorRole.Default);
    }

    private double _pageHostHeight = double.NaN;

    /// <summary>
    /// Gives a pane-owning page an exact viewport height, and hands every other page back to
    /// Auto.
    ///
    /// The subtraction matters: the padding and the horizontal bar are not room the page may
    /// use, and a host even one pixel taller leaves the outer scroller something to scroll —
    /// which is precisely the "list and detail move together" this exists to prevent.
    /// </summary>
    private void ApplyPageHostHeight()
    {
        if (this.FindControl<ScrollViewer>("PageScroller") is not { } scroller ||
            this.FindControl<Panel>("PageHost") is not { } host)
        {
            return;
        }

        var available = scroller.Bounds.Height
            - scroller.Padding.Top
            - scroller.Padding.Bottom
            - 16;
        var model = DataContext as MainViewModel;

        // A page whose content can be wider than the window is laid out from its own natural width.
        // A stretched page reports the viewport as the widest it needs, so the horizontal extent
        // stops at the viewport while the page paints past it — the bar then reaches its end with
        // content still off-screen. Left alignment is what makes the extent cover the real width.
        var sideways = model is { PageScrollsSideways: true }
            ? global::Avalonia.Layout.HorizontalAlignment.Left
            : global::Avalonia.Layout.HorizontalAlignment.Stretch;

        if (scroller.HorizontalContentAlignment != sideways)
        {
            scroller.HorizontalContentAlignment = sideways;
        }

        var target = model is { PageFillsViewport: true }
            ? (available > 0 ? available : 0)
            : double.NaN;

        if (target.Equals(_pageHostHeight))
        {
            return;
        }

        _pageHostHeight = target;
        host.Height = target;
    }
}
