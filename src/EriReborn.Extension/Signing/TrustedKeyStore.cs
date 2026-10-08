using System.Text.Json;
using System.Text.Json.Serialization;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;

namespace EriReborn.Extension.Signing;

/// <summary>
/// A public key the application chooses to trust. Trust comes from this store,
/// never from the package itself.
/// </summary>
public sealed record TrustedKey
{
    public required string KeyId { get; init; }

    /// <summary>"ecdsa-p256-sha256" (default) or "rsa-sha256".</summary>
    public string Algorithm { get; init; } = "ecdsa-p256-sha256";

    /// <summary>Base64 SubjectPublicKeyInfo (DER), or a PEM block.</summary>
    public required string PublicKey { get; init; }

    /// <summary>The trust this key confers on anything it signs.</summary>
    public SoftwareTrust Trust { get; init; } = SoftwareTrust.Verified;

    public string? Publisher { get; init; }
}

/// <summary>
/// Loads the shipped trust anchors. A manifest's own "trust" field is only a
/// claim; the level a package actually receives is decided by which key signed
/// it (spec 35).
/// </summary>
public sealed class TrustedKeyStore(IAppLogger log)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,

        // The file spells trust as a name ("Official"), not a number. Without
        // this the whole store fails to load and silently trusts nothing.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, TrustedKey> _keys = new(StringComparer.Ordinal);

    public int Count => _keys.Count;

    public IReadOnlyCollection<TrustedKey> Keys => _keys.Values;

    public TrustedKey? Find(string keyId) => _keys.GetValueOrDefault(keyId);

    public void Add(TrustedKey key) => _keys[key.KeyId] = key;

    public void LoadJson(string json)
    {
        var loaded = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("keys", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                // A document that reads fine and declares no keys really does mean no
                // keys; that is a finding, not a failure.
                log.Warn("trust.keys", "Trust store has no 'keys' array.");
                _keys.Clear();
                return;
            }

            foreach (var entry in array.EnumerateArray())
            {
                var key = entry.Deserialize<TrustedKey>(Options);
                if (key is not null && !string.IsNullOrWhiteSpace(key.KeyId) && !string.IsNullOrWhiteSpace(key.PublicKey))
                {
                    loaded[key.KeyId] = key;
                }
            }
        }
        catch (Exception ex)
        {
            // Deliberately fail-closed, and deliberately unlike the other stores.
            // Losing the ability to verify a package is the safe direction: an
            // unreadable trust file is not evidence that the keys it used to hold are
            // still the keys it holds. The install records and detection hints are the
            // opposite case — nothing about them is a security decision, and dropping
            // them there would be pure loss.
            log.Error("trust.keys", "Trust store could not be read; no key will be trusted.", ex);
            _keys.Clear();
            return;
        }

        _keys.Clear();
        foreach (var pair in loaded)
        {
            _keys[pair.Key] = pair.Value;
        }

        log.Info("trust.keys", $"Loaded {_keys.Count} trusted key(s).");
    }

    public static TrustedKeyStore FromFile(string path, IAppLogger log)
    {
        var store = new TrustedKeyStore(log);
        if (File.Exists(path))
        {
            store.LoadJson(File.ReadAllText(path));
        }
        else
        {
            log.Warn("trust.keys", $"Trust store not found: {path}");
        }

        return store;
    }
}
