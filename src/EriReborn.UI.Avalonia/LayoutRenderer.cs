using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EriReborn.Layout;
using EriReborn.Skin;
using EriReborn.UI.Avalonia.Controls;

namespace EriReborn.UI.Avalonia;

/// <summary>
/// Turns a schema layout into live Avalonia controls. This is what makes the
/// layout "real": the same document the editor manipulates is the thing being
/// rendered, so the preview is never a mock-up (spec 39).
///
/// Geometry is absolute within the parent's coordinate space, which keeps drag,
/// move and resize unambiguous.
/// </summary>
public static class LayoutRenderer
{
    // The structural outlines are deliberately the renderer's own colours rather
    // than the skin's: they say what kind of node is being drawn, and they have to
    // stay readable in the editor on every skin.
    private static readonly IBrush OutlineColor = SolidColorBrush.Parse("#5B8DEF");
    private static readonly IBrush StackColor = SolidColorBrush.Parse("#3FB950");
    private static readonly IBrush GridColor = SolidColorBrush.Parse("#D29922");

    // Surfaces and text are not constants here. They used to be, and the result
    // was that a page rendered from the schema stayed dark under a light skin —
    // the "a skin only changes some of the colours" failure the skin engine exists
    // to prevent. They are resolved from the active skin at draw time instead
    // (spec 40/47/48).

    public static Control Render(LayoutDocument document) => Render(document, LayoutBindings.Empty, false);

    public static Control Render(LayoutDocument document, LayoutBindings bindings)
        => Render(document, bindings, false);

    /// <summary>
    /// Renders a document.
    ///
    /// <paramref name="editorGuides"/> draws the coloured node outlines the editor
    /// uses to tell a container from a stack from a grid. They are an editing aid:
    /// on a real page they read as debugging leftovers, so a page draws with the
    /// skin's own border instead. Geometry is identical either way, which is what
    /// "the preview is not a mock-up" means.
    /// </summary>
    public static Control Render(LayoutDocument document, LayoutBindings bindings, bool editorGuides)
    {
        // Positions come from the flow layout, so what is drawn is exactly what
        // the editor shows and what the document means.
        var layout = LayoutFlow.Arrange(document);
        var nodes = LayoutEditor.Flatten(document).ToList();

        var right = 320d;
        var bottom = 240d;
        foreach (var node in nodes)
        {
            var rect = RectFor(layout, node);
            right = Math.Max(right, rect.X + rect.Width + 40);
            bottom = Math.Max(bottom, rect.Y + rect.Height + 40);
        }

        var canvas = new Canvas
        {
            Background = Brushes.Transparent,
            Width = right,
            Height = bottom,
        };

        // Parents first, so a container is drawn behind the children it arranges.
        foreach (var node in nodes)
        {
            canvas.Children.Add(RenderNode(node, RectFor(layout, node), bindings, editorGuides));
        }

        return canvas;
    }

    private static LayoutRect RectFor(IReadOnlyDictionary<string, LayoutRect> layout, LayoutNode node)
        => layout.TryGetValue(node.Id, out var rect)
            ? rect
            : new LayoutRect(node.X, node.Y, node.Width, node.Height);

    private static Control RenderNode(LayoutNode node, LayoutRect rect, LayoutBindings bindings, bool editorGuides)
    {
        var control = Build(node, bindings, editorGuides);
        Canvas.SetLeft(control, rect.X);
        Canvas.SetTop(control, rect.Y);
        control.Width = rect.Width;
        control.Height = rect.Height;
        control.IsVisible = node.Visible;

        // A control keeps the size the document gave it.
        //
        // The flow layout hands a stack's children the stack's width, which is right for text and for
        // containers that fill their row — and wrong for a button: stretched to the row, a 160px button
        // becomes a 500px pill with its label at one end, which reads as a misplaced control instead of a
        // button. The declared size is what the person drew in the editor, so it is the size they get.
        if (KeepsDeclaredSize(node))
        {
            control.Width = Math.Min(node.Width, rect.Width);
            control.Height = Math.Min(node.Height, rect.Height);
        }

        // Shape and link are properties of the node, not part of what kind of node it is, so they are
        // applied here where the arranged size is known — a circle needs the height to work out its
        // radius.
        ApplyShape(control, node, rect);
        ApplyLink(control, node, bindings);
        return control;
    }

