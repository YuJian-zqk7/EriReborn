using System.Security.Cryptography;
using System.Text;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace EriReborn.Platform.Android;

/// <summary>
/// Credential store backed by the Android Keystore: the AES key never leaves
/// secure hardware/keystore, and ciphertext is written into the app sandbox
/// (spec 31/59). Losing the keystore entry surfaces as a clear "not readable"
/// result instead of a silent failure.
/// </summary>
public sealed class AndroidCredentialStore : ICredentialStore
{
    private const string KeyAlias = "EriReborn.Credentials";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int GcmTagBits = 128;
    private const int IvBytes = 12;

    private readonly string _root;
    private readonly IAppLogger _log;

    public AndroidCredentialStore(string rootDirectory, IAppLogger log)
    {
        _root = rootDirectory;
        _log = log;
        Directory.CreateDirectory(_root);
    }

    public bool IsAvailable => OperatingSystem.IsAndroid();

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var file = PathFor(key);
        if (!IsAvailable || !File.Exists(file))
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            var payload = File.ReadAllBytes(file);
            if (payload.Length <= IvBytes)
            {
                return Task.FromResult<string?>(null);
            }

            var iv = payload[..IvBytes];
            var cipherText = payload[IvBytes..];

            var cipher = Cipher.GetInstance(Transformation)
                ?? throw new InvalidOperationException($"Cipher '{Transformation}' is unavailable.");

            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(GcmTagBits, iv));
            var plain = cipher.DoFinal(cipherText);
            return Task.FromResult<string?>(plain is null ? null : Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex)
        {
            _log.Warn("credentials.unprotect", $"Stored secret for '{key}' cannot be read on this device. {ex.Message}");
            return Task.FromResult<string?>(null);
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("The Android Keystore is only available on Android.");
        }

        var cipher = Cipher.GetInstance(Transformation)
            ?? throw new InvalidOperationException($"Cipher '{Transformation}' is unavailable.");

        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, GetOrCreateKey());
        var cipherText = cipher.DoFinal(Encoding.UTF8.GetBytes(value)) ?? Array.Empty<byte>();
        var iv = cipher.GetIV() ?? Array.Empty<byte>();

        var payload = new byte[iv.Length + cipherText.Length];
        iv.CopyTo(payload, 0);
        cipherText.CopyTo(payload, iv.Length);

        await File.WriteAllBytesAsync(PathFor(key), payload, cancellationToken).ConfigureAwait(false);
        _log.Info("credentials.set", $"Stored credential '{key}' (Android Keystore, AES-GCM).");
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var file = PathFor(key);
        if (!File.Exists(file))
        {
            return Task.FromResult(false);
        }

        File.Delete(file);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        IReadOnlyList<string> keys = Directory.EnumerateFiles(_root, "*.bin")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();
        return Task.FromResult(keys);
    }

    private static IKey GetOrCreateKey()
    {
        var keyStore = KeyStore.GetInstance("AndroidKeyStore")
            ?? throw new InvalidOperationException("The Android Keystore is unavailable.");

        keyStore.Load(null);

        if (keyStore.ContainsAlias(KeyAlias) && keyStore.GetKey(KeyAlias, null) is IKey existing)
        {
            return existing;
        }

        var generator = Javax.Crypto.KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")
            ?? throw new InvalidOperationException("Unable to create an Android Keystore AES generator.");
        var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetRandomizedEncryptionRequired(true)
            .Build();

        generator.Init(spec);
        return generator.GenerateKey()
            ?? throw new InvalidOperationException("The Android Keystore did not return a key.");
    }

    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_root, hash + ".bin");
    }
}
