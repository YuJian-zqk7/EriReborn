using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Android;

/// <summary>
/// Android updates reuse the install handoff because Android requires the user
/// to confirm every package installation (spec 23).
/// </summary>
public sealed class AndroidSoftwareUpdater(ISoftwareInstaller installer, IAppLogger log) : ISoftwareUpdater
{
    private readonly ISoftwareInstaller _installer = installer;
    private readonly IAppLogger _log = log;

    public async Task<UpdateResult> UpdateAsync(
        SoftwareDefinition software,
        SoftwareSource source,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var request = new InstallRequest
        {
            Software = software,
            Source = source,

        };

        var result = await _installer.InstallAsync(request, progress, cancellationToken).ConfigureAwait(false);

        var state = result.State switch
        {
            InstallState.AwaitingUserConfirmation => UpdateState.AwaitingUserConfirmation,
            InstallState.Succeeded => UpdateState.Updated,
            InstallState.AlreadyInstalled => UpdateState.UpToDate,
            InstallState.Unsupported => UpdateState.Unsupported,
            InstallState.SourceUnavailable => UpdateState.SourceUnavailable,
            InstallState.IntegrityFailed => UpdateState.IntegrityFailed,
            InstallState.Cancelled => UpdateState.Failed,
            _ => UpdateState.Failed,
        };

        _log.Info("update.android", $"Update for '{software.Id}' -> {state}.");
        return new UpdateResult(state, result.Message, ToVersion: source.Version ?? software.Version);
    }
}
