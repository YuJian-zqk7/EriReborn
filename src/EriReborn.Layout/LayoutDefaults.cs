namespace EriReborn.Layout;

/// <summary>Starting documents so the editor never opens empty.</summary>
public static class LayoutDefaults
{
    /// <summary>
    /// Field labels are muted. The name is not a literal colour: the renderer reads
    /// it as the active skin's muted colour, so one document stays readable under a
    /// light skin and a dark one (previously this froze one palette into the page).
    /// </summary>
    private const string Muted = "muted";

    /// <summary>
    /// Describes the Overview page.
    ///
    /// The document carries the layout and binding names only; the host supplies
    /// the values. Writing values here would freeze them, and the page would then
    /// show last week's machine name forever.
    /// </summary>
    public static LayoutDocument Overview() => new()
    {
        Page = "Home",
        Nodes = FoldContents(
            new LayoutNode[]
        {
            new()
            {
                Id = "root",
                Type = LayoutNodeType.Page,

                // No Text: the shell already titles the page ("Overview"), and a text
                // node on the page root drew a second title at the very top-left, on
                // top of the first card.
                Width = 960,
                Height = 900,
            },

            // ------------------------------------------------ 当前环境
            Card("envCard", "当前环境", 24, 24, 900, 180),
            Grid("envGrid", 12, 40, 876, 134,
                ("计算机", "text:MachineName"),
                ("用户", "text:UserName"),
                ("系统", "text:OsDescription"),
                ("架构 / CPU", "text:Architecture"),
                ("内存", "text:MemoryText")),

            // ------------------------------------------------ 软件检测
            // 这张卡片扫的是「软件清单」——已装 / 未装 / 不支持 / 未知——而不是硬件或运行库。
            // 旧名「环境扫描」和环境页那个真扫运行库与驱动的按钮撞名，用户分不清两个「扫描环境」
            // 各自在干什么，于是问「环境按钮有啥用」。改名叫「软件检测」，一眼就知道它在数软件。
            // 按钮放在卡片标题行的右侧，而不是正文的第一行：它原先在纵向 Stack 的最上面，
            // 于是孤零零地悬在进度条与状态文字之上——既不在标题旁，也不在底部操作区，
            // 看上去像是掉在卡片上的一块东西。标题相应收窄，两不重叠。
            Card("scanCard", "软件检测", 24, 214, 900, 224, titleWidth: 708),
            new()
            {
                Id = "scanButton",
                Type = LayoutNodeType.Button,
                Text = "扫描软件",
                X = 728,

                // 与标题行垂直居中对齐（标题 10..34，按钮 32 高 → 6..38），
                // 底边正好停在正文顶部之前，不与进度条贴在一起。
                Y = 6,
                Width = 160,
                Height = 32,
                Binding = "action:scan",
            },
            new()
            {
                Id = "scanStack",
                Type = LayoutNodeType.Stack,
                X = 12,
                Y = 40,
                Width = 876,
                Height = 176,
                Properties = new Dictionary<string, string>
                {
                    ["orientation"] = "vertical",
                    ["padding"] = "0",
                    ["spacing"] = "8",
                },
                Children = new LayoutNode[]
                {
                    new()
                    {
                        Id = "scanProgress",
                        Type = LayoutNodeType.Progress,
                        Width = 300,
                        Height = 18,
                        Binding = "value:ScanPercent",
                    },
                    new()
                    {
                        Id = "scanStatus",
                        Type = LayoutNodeType.Text,
                        Width = 520,
                        Height = 20,
                        Binding = "text:ScanProgressText",
                    },
                    new()
                    {
                        Id = "scanCommentary",
                        Type = LayoutNodeType.Text,
                        Width = 520,
                        Height = 24,
                        Binding = "text:ScanCommentary",
                    },
                    new()
                    {
                        Id = "scanSummary",
                        Type = LayoutNodeType.Text,
                        Width = 520,
                        Height = 48,
                        Binding = "text:ScanSummary",
                    },
                },
            },

            // ------------------------------------------------ 目录与环境统计
            Card("statsCard", "目录与环境统计", 24, 448, 900, 180),
            Grid("statsGrid", 12, 40, 876, 134,
                ("软件条目", "text:CatalogCount"),
                ("一级分类", "text:CategoryCount"),
                ("皮肤 / 资源表", "text:AssetSheetCount"),
                ("云端平台", "text:CloudProviderCount"),
                ("目录校验", "text:CatalogHealth")),

            // ------------------------------------------------ 分类分布
            Card("categoryCard", "分类分布", 24, 638, 900, 180),
            new()
            {
                Id = "categoryList",
                Type = LayoutNodeType.List,
                X = 12,
                Y = 40,
                Width = 876,
                Height = 134,
                Binding = "items:CategoryBreakdown",
            },
            },

            // Each card's contents belong inside the card. Authored flat so the
            // document is readable, folded here so the flow layout finds them.
            ("envCard", "envGrid"),
            ("scanCard", "scanStack"),
            ("scanCard", "scanButton"),
            ("statsCard", "statsGrid"),
            ("categoryCard", "categoryList"))
    };