    /// <summary>
    /// True for the kinds that are controls rather than containers: a button, an input, a switch and a
    /// progress bar draw their own background, and one stretched to its row stops looking like a control.
    /// Text and containers are left to fill, because filling is what they are for.
    /// </summary>
    private static bool KeepsDeclaredSize(LayoutNode node)
        => node.Type is LayoutNodeType.Button or LayoutNodeType.Input or LayoutNodeType.Toggle or LayoutNodeType.Progress
           && node.Width > 0
           && node.Height > 0;

    /// <summary>
    /// The shape a node wears, when it was given one. A node with no shape keeps what its type draws,
    /// which is what every document written before shapes existed has.
    /// </summary>
    private static void ApplyShape(Control control, LayoutNode node, LayoutRect rect)
    {
        if (!node.Properties.TryGetValue("shape", out var shape) || string.IsNullOrWhiteSpace(shape))
        {
            return;
        }

        var radius = shape.Trim().ToLowerInvariant() switch
        {
            "rect" => new CornerRadius(0),
            "circle" => new CornerRadius(Math.Max(0, Math.Min(rect.Width, rect.Height) / 2)),
            _ => new CornerRadius(6),
        };

        // A Border draws the node's surface; setting the radius on it is what turns a card into a pill
        // or a circle, in the editor and on the page alike.
        if (control is Border border)
        {
            border.CornerRadius = radius;
        }
        else if (control is Panel panel)
        {
            panel.ClipToBounds = radius != default;
        }
    }

