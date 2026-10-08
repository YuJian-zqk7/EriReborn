using EriReborn.Layout;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Flow layout for Stack and Grid. These two types used to be drawn as plain
/// boxes whose children never moved, so the palette advertised a capability the
/// renderer did not have.
/// </summary>
public sealed class LayoutFlowTests
{
    private static LayoutNode Leaf(string id, double x = 0, double y = 0, double w = 100, double h = 20)
        => new() { Id = id, Type = LayoutNodeType.Button, X = x, Y = y, Width = w, Height = h };

    private static LayoutDocument Document(params LayoutNode[] nodes)
        => new() { Page = "Test", Nodes = nodes };

    private static readonly Dictionary<string, string> Vertical = new() { ["orientation"] = "vertical" };
    private static readonly Dictionary<string, string> Horizontal = new() { ["orientation"] = "horizontal" };

    [Fact]
    public void A_standalone_node_keeps_its_authored_position()
    {
        var layout = LayoutFlow.Arrange(Document(Leaf("a", 40, 60)));

        Assert.Equal(new LayoutRect(40, 60, 100, 20), layout["a"]);
    }

    [Fact]
    public void A_vertical_stack_places_children_top_to_bottom()
    {
        var stack = new LayoutNode
        {
            Id = "s",
            Type = LayoutNodeType.Stack,
            X = 10,
            Y = 20,
            Width = 200,
            Height = 200,
            Properties = Vertical,
            Children = new[] { Leaf("a", 999, 999, 100, 30), Leaf("b", 999, 999, 100, 40) },
        };

        var layout = LayoutFlow.Arrange(Document(stack));

        // Authored child positions are ignored: the container decides.
        Assert.Equal(10 + LayoutFlow.DefaultPadding, layout["a"].X);
        Assert.Equal(20 + LayoutFlow.DefaultPadding, layout["a"].Y);
        Assert.Equal(20 + LayoutFlow.DefaultPadding + 30 + LayoutFlow.DefaultSpacing, layout["b"].Y);
    }

    [Fact]
    public void A_horizontal_stack_places_children_side_by_side()
    {
        var stack = new LayoutNode
        {
            Id = "s",
            Type = LayoutNodeType.Stack,
            X = 0,
            Y = 0,
            Width = 300,
            Height = 80,
            Properties = Horizontal,
            Children = new[] { Leaf("a", 0, 0, 50, 20), Leaf("b", 0, 0, 70, 20) },
        };

        var layout = LayoutFlow.Arrange(Document(stack));

        Assert.Equal(LayoutFlow.DefaultPadding, layout["a"].X);
        Assert.Equal(LayoutFlow.DefaultPadding + 50 + LayoutFlow.DefaultSpacing, layout["b"].X);
        Assert.Equal(layout["a"].Y, layout["b"].Y);
    }

    [Fact]
    public void Spacing_and_padding_are_taken_from_the_node()
    {
        var stack = new LayoutNode
        {
            Id = "s",
            Type = LayoutNodeType.Stack,
            Width = 200,
            Height = 200,
            Properties = new Dictionary<string, string>
            {
                ["orientation"] = "vertical",
                ["padding"] = "4",
                ["spacing"] = "2",
            },
            Children = new[] { Leaf("a", 0, 0, 100, 10), Leaf("b", 0, 0, 100, 10) },
        };

        var layout = LayoutFlow.Arrange(Document(stack));

        Assert.Equal(4, layout["a"].Y);
        Assert.Equal(4 + 10 + 2, layout["b"].Y);
    }

    [Fact]
    public void A_grid_places_children_into_the_declared_columns()
    {
        var grid = new LayoutNode
        {
            Id = "g",
            Type = LayoutNodeType.Grid,
            Y = 0,
            Width = 320, // 320 - 16 padding = 304 inner
            Height = 200,
            Properties = new Dictionary<string, string> { ["columns"] = "3" },
            Children = new[] { Leaf("a", 0, 0, 10, 20), Leaf("b", 0, 0, 10, 20), Leaf("c", 0, 0, 10, 20) },
        };

        var layout = LayoutFlow.Arrange(Document(grid));

        const double inner = 320 - LayoutFlow.DefaultPadding * 2;
        var cell = (inner - LayoutFlow.DefaultSpacing * 2) / 3;

        // Cells share the width, so the grid looks like a grid.
        Assert.Equal(cell, layout["a"].Width, 3);
        Assert.Equal(LayoutFlow.DefaultPadding, layout["a"].X, 3);
        Assert.Equal(LayoutFlow.DefaultPadding + cell + LayoutFlow.DefaultSpacing, layout["b"].X, 3);

        // The fourth child starts a new row.
        Assert.Equal(layout["a"].Y, layout["c"].Y);
    }

