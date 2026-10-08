namespace EriReborn.Layout;

/// <summary>
/// Structural edits over an immutable layout document. Every operation returns a
/// new document, so the editor can keep history and the UI never mutates state
/// it is rendering.
/// </summary>
public static class LayoutEditor
{
    public static LayoutNode? Find(LayoutDocument document, string id)
        => FindIn(document.Nodes, id);

    private static LayoutNode? FindIn(IReadOnlyList<LayoutNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Id, id, StringComparison.Ordinal))
            {
                return node;
            }

            var found = FindIn(node.Children, id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Absolute position of a node, accumulating parent offsets.</summary>
    public static (double X, double Y) AbsolutePosition(LayoutDocument document, string id)
    {
        var path = new List<LayoutNode>();
        if (!BuildPath(document.Nodes, id, path))
        {
            return (0, 0);
        }

        double x = 0;
        double y = 0;
        for (var i = 0; i < path.Count - 1; i++)
        {
            x += path[i].X;
            y += path[i].Y;
        }

        return (x + path[^1].X, y + path[^1].Y);
    }

    private static bool BuildPath(IReadOnlyList<LayoutNode> nodes, string id, List<LayoutNode> path)
    {
        foreach (var node in nodes)
        {
            path.Add(node);
            if (string.Equals(node.Id, id, StringComparison.Ordinal))
            {
                return true;
            }

            if (BuildPath(node.Children, id, path))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    public static LayoutDocument Update(LayoutDocument document, string id, Func<LayoutNode, LayoutNode> update)
    {
        var nodes = UpdateList(document.Nodes, id, update);

        // 'with' always copies, so short-circuit to keep no-op edits reference-equal.
        return ReferenceEquals(nodes, document.Nodes) ? document : document with { Nodes = nodes };
    }

    private static IReadOnlyList<LayoutNode> UpdateList(
        IReadOnlyList<LayoutNode> nodes,
        string id,
        Func<LayoutNode, LayoutNode> update)
    {
        var changed = false;
        var result = new List<LayoutNode>(nodes.Count);

        foreach (var node in nodes)
        {
            if (string.Equals(node.Id, id, StringComparison.Ordinal))
            {
                result.Add(update(node));
                changed = true;
                continue;
            }

            var children = UpdateList(node.Children, id, update);
            if (ReferenceEquals(children, node.Children))
            {
                result.Add(node);
                continue;
            }

            result.Add(node with { Children = children });
            changed = true;
        }

        return changed ? result : nodes;
    }

    public static LayoutDocument Move(LayoutDocument document, string id, double dx, double dy)
        => Update(document, id, n => n with
        {
            X = Math.Max(0, n.X + dx),
            Y = Math.Max(0, n.Y + dy),
        });

    public static LayoutDocument Resize(LayoutDocument document, string id, double dw, double dh)
        => Update(document, id, n => n with
        {
            Width = Math.Max(LayoutNode.MinSize, n.Width + dw),
            Height = Math.Max(LayoutNode.MinSize, n.Height + dh),
        });

    public static LayoutDocument SetVisible(LayoutDocument document, string id, bool visible)
        => Update(document, id, n => n with { Visible = visible });

    public static LayoutDocument SetText(LayoutDocument document, string id, string? text)
        => Update(document, id, n => n with { Text = text });

    /// <summary>
    /// Gives a node a new name (its id), keeping everything else.
    ///
    /// <para>
    /// The id is how a document refers to its own elements, so a rename is refused when the new name is
    /// blank or already taken: silently merging two nodes would lose one of them. An importer brings in
    /// machine-made names like <c>Text3</c>, and until now there was no way to give them the name the
    /// user actually thinks in.
    /// </para>
    /// </summary>
    public static LayoutDocument Rename(LayoutDocument document, string id, string newId)
    {
        var trimmed = newId?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || string.Equals(trimmed, id, StringComparison.Ordinal)
            || Find(document, trimmed) is not null)
        {
            return document;
        }

        return Update(document, id, n => n with { Id = trimmed });
    }

    public static LayoutDocument SetProperty(LayoutDocument document, string id, string key, string? value)
        => Update(document, id, n =>
        {
            var properties = new Dictionary<string, string>(n.Properties, StringComparer.Ordinal);
            if (string.IsNullOrEmpty(value))
            {
                properties.Remove(key);
            }
            else
            {
                properties[key] = value;
            }

            return n with { Properties = properties };
        });

    /// <summary>Adds a node under a parent, or at page root when parentId is null.</summary>
    public static LayoutDocument Add(LayoutDocument document, string? parentId, LayoutNode node)
    {
        if (string.IsNullOrEmpty(parentId))
        {
            return document with { Nodes = Append(document.Nodes, node) };
        }

        return Update(document, parentId, parent => parent with { Children = Append(parent.Children, node) });
    }

    private static IReadOnlyList<LayoutNode> Append(IReadOnlyList<LayoutNode> nodes, LayoutNode node)
    {
        var list = new List<LayoutNode>(nodes) { node };
        return list;
    }

    /// <summary>True when <paramref name="ancestorId"/> sits above <paramref name="id"/>.</summary>
    public static bool IsAncestorOf(LayoutDocument document, string ancestorId, string id)
    {
        var ancestor = Find(document, ancestorId);
        return ancestor is not null && FindIn(ancestor.Children, id) is not null;
    }

    /// <summary>
    /// Moves a node under a new parent so a Stack or Grid can arrange it. Moving
    /// a node into its own descendant is refused, because that would detach the
    /// subtree from the document.
    /// </summary>
    public static LayoutDocument Reparent(LayoutDocument document, string id, string newParentId)
    {
        if (string.Equals(id, newParentId, StringComparison.Ordinal))
        {
            return document;
        }

        var node = Find(document, id);
        if (node is null || FindIn(node.Children, newParentId) is not null)
        {
            return document;
        }

        var without = Remove(document, id);
        if (Find(without, newParentId) is null)
        {
            return document;
        }

        // The container decides the position, so the old absolute one is cleared
        // to avoid it reappearing if the node is later moved back out.
        return Update(without, newParentId, parent => parent with
        {
            Children = Append(parent.Children, node with { X = 0, Y = 0 }),
        });
    }

    /// <summary>Moves a node back out to the document root.</summary>
    public static LayoutDocument MoveToRoot(LayoutDocument document, string id)
    {
        if (document.Nodes.Any(n => string.Equals(n.Id, id, StringComparison.Ordinal)))
        {
            return document;
        }

        var node = Find(document, id);
        if (node is null)
        {
            return document;
        }

        var without = Remove(document, id);

        // Give it a visible position, otherwise it lands under whatever is at the
        // origin and looks lost.
        return without with { Nodes = Append(without.Nodes, node with { X = 40, Y = 40 }) };
    }

    public static LayoutDocument Remove(LayoutDocument document, string id)
        => Find(document, id) is null ? document : document with { Nodes = RemoveFrom(document.Nodes, id) };

    private static IReadOnlyList<LayoutNode> RemoveFrom(IReadOnlyList<LayoutNode> nodes, string id)
    {
        var result = new List<LayoutNode>(nodes.Count);
        foreach (var node in nodes)
        {
            if (string.Equals(node.Id, id, StringComparison.Ordinal))
            {
                continue;
            }

            var children = RemoveFrom(node.Children, id);
            result.Add(ReferenceEquals(children, node.Children) ? node : node with { Children = children });
        }

        return result;
    }

    /// <summary>Depth-first flattening, used by the editor canvas and validators.</summary>
    public static IEnumerable<LayoutNode> Flatten(LayoutDocument document) => Flatten(document.Nodes);

    private static IEnumerable<LayoutNode> Flatten(IReadOnlyList<LayoutNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>Generates an id that is unique within the document.</summary>
    public static string SuggestId(LayoutDocument document, LayoutNodeType type)
    {
        var stem = char.ToLowerInvariant(type.ToString()[0]) + type.ToString()[1..];
        for (var i = 1; i < 1000; i++)
        {
            var candidate = $"{stem}{i}";
            if (Find(document, candidate) is null)
            {
                return candidate;
            }
        }

        return $"{stem}{Guid.NewGuid():N}";
    }
}
