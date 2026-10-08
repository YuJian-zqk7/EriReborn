using System.Security.Cryptography;
using System.Text;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Windows;

/// <summary>
/// Credential store backed by Windows DPAPI with CurrentUser scope
/// (spec 31/59). Secrets never touch a plain-text configuration file, and the
/// store reports machine/user binding so a moved profile is explained rather
/// than silently failing.
///
/// <para>
/// The configured directory is only a preference: on locked-down or roaming profiles
/// <c>%AppData%\Roaming</c> can be read-only for the process, and a store that cannot write
/// used to end the launcher (the command that saves runs as async void). So the store probes
/// for a writable directory up front and falls back to one next to the executable, which is
/// always writable for a per-user install.
/// </para>
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EriReborn.v3.credentials");

    private readonly string _preferredRoot;
    private readonly IAppLogger _log;
    private string? _activeRoot;

    public DpapiCredentialStore(string rootDirectory, IAppLogger log)
    {
        _preferredRoot = rootDirectory;
        _log = log;
        _ = ActiveRoot();
    }

    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>The directory actually in use, for diagnostics and the settings surface.</summary>
    public string RootDirectory => ActiveRoot();

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Task.FromResult<string?>(null);
        }

        var file = FindFile(key);
        if (file is null || !File.Exists(file))
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(file);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
        }
        catch (CryptographicException ex)
        {
            _log.Warn("credentials.unprotect", $"Stored secret for '{key}' cannot be read on this user/machine. {ex.Message}");
            return Task.FromResult<string?>(null);
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            Entropy,
            DataProtectionScope.CurrentUser);

        try
        {
            await WriteAsync(Path.Combine(ActiveRoot(), FileNameFor(key)), protectedBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            // The probe said writable but the real write was refused; the fallback is our last
            // chance to keep the credential instead of failing the user's sign-in.
            var fallback = FallbackRoot();
            if (string.Equals(_activeRoot, fallback, StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }

            _activeRoot = fallback;
            _log.Warn("credentials.fallback", $"Write to '{_preferredRoot}' was denied; using '{fallback}' instead.");
            await WriteAsync(Path.Combine(fallback, FileNameFor(key)), protectedBytes, cancellationToken).ConfigureAwait(false);
        }

        _log.Info("credentials.set", $"Stored credential '{key}' in '{ActiveRoot()}' (DPAPI, CurrentUser).");
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var file = FindFile(key);
        if (file is null || !File.Exists(file))
        {
            return Task.FromResult(false);
        }

        File.Delete(file);
        _log.Info("credentials.remove", $"Removed credential '{key}'.");
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.bin"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(name) && !keys.Contains(name))
                {
                    keys.Add(name);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads look in both roots, so credentials keep working after a fallback switch.</summary>
    private string? FindFile(string key)
    {
        var name = FileNameFor(key);
        foreach (var root in Roots())
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> Roots()
    {
        yield return ActiveRoot();
        var fallback = FallbackRoot();
        if (!string.Equals(fallback, _activeRoot, StringComparison.OrdinalIgnoreCase))
        {
            yield return fallback;
        }
    }

    private string ActiveRoot()
    {
        if (_activeRoot is not null)
        {
            return _activeRoot;
        }

        if (IsWritable(_preferredRoot))
        {
            _activeRoot = _preferredRoot;
            _log.Info("credentials.root", $"Credential store root: '{_activeRoot}'.");
            return _activeRoot;
        }

        var fallback = FallbackRoot();
        if (IsWritable(fallback))
        {
            _log.Warn("credentials.fallback",
                $"'{_preferredRoot}' is not writable for this process; using '{fallback}' instead.");
            _activeRoot = fallback;
            return _activeRoot;
        }

        // Neither works: keep the preferred path so the real error surfaces to the user.
        _log.Warn("credentials.root", $"No writable credential directory found; falling back to '{_preferredRoot}'.");
        _activeRoot = _preferredRoot;
        return _activeRoot;
    }

    private static string FallbackRoot() => Path.Combine(AppContext.BaseDirectory, "EriReborn-credentials");

    /// <summary>
    /// A probe, not a guess: directory creation can succeed while file creation is denied, which
    /// is exactly what happened before, so this writes and deletes a real byte.
    /// </summary>
    private static bool IsWritable(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, ".writable-probe");
            File.WriteAllBytes(probe, new byte[] { 1 });
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FileNameFor(string key)
    {
        // File names are hashed so arbitrary key characters cannot escape the directory.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return hash + ".bin";
    }
}
