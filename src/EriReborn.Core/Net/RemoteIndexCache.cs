using EriReborn.Core.Logging;

namespace EriReborn.Core.Net;

/// <summary>The outcome of fetching an index, already parsed.</summary>
public sealed record RemoteIndexFetch<T>(
    bool Success,
    T? Value,
    bool FromCache,
    string Message)
    where T : class;

/// <summary>
/// Fetches a remote index and keeps a copy on disk.
///
/// <para>
/// A network failure must never turn into an empty page: the cached copy is served
/// instead, and the message says so (spec 122/124). Shared by every marketplace,
/// because the caching rule is the same one for all of them — only the shape of the
/// document differs.
/// </para>
///
/// <para>
/// <b>The caller supplies the parse step, and this class runs it before writing the
/// cache.</b> An earlier version wrote first and returned raw text, which meant a
/// garbage or hostile response replaced a good cache with itself, and the next
/// offline launch had nothing to fall back to. It also meant a malformed document
/// surfaced as an exception from the one code path whose whole job was to degrade
/// gracefully. Neither is possible now, because there is no step here that can
/// produce a value nobody has checked.
/// </para>
/// </summary>
public static class RemoteIndexCache
{
    /// <param name="what">What is being fetched, for the message the user reads.</param>
    /// <param name="parse">Turns the text into the index; throwing means "not usable".</param>
    /// <remarks>
    /// Takes an HttpClient rather than the platform's network service, so this
    /// stays in Core: Core does not depend on the platform layer, and a cache is
    /// not a platform concern anyway.
    /// </remarks>
    public static async Task<RemoteIndexFetch<T>> FetchAsync<T>(
        HttpClient client,
        IAppLogger log,
        string? indexUrl,
        string cacheFile,
        string what,
        Func<string, T> parse,
        string? configurationHint = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(parse);

        var noUrl = string.IsNullOrWhiteSpace(configurationHint)
            ? $"未配置{what}索引地址。"
            : $"未配置{what}索引地址（{configurationHint}）。";

        if (string.IsNullOrWhiteSpace(indexUrl))
        {
            return await TryReadCacheAsync(parse, log, cacheFile, noUrl, cancellationToken)
                       .ConfigureAwait(false)
                   ?? new RemoteIndexFetch<T>(false, null, false, noUrl);
        }

        string json;
        try
        {
            json = await client.GetStringAsync(indexUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Kept apart from a parse failure on purpose: telling someone their
            // network is down when the document is simply malformed sends them to
            // fix the wrong thing.
            log.Warn("index.fetch", $"{what} index fetch failed: {ex.Message}");

            return await TryReadCacheAsync(parse, log, cacheFile, $"网络不可用：{ex.Message}", cancellationToken)
                       .ConfigureAwait(false)
                   ?? new RemoteIndexFetch<T>(false, null, false, $"{what}索引不可用且无缓存：{ex.Message}");
        }

        T index;
        try
        {
            index = parse(json);
        }
        catch (Exception ex)
        {
            log.Warn("index.parse", $"{what} index could not be parsed: {ex.Message}");

            return await TryReadCacheAsync(parse, log, cacheFile, $"{what}索引内容无法解析：{ex.Message}", cancellationToken)
                       .ConfigureAwait(false)
                   ?? new RemoteIndexFetch<T>(false, null, false, $"{what}索引内容无法解析：{ex.Message}");
        }

        // Written only after it parsed, so a bad response cannot destroy the copy the
        // next offline launch depends on.
        try
        {
            var directory = Path.GetDirectoryName(cacheFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(cacheFile, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A cache we cannot write is a smaller problem than an index we cannot
            // show, so the index still goes to the caller.
            log.Warn("index.cache", $"{what} index could not be cached: {ex.Message}");
        }

        log.Info("index.fetch", $"Fetched the {what} index from '{indexUrl}'.");
        return new RemoteIndexFetch<T>(true, index, false, $"已更新{what}索引。");
    }

    /// <summary>
    /// Reads and parses the cached copy, and <b>never throws</b>.
    /// </summary>
    /// <returns>Null when there is no cache at all, so the caller words its own message.</returns>
    private static async Task<RemoteIndexFetch<T>?> TryReadCacheAsync<T>(
        Func<string, T> parse,
        IAppLogger log,
        string cacheFile,
        string reason,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(cacheFile))
        {
            return null;
        }

        try
        {
            var cached = await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);

            return new RemoteIndexFetch<T>(true, parse(cached), true, $"{reason}显示本地缓存。");
        }
        catch (Exception ex)
        {
            // Any failure to get an index out of the cache reads the same way to the
            // user: there is a cache, and it did not yield an index.
            log.Warn("index.cache", $"Cached {cacheFile} could not be read: {ex.Message}");

            return new RemoteIndexFetch<T>(false, null, false, $"{reason}但本地缓存无法解析：{ex.Message}");
        }
    }
}
