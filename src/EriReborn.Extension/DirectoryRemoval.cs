namespace EriReborn.Extension;

/// <summary>
/// Directory deletion helpers. A loaded extension keeps its assembly mapped, and
/// a collectible load context only releases it after a collection, so deletion is
/// retried with collections in between.
/// </summary>
internal static class DirectoryRemoval
{
    /// <summary>Deletes a directory, collecting garbage between attempts.</summary>
    public static bool TryDelete(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return !Directory.Exists(path);
            }
            catch (IOException)
            {
                // Still mapped; collect and retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Still mapped; collect and retry.
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Thread.Sleep(150);
        }

        return !Directory.Exists(path);
    }

    public static string PendingMarker(string extensionsRoot, string extensionId)
        => Path.Combine(extensionsRoot, extensionId + ".pending-delete");
}