    /// <summary>
    /// Makes a node clickable when it carries a link: the pointer says so, the address is shown on hover,
    /// and the click goes to <see cref="LayoutBindings.FollowLink"/>. Following it belongs to whoever
    /// hosts the page — a rendered document cannot know what a link means in a given app, and inventing a
    /// navigation would be a lie about what the node does. With no follower the link is only what it looks
    /// like, which is what the editor passes while the user is arranging the page.
    /// </summary>
    private static void ApplyLink(Control control, LayoutNode node, LayoutBindings bindings)
    {
        if (!node.Properties.TryGetValue("link", out var link) || string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        control.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(control, link);

        // Without this the link is decoration: the user fills the box in, sees the hand cursor, clicks,
        // and nothing happens.
        if (bindings.FollowLink is { } follow)
        {
            control.PointerPressed += (_, args) =>
            {
                // Handled only when it led somewhere: a link the host could not follow leaves the click
                // to whatever else may have wanted it.
                if (follow(link))
                {
                    args.Handled = true;
                }
            };
        }
    }

    /// <summary>A skin colour, re-resolved when the skin changes.</summary>
    private static DynamicResourceExtension Skin(string key) => new(key);

    /// <summary>The skin resource a schema colour name stands for.</summary>
    private static string SkinKey(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "accent" => "SkinAccent",
        "background" => "SkinBackground",
        "surface" => "SkinSurface",
        "surfacealt" => "SkinSurfaceAlt",
        "border" => "SkinBorder",
        "muted" => "SkinMuted",
        _ => "SkinForeground",
    };

    /// <summary>A literal schema colour, or null when it is absent or unusable.</summary>
    private static IBrush? ParseBrush(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        try
        {
            return SolidColorBrush.Parse(color);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Colours a control from a schema colour: a literal colour is used exactly as
    /// written, and anything else names a skin colour, so one document reads
    /// correctly under every skin. An unknown name falls back to the skin's
    /// foreground rather than leaving the text invisible.
    /// </summary>
    private static void ApplyColor(AvaloniaObject target, AvaloniaProperty property, string? color)
    {
        var literal = ParseBrush(color);
        if (literal is not null)
        {
            target.SetValue(property, literal);
            return;
        }

        target.Bind(property, Skin(SkinKey(color)));
    }

    /// <summary>Resolved text for a node, falling back to its authored text.</summary>
    private static string? BoundText(LayoutNode node, LayoutBindings bindings)
    {
        var parsed = LayoutBindings.Parse(node.Binding);
        return parsed is { Kind: "text" } text
            ? bindings.Text(text.Name) ?? node.Text
            : node.Text;
    }

    private static Control Build(LayoutNode node, LayoutBindings bindings, bool editorGuides) => node.Type switch
    {
        LayoutNodeType.Text => BuildText(node, bindings),
        LayoutNodeType.Button => BuildButton(node, bindings),
        LayoutNodeType.Input => new TextBox
        {
            Text = node.Text ?? string.Empty,
            Watermark = "输入…",
            VerticalContentAlignment = VerticalAlignment.Center,
        },
        LayoutNodeType.Toggle => BuildToggle(node),
        LayoutNodeType.Progress => BuildProgress(node, bindings),
        LayoutNodeType.List => BuildList(node, bindings),
        LayoutNodeType.Image => BuildImage(node),
        // A null border means "use the skin's border", which is what a real page gets.
        LayoutNodeType.Grid => BuildContainer(node, editorGuides ? GridColor : null),
        LayoutNodeType.Stack => BuildContainer(node, editorGuides ? StackColor : null),
        _ => BuildContainer(node, editorGuides ? OutlineColor : null),
    };

    private static Control BuildText(LayoutNode node, LayoutBindings bindings)
    {
        var text = new TextBlock
        {
            Text = BoundText(node, bindings) ?? string.Empty,
            FontSize = node.FontSize ?? 14,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The schema has always carried a Color; it is honoured here, and a colour
        // that is not a literal is read as a skin colour rather than dropped.
        ApplyColor(text, TextBlock.ForegroundProperty, node.Color);
        return text;
    }

    private static Control BuildToggle(LayoutNode node)
    {
        var toggle = new CheckBox
        {
            Content = node.Text ?? "开关",
            IsChecked = true,
        };

        ApplyColor(toggle, TemplatedControl.ForegroundProperty, node.Color);
        return toggle;
    }

    private static Control BuildButton(LayoutNode node, LayoutBindings bindings)
    {
        var button = new Button
        {
            Content = BoundText(node, bindings) ?? "按钮",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var parsed = LayoutBindings.Parse(node.Binding);
        if (parsed is { Kind: "action" } action && bindings.Actions.TryGetValue(action.Name, out var command))
        {
            button.Command = command;
        }

        return button;
    }

    private static Control BuildProgress(LayoutNode node, LayoutBindings bindings)
    {
        var parsed = LayoutBindings.Parse(node.Binding);
        var value = parsed is { Kind: "value" } bound ? bindings.Value?.Invoke(bound.Name) ?? 0 : 0;

        // Starts at zero instead of a made-up number: a progress bar reading 45%
        // while nothing is happening is simply false.
        return new ProgressBar
        {
            Value = Math.Clamp(value, 0, 100),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static Control BuildList(LayoutNode node, LayoutBindings bindings)
    {
        var list = new ListBox();
        list.Bind(TemplatedControl.BackgroundProperty, Skin("SkinSurface"));

        var parsed = LayoutBindings.Parse(node.Binding);

        if (parsed is { Kind: "items" } bound && bindings.Items is not null)
        {
            foreach (var item in bindings.Items(bound.Name))
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("200,*"),
                    Margin = new Thickness(6, 3),
                };

                var label = new TextBlock { Text = item.Label };
                label.Bind(TextBlock.ForegroundProperty, Skin("SkinMuted"));

                var value = new TextBlock { Text = item.Value };
                value.Bind(TextBlock.ForegroundProperty, Skin("SkinForeground"));

                Grid.SetColumn(label, 0);
                Grid.SetColumn(value, 1);

                row.Children.Add(label);
                row.Children.Add(value);
                list.Items.Add(row);
            }
        }

        // With no items binding the list is empty rather than filled with sample
        // text, which is the difference between a placeholder and a lie.
        return list;
    }

    /// <summary>
    /// An image node, drawn from the picture it names.
    ///
    /// <para>
    /// It used to be a grey box with the word 「图片」 in it whatever the document said, which is why
    /// adding an image component looked like it did nothing. A node with a picture draws it; a node
    /// without one keeps the placeholder, because an empty frame is honest and an invented picture is
    /// not.
    /// </para>
    /// </summary>
    private static Control BuildImage(LayoutNode node)
    {
        if (TryLoadPicture(node) is { } picture)
        {
            return new Image
            {
                Source = picture,
                Stretch = Stretch.UniformToFill,
            };
        }

        var caption = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(node.Text) ? "双击右侧「图片文件」选一张" : node.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        caption.Bind(TextBlock.ForegroundProperty, Skin("SkinMuted"));

        var placeholder = new Border
        {
            BorderThickness = new Thickness(1),
            Child = caption,
        };
        placeholder.Bind(Border.BackgroundProperty, Skin("SkinSurface"));
        placeholder.Bind(Border.BorderBrushProperty, Skin("SkinBorder"));

        return placeholder;
    }

    /// <summary>The picture a node names, or null when it names none that can be read.</summary>
    private static IImage? TryLoadPicture(LayoutNode node)
    {
        if (!node.Properties.TryGetValue("src", out var source) || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        try
        {
            return File.Exists(source) ? new Bitmap(source) : null;
        }
        catch (Exception)
        {
            // A file that is not a picture, or has been moved: the placeholder is the honest answer,
            // and the editor should not come down over one bad path.
            return null;
        }
    }

    /// <summary>
    /// Containers render their children on their own canvas, so nested geometry
    /// stays absolute and predictable.
    /// </summary>
    private static Control BuildContainer(LayoutNode node, IBrush? border)
    {
        var inner = new Canvas
        {
            Background = Brushes.Transparent,
            Width = Math.Max(0, node.Width),
            Height = Math.Max(0, node.Height),
        };

        // Children are not drawn here: Render places every node absolutely using
        // the flow layout, so drawing them again inside the frame would double
        // them and put them at the authored position rather than the arranged one.

        // Flat by default, and deliberately so: the library's frame art carries corner
        // decorations (a figure, a badge). Repeated on every card they turn a page into
        // wallpaper and press the title under the border, which is worse than a plain box.
        // The frame art stays available for a single hero panel.
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = inner,
        };
        frame.Bind(Border.BackgroundProperty, Skin("SkinSurface"));

        if (border is null)
        {
            frame.Bind(Border.BorderBrushProperty, Skin("SkinBorder"));
        }
        else
        {
            frame.BorderBrush = border;
        }

        // A card that already has its own title text node would otherwise show the
        // title twice: once from this header and once from the node. The authored
        // node wins, because it is the one the editor can select and move.
        var hasAuthoredTitle = node.Children.Any(child =>
            child.Type == LayoutNodeType.Text
            && !string.IsNullOrWhiteSpace(child.Text)
            && string.Equals(child.Text, node.Text, StringComparison.Ordinal));

        if (!hasAuthoredTitle && !string.IsNullOrWhiteSpace(node.Text))
        {
            var stack = new DockPanel();
            var header = new TextBlock
            {
                Text = node.Text,
                Margin = new Thickness(8, 4),
                FontSize = 12,
            };
            header.Bind(TextBlock.ForegroundProperty, Skin("SkinMuted"));

            DockPanel.SetDock(header, Dock.Top);
            stack.Children.Add(header);
            stack.Children.Add(frame);

            DeclareSurface(stack, node);
            return stack;
        }

        DeclareSurface(frame, node);
        return frame;
    }

    /// <summary>
    /// Marks a schema-rendered card as the one editable thing it really is: a surface painted with the
    /// skin's card colour.
    ///
    /// <para>
    /// The overview's body is rendered from the layout document, and its text nodes are bound to live
    /// facts — the machine name, the user name, version numbers. Those are values, not skin words, so
    /// declaring them editable would offer an edit that changes nothing: the failure the workshop's
    /// own rules forbid. The card is different: it is genuinely a skin surface, and it is what the
    /// user is pointing at, so a click anywhere in the page now lands on something real. The text
    /// nodes deliberately declare nothing, which is what lets the click walk up to the card.
    /// </para>
    /// </summary>
    private static void DeclareSurface(Control control, LayoutNode node)
    {
        SkinElement.SetId(control, "layout." + node.Id);
        SkinElement.SetKind(control, SkinElementKind.ColorSurface);
        SkinElement.SetDisplayName(control, "布局卡片");
        SkinElement.SetColorKey(control, "surface");
    }
}
