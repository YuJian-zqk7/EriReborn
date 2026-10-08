using EriReborn.Layout;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The layout editor is schema-driven (spec 39): drag, resize, visibility and
/// text edits are structural operations on JSON data, never XAML text.
/// </summary>
public sealed class LayoutEditorTests
{
    private static LayoutDocument Document() => new()
    {
        Page = "Home",
        Nodes = new LayoutNode[]
        {
            new() { Id = "root", Type = LayoutNodeType.Page, X = 0, Y = 0, Width = 800, Height = 600 },
            new()
            {
                Id = "card",
                Type = LayoutNodeType.Container,
                X = 20,
                Y = 30,
                Width = 300,
                Height = 200,
                Children = new LayoutNode[]
                {
                    new() { Id = "label", Type = LayoutNodeType.Text, Text = "hello", X = 10, Y = 12, Width = 120, Height = 24 },
                },
            },
        },
    };

    [Fact]
    public void Document_round_trips_through_json()
    {
        var original = LayoutDefaults.Overview();

        var json = LayoutSerializer.Serialize(original);
        var restored = LayoutSerializer.Deserialize(json);

        Assert.Equal(original.Page, restored.Page);
        Assert.Equal(original.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(original.Nodes[0].Id, restored.Nodes[0].Id);
        Assert.Equal(original.Nodes[0].Type, restored.Nodes[0].Type);
        Assert.Equal(original.Nodes[0].Width, restored.Nodes[0].Width);

        // The grid lives inside its card, so it is found rather than assumed to be
        // a top-level node — the document is nested on purpose.
        var grid = LayoutEditor.Find(restored, "statsGrid")!;
        Assert.Equal("2", grid.Properties["columns"]);
        Assert.Contains("Grid", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_documents_round_trip()
    {
        var restored = LayoutSerializer.Deserialize(LayoutSerializer.Serialize(Document()));

        var card = restored.Nodes.Single(n => n.Id == "card");
        var label = Assert.Single(card.Children);
        Assert.Equal("label", label.Id);
        Assert.Equal("hello", label.Text);
    }

    [Fact]
    public void Move_changes_only_the_target_and_clamps_at_zero()
    {
        var document = Move_Setup();

        var moved = LayoutEditor.Move(document, "label", 15, -100);

        var label = LayoutEditor.Find(moved, "label")!;
        Assert.Equal(25, label.X);
        Assert.Equal(0, label.Y);            // clamped, never negative
        Assert.Equal(30, LayoutEditor.Find(moved, "card")!.Y);   // sibling untouched

        static LayoutDocument Move_Setup() => Document();
    }

    [Fact]
    public void Resize_clamps_to_the_minimum_size()
    {
        var resized = LayoutEditor.Resize(Document(), "label", -1000, -1000);
        var label = LayoutEditor.Find(resized, "label")!;

        Assert.Equal(LayoutNode.MinSize, label.Width);
        Assert.Equal(LayoutNode.MinSize, label.Height);
    }

    [Fact]
    public void Visibility_text_and_properties_are_editable()
    {
        var document = Document();

        document = LayoutEditor.SetVisible(document, "label", false);
        document = LayoutEditor.SetText(document, "label", "changed");
        document = LayoutEditor.SetProperty(document, "card", "columns", "2");

        var label = LayoutEditor.Find(document, "label")!;
        Assert.False(label.Visible);
        Assert.Equal("changed", label.Text);
        Assert.Equal("2", LayoutEditor.Find(document, "card")!.Properties["columns"]);

        document = LayoutEditor.SetProperty(document, "card", "columns", null);
        Assert.False(LayoutEditor.Find(document, "card")!.Properties.ContainsKey("columns"));
    }

    [Fact]
    public void Nodes_can_be_added_at_the_root_and_inside_a_parent()
    {
        var document = Document();

        document = LayoutEditor.Add(document, null, new LayoutNode
        {
            Id = "footer",
            Type = LayoutNodeType.Text,
            X = 0,
            Y = 560,
            Width = 200,
            Height = 24,
        });

        document = LayoutEditor.Add(document, "card", new LayoutNode
        {
            Id = "badge",
            Type = LayoutNodeType.Toggle,
            X = 10,
            Y = 60,
            Width = 80,
            Height = 24,
        });

        Assert.NotNull(LayoutEditor.Find(document, "footer"));
        var card = LayoutEditor.Find(document, "card")!;
        Assert.Equal(2, card.Children.Count);
        Assert.Contains(card.Children, c => c.Id == "badge");
    }

    [Fact]
    public void Removing_a_parent_removes_its_children()
    {
        var document = LayoutEditor.Remove(Document(), "card");

        Assert.Null(LayoutEditor.Find(document, "card"));
        Assert.Null(LayoutEditor.Find(document, "label"));
        Assert.Single(document.Nodes);
    }

    [Fact]
    public void Absolute_position_accumulates_parent_offsets()
    {
        var (x, y) = LayoutEditor.AbsolutePosition(Document(), "label");

        Assert.Equal(30, x);   // 20 + 10
        Assert.Equal(42, y);   // 30 + 12
    }

    [Fact]
    public void A_no_op_update_returns_the_same_document_instance()
    {
        var document = Document();
        var result = LayoutEditor.Move(document, "does-not-exist", 5, 5);

        Assert.Same(document, result);
    }

    [Fact]
    public void Suggested_ids_do_not_collide()
    {
        var document = Document();

        var first = LayoutEditor.SuggestId(document, LayoutNodeType.Text);
        document = LayoutEditor.Add(document, null, LayoutNode.CreateDefault(first, LayoutNodeType.Text));
        var second = LayoutEditor.SuggestId(document, LayoutNodeType.Text);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_default_document_is_valid()
    {
        var result = LayoutValidator.Validate(LayoutDefaults.Overview());

        // The issues themselves, otherwise the failure says only "false".
        Assert.True(result.IsValid, string.Join(" | ", result.Issues));
    }

    [Fact]
    public void Validator_reports_duplicate_ids()
    {
        var document = new LayoutDocument
        {
            Page = "Home",
            Nodes = new LayoutNode[]
            {
                new() { Id = "same", Type = LayoutNodeType.Text, Width = 100, Height = 20 },
                new() { Id = "same", Type = LayoutNodeType.Button, Width = 100, Height = 20 },
            },
        };

        var result = LayoutValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "layout.duplicate_id");
    }

    [Fact]
    public void Validator_reports_bad_geometry_and_ids()
    {
        var document = new LayoutDocument
        {
            Page = "Home",
            Nodes = new LayoutNode[]
            {
                new() { Id = "has space", Type = LayoutNodeType.Text, Width = 100, Height = 20 },
                new() { Id = "tiny", Type = LayoutNodeType.Text, X = -5, Y = 0, Width = 2, Height = 2 },
            },
        };

        var result = LayoutValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "layout.whitespace_id");
        Assert.Contains(result.Issues, i => i.Code == "layout.too_small");
        Assert.Contains(result.Issues, i => i.Code == "layout.negative_position");
    }

    [Fact]
    public void Validator_rejects_a_grid_without_a_positive_column_count()
    {
        var document = new LayoutDocument
        {
            Page = "Home",
            Nodes = new LayoutNode[]
            {
                new()
                {
                    Id = "grid",
                    Type = LayoutNodeType.Grid,
                    Width = 200,
                    Height = 100,
                    Properties = new Dictionary<string, string> { ["columns"] = "zero" },
                },
            },
        };

        var result = LayoutValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "layout.bad_columns");
    }

    [Fact]
    public void Flatten_visits_every_node_once()
    {
        var all = LayoutEditor.Flatten(Document()).Select(n => n.Id).ToArray();

        Assert.Equal(new[] { "root", "card", "label" }, all);
    }

    /// <summary>
    /// The Overview page's scan button belongs on the card's title row.
    ///
    /// <para>
    /// It used to be the first child of the card's vertical stack, so it floated above the progress bar
    /// and the status lines it acts on — neither beside the title nor in a footer, which is exactly what
    /// made the position read as wrong. This pins the arrangement, and pins that the two no longer
    /// overlap, so a later edit cannot quietly put it back.
    /// </para>
    /// </summary>
    [Fact]
    public void The_overview_scan_button_sits_on_the_card_title_row()
    {
        var document = LayoutDefaults.Overview();
        var layout = LayoutFlow.Arrange(document);

        var button = Find(document, "scanButton");
        Assert.NotNull(button);

        // Its parent is the card, not the stack: a child of the stack is positioned by the stack, which
        // is what put it back at the top of the body in the first place.
        Assert.Equal("scanCard", ParentOf(document.Nodes, "scanButton")?.Id);

        var buttonRect = layout["scanButton"];
        var titleRect = layout["scanCardTitle"];
        var bodyRect = layout["scanStack"];

        Assert.True(
            titleRect.X + titleRect.Width <= buttonRect.X,
            $"标题右边缘 {titleRect.X + titleRect.Width} 压到了按钮左边缘 {buttonRect.X}。");

        Assert.True(
            buttonRect.Y + buttonRect.Height <= bodyRect.Y,
            $"按钮底边 {buttonRect.Y + buttonRect.Height} 压到了正文顶部 {bodyRect.Y}。");

        // Still the card's action: moving it must not detach it from the scan command.
        Assert.Equal("action:scan", button!.Binding);
    }

    private static LayoutNode? Find(LayoutDocument document, string id)
        => LayoutEditor.Flatten(document)
            .FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.Ordinal));

    private static LayoutNode? ParentOf(IReadOnlyList<LayoutNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Any(child => string.Equals(child.Id, id, StringComparison.Ordinal)))
            {
                return node;
            }

            if (ParentOf(node.Children, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
