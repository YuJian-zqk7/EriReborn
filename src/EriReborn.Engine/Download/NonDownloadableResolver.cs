using EriReborn.Core.Domain;

namespace EriReborn.Engine.Download;

/// <summary>
/// Sources that legitimately need no download route.
///
/// winget fetches its own packages; a local source is already on disk. Reporting
/// these as <see cref="ResolveStatus.NotADownload"/> keeps them from being
/// mistaken for a broken resolver — the distinction the whole status enum exists
/// for.
/// </summary>
public sealed class NonDownloadableResolver : IDownloadResolver
{
    public string Id => "not-a-download";

    public bool CanResolve(SoftwareSource source)
        => source.Kind is SourceKind.Winget or SourceKind.Local or SourceKind.Manual;

    public Task<ResolveOutcome> ResolveAsync(SoftwareSource source, CancellationToken cancellationToken = default)
        => Task.FromResult(ResolveOutcome.NotADownload(source.Kind switch
        {
            SourceKind.Winget => "winget 来源由 winget 自行获取，不经过下载引擎。",
            SourceKind.Local => "本地来源已经在磁盘上，无需下载。",
            _ => "该来源由用户手动处理，不经过下载引擎。",
        }));
}
