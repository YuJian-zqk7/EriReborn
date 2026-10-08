using EriReborn.Core.Logging;

namespace EriReborn.Extension;

/// <summary>
/// Wraps whatever host the application supplied and refuses the capabilities an
/// extension did not declare.
///
/// <para>
/// The wrap happens in one place — when the assembly is loaded — so every host
/// (the application, the marketplace, a test) is gated the same way. Gating inside
/// each host would have meant four copies and at least one of them forgetting.
/// </para>
///
/// <para>
/// A refusal is logged and the call returns empty; it is never thrown into
/// extension code, because an extension that forgot to declare something must not
/// be able to take the application down.
/// </para>
/// </summary>
public sealed class PermissionCheckedExtensionHost : IExtensionHost
{
    private readonly IExtensionHost _inner;
    private readonly ExtensionPermissions _permissions;
    private readonly IAppLogger _log;
    private readonly string _extensionId;

    public PermissionCheckedExtensionHost(
        IExtensionHost inner,
        ExtensionPermissions permissions,
        IAppLogger log,
        string extensionId)
    {
        _inner = inner;
        _permissions = permissions;
        _log = log;
        _extensionId = extensionId;
    }

    public void Log(string level, string message) => _inner.Log(level, message);

    public EriReborn.Platform.Abstractions.INetworkService? Network
    {
        get
        {
            if (_permissions.Allows(ExtensionPermissions.NetworkAccess))
            {
                return _inner.Network;
            }

            Deny(nameof(Network), ExtensionPermissions.NetworkAccess);
            return null;
        }
    }

    public EriReborn.Platform.Abstractions.ICredentialStore? Credentials
    {
        get
        {
            if (_permissions.Allows(ExtensionPermissions.CredentialsAccess))
            {
                return _inner.Credentials;
            }

            Deny(nameof(Credentials), ExtensionPermissions.CredentialsAccess);
            return null;
        }
    }

    public EriReborn.Platform.Abstractions.IProcessService? Processes
    {
        get
        {
            if (_permissions.Allows(ExtensionPermissions.ProcessAccess))
            {
                return _inner.Processes;
            }

            Deny(nameof(Processes), ExtensionPermissions.ProcessAccess);
            return null;
        }
    }

    // Logging is never gated: an extension that cannot report its own failure is a
    // failure the user cannot see.
    public EriReborn.Core.Logging.IAppLogger? Logger => _inner.Logger;

    public void RegisterCloudProvider(EriReborn.Cloud.ICloudProvider provider)
    {
        if (!_permissions.Allows(ExtensionPermissions.CloudContribute))
        {
            Deny(nameof(RegisterCloudProvider), ExtensionPermissions.CloudContribute);
            return;
        }

        _inner.RegisterCloudProvider(provider);
    }

    public void RegisterDownloadEngine(EriReborn.Engine.Download.IDownloadEngine engine)
    {
        if (!_permissions.Allows(ExtensionPermissions.DownloaderContribute))
        {
            Deny(nameof(RegisterDownloadEngine), ExtensionPermissions.DownloaderContribute);
            return;
        }

        _inner.RegisterDownloadEngine(engine);
    }

    public void ContributeSoftware(IEnumerable<EriReborn.Core.Domain.SoftwareDefinition> items)
    {
        if (!_permissions.Allows(ExtensionPermissions.CatalogContribute))
        {
            Deny(nameof(ContributeSoftware), ExtensionPermissions.CatalogContribute);
            return;
        }

        _inner.ContributeSoftware(items);
    }

    public void RegisterPage(IExtensionPage page)
    {
        if (!_permissions.Allows(ExtensionPermissions.UiContribute))
        {
            Deny(nameof(RegisterPage), ExtensionPermissions.UiContribute);
            return;
        }

        // Extensions call the one-argument form; the wrapper is the only place that knows
        // which extension is calling, so the owner tag is attached here — otherwise a
        // later disable could not take the page back.
        _inner.RegisterPage(page, _extensionId);
    }

    public void RegisterPage(IExtensionPage page, string? ownerExtensionId)
    {
        if (!_permissions.Allows(ExtensionPermissions.UiContribute))
        {
            Deny(nameof(RegisterPage), ExtensionPermissions.UiContribute);
            return;
        }

        // The wrapper knows which extension is calling; the page is tagged with it so a
        // later disable/unload can take the page back.
        _inner.RegisterPage(page, ownerExtensionId ?? _extensionId);
    }

    public void RemovePages(string ownerExtensionId) => _inner.RemovePages(ownerExtensionId);

    public Task<string?> PickFileAsync(string title, string filter)
    {
        if (!_permissions.Allows(ExtensionPermissions.UiContribute))
        {
            Deny(nameof(PickFileAsync), ExtensionPermissions.UiContribute);
            return Task.FromResult<string?>(null);
        }

        return _inner.PickFileAsync(title, filter);
    }

    // The directory is the extension's own sandbox under the host's user data — the
    // same reach settings.read/write already grants, so it follows those gates
    // instead of inventing a permission nobody has declared yet.
    public string? GetDataDirectory(string extensionId)
    {
        if (!_permissions.Allows(ExtensionPermissions.SettingsWrite))
        {
            Deny(nameof(GetDataDirectory), ExtensionPermissions.SettingsWrite);
            return null;
        }

        return _inner.GetDataDirectory(extensionId);
    }

    public bool OpenReaderWindow(EriReborn.Extension.Reader.IReaderSession session)
    {
        if (!_permissions.Allows(ExtensionPermissions.UiContribute))
        {
            Deny(nameof(OpenReaderWindow), ExtensionPermissions.UiContribute);
            return false;
        }

        return _inner.OpenReaderWindow(session);
    }

    public string GetSetting(string key, string? fallback = null)
    {
        if (!_permissions.Allows(ExtensionPermissions.SettingsRead))
        {
            Deny(nameof(GetSetting), ExtensionPermissions.SettingsRead);
            return fallback ?? string.Empty;
        }

        return _inner.GetSetting(key, fallback);
    }

    public void SetSetting(string key, string value)
    {
        if (!_permissions.Allows(ExtensionPermissions.SettingsWrite))
        {
            Deny(nameof(SetSetting), ExtensionPermissions.SettingsWrite);
            return;
        }

        _inner.SetSetting(key, value);
    }

    private void Deny(string action, string permission)
        => _log.Warn(
            "extension.permission_denied",
            $"扩展 '{_extensionId}' 调用了 {action}，但没有声明 '{permission}'。已拒绝。");
}