    /// <summary>
    /// Moves named top-level nodes inside their card.
    ///
    /// A top-level node is drawn at its own absolute position; a child is drawn
    /// relative to its parent. These contents were siblings of their cards, so every
    /// one of them landed on top of the first card at (12,40) and the page looked
    /// broken. Folding keeps the document readable while giving the layout what it
    /// has to have.
    /// </summary>
    private static LayoutNode[] FoldContents(
        LayoutNode[] nodes,
        params (string Card, string Content)[] pairs)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var moved = pairs.Select(pair => pair.Content).ToHashSet(StringComparer.Ordinal);

        return nodes
            .Where(node => !moved.Contains(node.Id))
            .Select(node =>
            {
                // Every pair that names this card, not just the first one. Returning on the first match
                // looked harmless while a card had a single folded node, and silently dropped the second
                // the moment one gained a title-row action: the node left the top level and was never
                // attached to anything.
                var children = node.Children;

                foreach (var (card, content) in pairs)
                {
                    if (string.Equals(node.Id, card, StringComparison.Ordinal)
                        && byId.TryGetValue(content, out var child))
                    {
                        children = children.Append(child).ToArray();
                    }
                }

                return ReferenceEquals(children, node.Children) ? node : node with { Children = children };
            })
            .ToArray();
    }

    /// <summary>
    /// A card: a titled container frame.
    ///
    /// <para>
    /// <paramref name="titleWidth"/> narrows the title when something else lives on the same row — an
    /// action button at the card's top-right, for instance. Left at the full width the title runs under
    /// that button, and the editor would show two nodes overlapping.
    /// </para>
    /// </summary>
    private static LayoutNode Card(
        string id,
        string title,
        double x,
        double y,
        double width,
        double height,
        double? titleWidth = null) => new()
    {
        Id = id,
        Type = LayoutNodeType.Container,
        Text = title,
        X = x,
        Y = y,
        Width = width,
        Height = height,
        Children = new LayoutNode[]
        {
            new()
            {
                Id = id + "Title",
                Type = LayoutNodeType.Text,
                Text = title,
                X = 12,
                Y = 10,
                Width = titleWidth ?? width - 24,
                Height = 24,
                FontSize = 15,
            },
        },
    };

    /// <summary>A two-column label/value grid.</summary>
    private static LayoutNode Grid(
        string id,
        double x,
        double y,
        double width,
        double height,
        params (string Label, string Binding)[] rows)
    {
        var children = new List<LayoutNode>(rows.Length * 2);
        for (var i = 0; i < rows.Length; i++)
        {
            var (label, binding) = rows[i];

            // Ids come from an index and the binding name, never from the label:
            // a label like "架构 / CPU" is not a legal identifier.
            children.Add(new LayoutNode
            {
                Id = $"{id}_label{i}",
                Type = LayoutNodeType.Text,
                Text = label,

                // Half the grid minus the gap, so a wider card really is wider
                // instead of leaving the value column stranded in the middle.
                Width = Math.Max(80, (width / 2) - 12),
                Height = 22,
                Color = Muted,
            });

            children.Add(new LayoutNode
            {
                Id = $"{id}_{binding[(binding.IndexOf(':') + 1)..]}",

                Type = LayoutNodeType.Text,
                Width = Math.Max(80, (width / 2) - 12),
                Height = 22,
                Binding = binding,
            });
        }

        return new LayoutNode
        {
            Id = id,
            Type = LayoutNodeType.Grid,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Properties = new Dictionary<string, string>
            {
                ["columns"] = "2",
                ["padding"] = "2",
                ["spacing"] = "4",
            },
            Children = children,
        };
    }
}
