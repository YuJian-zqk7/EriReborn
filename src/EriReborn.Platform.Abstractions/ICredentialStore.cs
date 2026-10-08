namespace EriReborn.Platform.Abstractions;

/// <summary>
/// Secure credential storage (spec 31/59). Values must never land in plain
/// configuration files. When the platform has no secure store,
/// <see cref="IsAvailable"/> is false and reads return null explicitly.
/// </summary>
public interface ICredentialStore
{
    bool IsAvailable { get; }

    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default);
}
