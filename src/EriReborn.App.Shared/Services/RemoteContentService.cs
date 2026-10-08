using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.App.Shared.Services;

public sealed record RemoteFetchResult(bool Success, string? Content, bool FromCache, string Message);

/// <summary>
/// Fetches remote JSON (announcements, blog) with an on-disk cache so a
/// network failure never blocks the application (spec 62/63).
/// </summary>
public sealed class RemoteContentService(INetworkService network, AppPaths paths, IAppLogger log)
{
    private readonly INetworkService _network = network;
    private readonly IAppLogger _log = log;
    private readonly string _cacheDirectory = Path.Combine(paths.UserDataDirectory, "cache");

    public async Task<RemoteFetchResult> FetchAsync(string? url, string cacheKey, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var cacheFile = Path.Combine(_cacheDirectory, cacheKey + ".json");

        if (string.IsNullOrWhiteSpace(url))
        {
            return File.Exists(cacheFile)
                ? new RemoteFetchResult(true, await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false), true, "未配置远程地址，显示缓存内容。")
                : new RemoteFetchResult(false, null, false, "未配置远程地址，且本地没有缓存。");
        }

        try
        {
            var content = await _network.Client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(cacheFile, content, cancellationToken).ConfigureAwait(false);
            return new RemoteFetchResult(true, content, false, "已从网络更新。");
        }
        catch (Exception ex)
        {
            _log.Warn("remote.fetch", $"Failed to fetch '{url}': {ex.Message}");
            if (File.Exists(cacheFile))
            {
                var cached = await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);
                return new RemoteFetchResult(true, cached, true, "网络不可用，显示缓存内容。");
            }

            return new RemoteFetchResult(false, null, false, $"网络不可用且无缓存：{ex.Message}");
        }
    }
}
