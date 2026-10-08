using EriReborn.Core.Validation;

namespace EriReborn.Layout;

/// <summary>
/// Validates a layout document before it is rendered, saved or shared. Invalid
/// documents are reported with concrete reasons (spec 16/39).
/// </summary>
public static class LayoutValidator
{
    public static ValidationResult Validate(LayoutDocument document)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(document.Page))
        {
            issues.Add(new ValidationIssue("layout.no_page", "布局必须声明所属页面。"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(document.Nodes, seen, issues, "root");
        CheckOverflow(document, issues);

        return ValidationResult.From(issues);
    }

    /// <summary>
    /// Reports arranged children that do not fit inside their container. Without
    /// this, a Stack or Grid whose content is taller than its frame clips silently
    /// and the only clue is a missing element.
    /// </summary>
    private static void CheckOverflow(LayoutDocument document, List<ValidationIssue> issues)
    {
        const double tolerance = 0.5;

        var layout = LayoutFlow.Arrange(document);

        foreach (var node in LayoutEditor.Flatten(document))
        {
            if (node.Type is not (LayoutNodeType.Stack or LayoutNodeType.Grid) || node.Children.Count == 0)
            {
                continue;
            }

            if (!layout.TryGetValue(node.Id, out var frame))
            {
                continue;
            }

            foreach (var child in node.Children)
            {
                if (!layout.TryGetValue(child.Id, out var rect))
                {
                    continue;
                }

                var overflowsRight = rect.X + rect.Width > frame.X + frame.Width + tolerance;
                var overflowsBottom = rect.Y + rect.Height > frame.Y + frame.Height + tolerance;

                if (overflowsRight || overflowsBottom)
                {
                    issues.Add(new ValidationIssue(
                        "layout.overflow",
                        $"{child.Id} 超出 {node.Id} 的边界，内容会被裁切；请加大容器或减少子元素。",
                        node.Id));
                }
            }
        }
    }

    private static void Walk(
        IReadOnlyList<LayoutNode> nodes,
        HashSet<string> seen,
        List<ValidationIssue> issues,
        string parentPath)
    {
        foreach (var node in nodes)
        {
            var path = $"{parentPath}/{node.Id}";

            if (string.IsNullOrWhiteSpace(node.Id))
            {
                issues.Add(new ValidationIssue("layout.empty_id", "节点 id 不能为空。", parentPath));
            }
            else
            {
                if (node.Id.Any(char.IsWhiteSpace))
                {
                    issues.Add(new ValidationIssue("layout.whitespace_id", "节点 id 不能包含空白字符。", node.Id));
                }

                if (!seen.Add(node.Id))
                {
                    issues.Add(new ValidationIssue("layout.duplicate_id", "节点 id 重复。", node.Id));
                }
            }

            if (node.Width < LayoutNode.MinSize || node.Height < LayoutNode.MinSize)
            {
                issues.Add(new ValidationIssue(
                    "layout.too_small",
                    $"节点尺寸不得小于 {LayoutNode.MinSize}。",
                    node.Id));
            }

            if (node.X < 0 || node.Y < 0)
            {
                issues.Add(new ValidationIssue("layout.negative_position", "节点坐标不能为负。", node.Id));
            }

            if (node.Type == LayoutNodeType.Grid
                && node.Properties.TryGetValue("columns", out var columns)
                && (!int.TryParse(columns, out var count) || count < 1))
            {
                issues.Add(new ValidationIssue("layout.bad_columns", "Grid 的 columns 必须是正整数。", node.Id));
            }

            Walk(node.Children, seen, issues, path);
        }
    }
}