    [Fact]
    public void A_grid_wraps_to_a_second_row()
    {
        var grid = new LayoutNode
        {
            Id = "g",
            Type = LayoutNodeType.Grid,
            Width = 220,
            Height = 200,
            Properties = new Dictionary<string, string> { ["columns"] = "2" },
            Children = new[] { Leaf("a", 0, 0, 10, 20), Leaf("b", 0, 0, 10, 20), Leaf("c", 0, 0, 10, 20) },
        };

        var layout = LayoutFlow.Arrange(Document(grid));

        Assert.Equal(layout["a"].Y, layout["b"].Y);
        Assert.True(layout["c"].Y > layout["a"].Y, "the third child should wrap onto the next row");
    }

    [Fact]
    public void A_grid_row_is_as_tall_as_its_tallest_child()
    {
        var grid = new LayoutNode
        {
            Id = "g",
            Type = LayoutNodeType.Grid,
            Width = 220,
            Height = 300,
            Properties = new Dictionary<string, string> { ["columns"] = "2" },
            Children = new[] { Leaf("a", 0, 0, 10, 20), Leaf("b", 0, 0, 10, 50), Leaf("c", 0, 0, 10, 10) },
        };

        var layout = LayoutFlow.Arrange(Document(grid));

        Assert.Equal(50, layout["a"].Height);
        Assert.Equal(50, layout["b"].Height);

        // The second row starts below the first row's height, not below 20.
        Assert.Equal(layout["a"].Y + 50 + LayoutFlow.DefaultSpacing, layout["c"].Y);
    }

    [Fact]
    public void A_container_positions_children_relative_to_itself()
    {
        var container = new LayoutNode
        {
            Id = "c",
            Type = LayoutNodeType.Container,
            X = 100,
            Y = 50,
            Width = 300,
            Height = 200,
            Children = new[] { Leaf("a", 12, 14) },
        };

        var layout = LayoutFlow.Arrange(Document(container));

        Assert.Equal(112, layout["a"].X);
        Assert.Equal(64, layout["a"].Y);
    }

    [Fact]
    public void An_empty_stack_is_not_an_error()
    {
        var stack = new LayoutNode
        {
            Id = "s",
            Type = LayoutNodeType.Stack,
            Width = 100,
            Height = 100,
            Children = Array.Empty<LayoutNode>(),
        };

        var layout = LayoutFlow.Arrange(Document(stack));

        Assert.Single(layout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_stack_and_grid_children_are_position_managed(bool expected)
    {
        var stack = new LayoutNode
        {
            Id = "s",
            Type = LayoutNodeType.Stack,
            Width = 200,
            Height = 200,
            Children = new[] { Leaf("sChild") },
        };

        var container = new LayoutNode
        {
            Id = "t",
            Type = LayoutNodeType.Container,
            Width = 200,
            Height = 200,
            Children = new[] { Leaf("tChild") },
        };

        var document = Document(stack, container);

        Assert.Equal(expected, LayoutFlow.IsManagedByParent(document, expected ? "sChild" : "tChild"));
    }

    [Fact]
    public void Managed_children_are_still_placed_so_the_canvas_can_draw_them()
    {
        var stack = new LayoutNode
        {
            Id = "s",
            Type = LayoutNodeType.Stack,
            X = 5,
            Y = 5,
            Width = 200,
            Height = 200,
            Children = new[] { Leaf("a") },
        };

        var layout = LayoutFlow.Arrange(Document(stack));

        Assert.True(layout.ContainsKey("s"));
        Assert.True(layout.ContainsKey("a"));
    }
}
