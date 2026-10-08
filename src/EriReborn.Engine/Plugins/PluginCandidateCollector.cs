using EriReborn.Cloud;

namespace EriReborn.Engine.Plugins;

/// <summary>What a walk of a share produced, or why it could not be done.</summary>
public sealed record PluginCandidateCollection(PluginCandidateTree? Tree, string? Error)
{
    public bool Success => Tree is not null;

    public static PluginCandidateCollection Fail(string error) => new(null, error);
}

/// <summary>
/// Reads a share's real contents into a bounded list a model can be shown.
///
/// <para>
/// The walk goes through <see cref="ICloudProvider"/>, so no platform's folder semantics live here:
/// a platform that cannot open a folder inside a share says so, and the walk records that instead of
/// reporting a tree it did not see (spec 27/135).
/// </para>
///
/// <para>
/// It is bounded twice — by depth and by number of items — because a model prompt is not a file
/// browser. Both bounds are reported in the result rather than applied silently (spec 151).
/// </para>
/// </summary>
public static class PluginCandidateCollector
{
    /// <summary>
    /// How deep the walk goes. A share of installers is wide, not deep — but "not deep" was three
    /// levels, which cut the common shape of one (root / 分类 / 子类 / 版本 / 文件) one level above the
    /// files, so the model was shown folders and no installers at all.
    /// </summary>
    public const int DefaultMaxDepth = 5;

    /// <summary>
    /// How many items the model is shown.
    ///
    /// <para>
    /// A real share of installers runs to thousands of files; 600 still cut large shares, and the
    /// draft builder now asks for the entries in batches, so the bound can afford to sit higher.
    /// This is still a bound — a prompt is not a file browser, and both bounds stay reported rather
    /// than applied silently (spec 151).
    /// </para>
    /// </summary>
    public const int DefaultMaxItems = 2000;

    public static async Task<PluginCandidateCollection> CollectAsync(
        ICloudProvider provider,
        string shareUrl,
        CloudCredential? credential,
        int maxDepth = DefaultMaxDepth,
        int maxItems = DefaultMaxItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrWhiteSpace(shareUrl))
        {
            return PluginCandidateCollection.Fail("没有分享链接。");
        }

        var depthLimit = Math.Max(0, maxDepth);
        var itemLimit = Math.Max(1, maxItems);
        var items = new List<PluginCandidate>();
        var unreadable = new List<string>();
        var omitted = 0;

        // Breadth first, so a wide share is cut at its shallowest level rather than by which branch
        // happened to be walked first.
        var queue = new Queue<(string? FolderId, string Path, int Depth)>();
        queue.Enqueue((null, string.Empty, 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folderId, parentPath, depth) = queue.Dequeue();

            var listing = await provider
                .ListShareFolderAsync(shareUrl, folderId, credential, cancellationToken)
                .ConfigureAwait(false);

            if (!listing.Success)
            {
                // The root failing is the whole read failing. A subfolder failing is a partial tree,
                // and a partial tree that looks complete is worse than a reported failure.
                if (depth == 0)
                {
                    return PluginCandidateCollection.Fail(listing.Message ?? "无法读取这个分享的目录。");
                }

                unreadable.Add(string.IsNullOrEmpty(parentPath) ? "(分享根目录)" : parentPath);
                continue;
            }

            foreach (var entry in listing.Files)
            {
                var path = string.IsNullOrEmpty(parentPath) ? entry.Name : parentPath + "/" + entry.Name;

                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    omitted++;
                    continue;
                }

                if (items.Count >= itemLimit)
                {
                    omitted++;
                    continue;
                }

                // The platform's own id when it has one: it survives a rename or a move, which is
                // exactly what a name-based pointer does not (spec 30).
                var candidateId = string.IsNullOrWhiteSpace(entry.Id) ? path : entry.Id;

                items.Add(new PluginCandidate(candidateId, entry.Name, entry.IsFolder, entry.SizeBytes, path));

                if (entry.IsFolder && depth < depthLimit)
                {
                    queue.Enqueue((entry.Id, path, depth + 1));
                }
            }
        }

        return new PluginCandidateCollection(
            new PluginCandidateTree(provider.Id, shareUrl, items, omitted, unreadable),
            null);
    }
}
