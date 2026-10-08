using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using EriReborn.Skin;
using EriReborn.UI.Avalonia.Controls;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>
/// The skin editor in a window of its own.
///
/// <para>
/// The editor used to live only inside the shell's page column, which shares its width with the
/// navigation rail: on a normal window the preview and the property panel were squeezed to the point of
/// being unusable. The window is the room the job needs — the same view over the same page, so a change
/// made here shows in the shell's copy and the other way round, because there is one editor and not two.
/// </para>
///
/// <para>
/// The window is built in code rather than in XAML because it holds exactly one thing: the editor. There
/// is no second layout to keep in step with it.
/// </para>
/// </summary>
public sealed class SkinEditorWindow : Window
{
    public SkinEditorWindow()
    {
        Title = "皮肤编辑器";
        Width = 1460;
        Height = 920;
        MinWidth = 980;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Frameless like the shell, so the editor carries its own title bar. Without this the window leaned
        // on the system chrome, which the active skin can switch off — leaving the editor with no way to
        // close it (spec: the editor window must always be closable).
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = 40;

        var host = new Border { Child = new WorkshopView() };

        // Painted from the active skin rather than from the window's default: the editor is a view of the
        // skin, so its own surfaces belong to the skin too. Assigned through the binding indexer, which is
        // how a dynamic resource is reached from code.
        host[!Border.BackgroundProperty] = new DynamicResourceExtension("SkinBackground");

        var closeButton = new Button
        {
            Width = 46,
            Height = 38,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Content = CreateCloseGlyph(),
        };
        ToolTip.SetTip(closeButton, "关闭编辑器");
        closeButton.Click += (_, _) => Close();

        var titleBar = new Border
        {
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = "皮肤编辑器",
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(14, 0, 0, 0),
                    },
                    closeButton,
                },
            },
        };
        titleBar[!Border.BackgroundProperty] = new DynamicResourceExtension("SkinSurface");
        titleBar.PointerPressed += (_, e) =>
        {
            // Don't start a drag when the press lands on the close button.
            if (closeButton.IsPointerOver)
            {
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("40,*"),
            Children =
            {
                titleBar,
                host,
            },
        };
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(host, 1);

        // The idle pointer is the skin's too. Without this the standalone editor uses the platform
        // arrow, which breaks the pointer the user is editing here.
        ApplyCursor();
    }

    /// <summary>Draws the close glyph rather than fetching it from the asset library: the library
    /// image is a dark stroke that vanishes on a dark skin's title bar (the same problem the main
    /// window had), and AssetImage cannot be tinted to follow SkinForeground.</summary>
    private static ShapePath CreateCloseGlyph()
    {
        var glyph = new ShapePath
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Data = StreamGeometry.Parse("M 0,0 L 12,12 M 12,0 L 0,12"),
            StrokeThickness = 1.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph[!ShapePath.StrokeProperty] = new DynamicResourceExtension("SkinForeground");
        return glyph;
    }

    /// <summary>Sets the window's default cursor from the active skin.</summary>
    private void ApplyCursor()
    {
        if (App.Host is not { } host)
        {
            return;
        }

        Cursor = CursorManager.Resolve(host.Skins.Active, CursorRole.Default);
    }
}
