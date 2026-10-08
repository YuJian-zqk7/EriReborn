namespace EriReborn.Extension;

/// <summary>Outcome of ordering extensions by their declared dependencies.</summary>
public sealed record ExtensionResolveResult(
    IReadOnlyList<string> LoadOrder,
    IReadOnlyDictionary<string, string> Blocked)
{
    public bool IsBlocked(string extensionId) => Blocked.ContainsKey(extensionId);
}

/// <summary>
/// Orders extensions so a dependency always loads before whatever needs it, and
/// decides which ones cannot load at all (spec 34/35).
///
/// Declared dependencies used to be parsed and displayed but never consulted,
/// so an extension with an absent dependency was loaded anyway and failed later
/// at run time with no explanation.
/// </summary>
public static class ExtensionDependencyResolver
{
    /// <param name="manifests">Every extension that passed validation.</param>
    /// <param name="unavailable">
    /// Ids that exist but must not load (for example, switched off by the user).
    /// They satisfy "is declared" but not "is loadable".
    /// </param>
    public static ExtensionResolveResult Resolve(
        IReadOnlyCollection<ExtensionManifest> manifests,
        IReadOnlyCollection<string>? unavailable = null)
    {
        var off = unavailable is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(unavailable, StringComparer.Ordinal);

        var byId = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            byId[manifest.Id] = manifest;
        }

        var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        var state = new Dictionary<string, VisitState>(StringComparer.Ordinal);

        void Block(string id, string reason)
        {
            // Keep the first explanation: it is the most specific one.
            if (!blocked.ContainsKey(id))
            {
                blocked[id] = reason;
            }
        }

        void Visit(string id, List<string> path)
        {
            if (state.TryGetValue(id, out var current))
            {
                if (current == VisitState.Visiting)
                {
                    var start = path.IndexOf(id);
                    var cycle = string.Join(" → ", path.Skip(start).Append(id));
                    foreach (var member in path.Skip(start))
                    {
                        Block(member, $"检测到循环依赖：{cycle}");
                    }
                }

                return;
            }

            state[id] = VisitState.Visiting;
            path.Add(id);

            if (!byId.TryGetValue(id, out var manifest))
            {
                path.RemoveAt(path.Count - 1);
                state[id] = VisitState.Done;
                return;
            }

            foreach (var dependency in manifest.Dependencies.Distinct(StringComparer.Ordinal))
            {
                if (string.Equals(dependency, id, StringComparison.Ordinal))
                {
                    Block(id, "该扩展依赖自身。");
                    continue;
                }

                if (!byId.ContainsKey(dependency))
                {
                    Block(id, off.Contains(dependency)
                        ? $"依赖的扩展 '{dependency}' 已被禁用。"
                        : $"缺少依赖扩展 '{dependency}'。");

                    if (!off.Contains(dependency))
                    {
                        // Report it as present-but-unavailable too, so a missing
                        // dependency is not silently "unknown".
                        Block(dependency, "该扩展并未安装。");
                    }

                    continue;
                }

                if (off.Contains(dependency))
                {
                    Block(id, $"依赖的扩展 '{dependency}' 已被禁用。");
                    continue;
                }

                Visit(dependency, path);
                if (blocked.ContainsKey(dependency))
                {
                    Block(id, $"依赖的扩展 '{dependency}' 无法加载。");
                }
            }

            path.RemoveAt(path.Count - 1);
            state[id] = VisitState.Done;

            if (!blocked.ContainsKey(id) && !off.Contains(id))
            {
                order.Add(id);
            }
        }

        foreach (var id in byId.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            Visit(id, new List<string>());
        }

        return new ExtensionResolveResult(order, blocked);
    }

    private enum VisitState
    {
        Visiting,
        Done,
    }
}
