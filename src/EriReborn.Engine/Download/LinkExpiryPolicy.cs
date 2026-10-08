namespace EriReborn.Engine.Download;

/// <summary>
/// Decides whether a failed download is worth re-resolving the source for.
///
/// <para>
/// A cloud share's direct link is minted per session and stops working on its
/// own. When that happens the server answers 401/403/404/410 — the same answers
/// it gives for a file that is genuinely gone. The two cannot be told apart from
/// the status code alone, so this is a judgement call, and the caller is expected
/// to act on it <b>once</b>: refreshing repeatedly would turn a deleted file into
/// an infinite loop against someone else's server.
/// </para>
/// </summary>
public static class LinkExpiryPolicy
{
    /// <summary>
    /// True when the answer suggests a stale link. Deliberately says "stale",
    /// not "expired": the evidence does not support the stronger claim.
    /// </summary>
    public static bool LooksLikeStaleLink(DownloadState state, int? statusCode)
    {
        // These are conclusions about the bytes, not about the link. Refreshing
        // would download the same wrong bytes again.
        if (state is DownloadState.HashMismatch
            or DownloadState.SizeMismatch
            or DownloadState.Cancelled
            or DownloadState.ServedFromCache)
        {
            return false;
        }

        return statusCode is 401 or 403 or 404 or 410;
    }

    /// <summary>Human wording for the log, so a refresh is never silent.</summary>
    public static string Describe(int? statusCode)
        => statusCode is null
            ? "服务器没有给出状态码"
            : statusCode switch
            {
                401 => "HTTP 401（需要重新授权）",
                403 => "HTTP 403（链接已失效或拒绝访问）",
                404 => "HTTP 404（链接不存在或文件已移除）",
                410 => "HTTP 410（链接已永久失效）",
                _ => $"HTTP {statusCode}",
            };
}
