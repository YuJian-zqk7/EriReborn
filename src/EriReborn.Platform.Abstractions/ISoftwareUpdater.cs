using EriReborn.Core.Domain;

namespace EriReborn.Platform.Abstractions;

/// <summary>Update is modelled separately from install (spec 23).</summary>
public interface ISoftwareUpdater
{
    Task<UpdateResult> UpdateAsync(
        SoftwareDefinition software,
        SoftwareSource source,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
