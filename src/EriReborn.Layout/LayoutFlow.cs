namespace EriReborn.Layout;

/// <summary>Computed geometry for one node.</summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height);

/// <summary>
/// Turns the authored document into the geometry that is actually drawn.
///
/// Most nodes are positioned absolutely, because that is what makes dragging
/// meaningful. <see cref="LayoutNodeType.Stack"/> and
/// <see cref="LayoutNodeType.Grid"/> instead arrange their children: a Stack
/// lays them along one axis, a Grid places them into fixed columns. Without this
/// the two types were merely coloured boxes whose children never moved.
///
/// The computation is pure and separate from rendering so it can be tested and
/// so the editor and the renderer cannot disagree about where a node is.
/// </summary>
public static class LayoutFlow
{
    public const double DefaultSpacing = 8;

    public const double DefaultPadding = 8;

    public const int DefaultColumns = 2;

    /// <summary>Effective geometry for every node reachable from the document.</summary>
    public static IReadOnlyDictionary<string, LayoutRect> Arrange(LayoutDocument document)
    {
        var result = new Dictionary<string, LayoutRect>(StringComparer.Ordinal);

        foreach (var node in document.Nodes)
        {
            // A top-level node's authored position is absolute.
            Place(node, node.X, node.Y, null, null, result);
        }

        return result;
    }

    /// <summary>True when the node's parent decides its position.</summary>
    public static bool IsManagedByParent(LayoutDocument document, string id)
    {
        var parent = ParentOf(document.Nodes, id, null);
        return parent is LayoutNodeType.Stack or LayoutNodeType.Grid;
    }

    private static LayoutNodeType? ParentOf(IReadOnlyList<LayoutNode> nodes, string id, LayoutNodeType? parentType)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Id, id, StringComparison.Ordinal))
            {
                return parentType;
            }

            var found = ParentOf(node.Children, id, node.Type);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void Place(
        LayoutNode node,
        double x,
        double y,
        double? widthOverride,
        double? heightOverride,
        Dictionary<string, LayoutRect> into)
    {
        var width = widthOverride ?? node.Width;
        var height = heightOverride ?? node.Height;
        into[node.Id] = new LayoutRect(x, y, width, height);

        var padding = Number(node, "padding", DefaultPadding);
        var spacing = Number(node, "spacing", DefaultSpacing);

        switch (node.Type)
        {
            case LayoutNodeType.Stack:
                PlaceStack(node, x, y, padding, spacing, into);
                break;

            case LayoutNodeType.Grid:
                PlaceGrid(node, x, y, padding, spacing, into);
                break;

            default:
                // Everything else, including Container and Page, positions its
                // children relative to itself.
                foreach (var child in node.Children)
                {
                    Place(child, x + child.X, y + child.Y, null, null, into);
                }

                break;
        }
    }

    private static void PlaceStack(
        LayoutNode node,
        double x,
        double y,
        double padding,
        double spacing,
        Dictionary<string, LayoutRect> into)
    {
        var horizontal = string.Equals(
            Property(node, "orientation"),
            "horizontal",
            StringComparison.OrdinalIgnoreCase);

        var cursor = horizontal ? x + padding : y + padding;

        foreach (var child in node.Children)
        {
            if (horizontal)
            {
                Place(child, cursor, y + padding, null, null, into);
                cursor += child.Width + spacing;
            }
            else
            {
                Place(child, x + padding, cursor, null, null, into);
                cursor += child.Height + spacing;
            }
        }
    }

    private static void PlaceGrid(
        LayoutNode node,
        double x,
        double y,
        double padding,
        double spacing,
        Dictionary<string, LayoutRect> into)
    {
        var columns = Math.Max(1, (int)Number(node, "columns", DefaultColumns));
        var innerWidth = Math.Max(0, node.Width - padding * 2);

        // Cells share the width, so a Grid looks like a Grid rather than a row
        // of differently sized boxes.
        var cellWidth = Math.Max(
            LayoutNode.MinSize,
            (innerWidth - spacing * (columns - 1)) / columns);

        var rows = (node.Children.Count + columns - 1) / columns;

        for (var row = 0; row < rows; row++)
        {
            var rowStart = row * columns;
            var rowCount = Math.Min(columns, node.Children.Count - rowStart);
            if (rowCount <= 0)
            {
                break;
            }

            // Each row is as tall as its tallest child.
            var rowHeight = LayoutNode.MinSize;
            for (var i = 0; i < rowCount; i++)
            {
                rowHeight = Math.Max(rowHeight, node.Children[rowStart + i].Height);
            }

            var rowY = y + padding;
            for (var previous = 0; previous < row; previous++)
            {
                rowY += RowHeight(node, previous, columns) + spacing;
            }

            for (var column = 0; column < rowCount; column++)
            {
                var child = node.Children[rowStart + column];
                Place(
                    child,
                    x + padding + column * (cellWidth + spacing),
                    rowY,
                    cellWidth,
                    rowHeight,
                    into);
            }
        }
    }

    private static double RowHeight(LayoutNode grid, int row, int columns)
    {
        var start = row * columns;
        var height = LayoutNode.MinSize;
        for (var i = start; i < Math.Min(start + columns, grid.Children.Count); i++)
        {
            height = Math.Max(height, grid.Children[i].Height);
        }

        return height;
    }

    private static string? Property(LayoutNode node, string key)
        => node.Properties.TryGetValue(key, out var value) ? value : null;

    private static double Number(LayoutNode node, string key, double fallback)
        => double.TryParse(
            Property(node, key),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;
}
