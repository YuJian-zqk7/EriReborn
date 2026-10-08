using System.Security.Cryptography;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Extension.Signing;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The shipped trust anchor is the root of every extension trust decision. If it
/// fails to parse, the application silently trusts nothing — which is how a
/// string-vs-enum mismatch nearly shipped.
/// </summary>
public sealed class TrustAnchorTests
{
    private static string TrustFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "trust", "keys.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("assets/trust/keys.json was not found above the test directory.");
    }

    [Fact]
    public void The_shipped_trust_anchor_loads()
    {
        var store = new TrustedKeyStore(AppLog.For("Test"));
        store.LoadJson(File.ReadAllText(TrustFile()));

        Assert.Equal(1, store.Count);

        var key = store.Find("erireborn-root-2026");
        Assert.NotNull(key);
        Assert.Equal("ecdsa-p256-sha256", key!.Algorithm);
        Assert.Equal(SoftwareTrust.Official, key.Trust);
        Assert.Equal("EriReborn", key.Publisher);
    }

    [Fact]
    public void The_shipped_public_key_is_a_usable_p256_key()
    {
        var store = new TrustedKeyStore(AppLog.For("Test"));
        store.LoadJson(File.ReadAllText(TrustFile()));
        var key = store.Find("erireborn-root-2026")!;

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out var read);

        Assert.True(read > 0);
        Assert.Equal(256, ecdsa.KeySize);
    }

    [Fact]
    public void The_trust_file_does_not_contain_a_private_key()
    {
        var json = File.ReadAllText(TrustFile());

        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Trust_written_as_a_name_or_a_number_both_parse()
    {
        var asName = new TrustedKeyStore(AppLog.For("Test"));
        asName.LoadJson("""{ "keys": [ { "keyId": "k", "publicKey": "abc", "trust": "Verified" } ] }""");
        Assert.Equal(SoftwareTrust.Verified, asName.Find("k")!.Trust);

        var asNumber = new TrustedKeyStore(AppLog.For("Test"));
        asNumber.LoadJson("""{ "keys": [ { "keyId": "k", "publicKey": "abc" } ] }""");
        Assert.Equal(SoftwareTrust.Verified, asNumber.Find("k")!.Trust);
    }
}
