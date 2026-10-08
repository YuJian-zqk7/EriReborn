using System.Security.Cryptography;
using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Paths;
using EriReborn.Engine.Software;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The catalog authority chain: C decides who may override whom, B decides what
/// happens when the official signature does not verify. Both halves exist to stop
/// a swapped or edited catalog from being used as if it were official.
/// </summary>
public sealed class CatalogTrustChainTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

    public CatalogTrustChainTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static readonly IAppLogger Log = AppLog.For("TrustChainTest");

    private string WriteCatalog(string fileName, string catalogId, string id, string name, string trust = "Verified")
    {
        var json = $$"""
        {
          "schema": 3,
          "catalog": "{{catalogId}}",
          "items": [
            {
              "id": "{{id}}",
              "name": "{{name}}",
              "categoryId": "Utility",
              "directory": "{{name.Replace(" ", string.Empty)}}",
              "tier": "Optional",
              "trust": "{{trust}}",
              "mode": "Install",
              "sources": []
            }
          ]
        }
        """;

        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    private static (ECDsa Key, CatalogTrustKey TrustKey) NewKey(string keyId)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return (key, new CatalogTrustKey(keyId, CatalogTrustAnchor.DefaultAlgorithm, publicKey, "Test"));
    }

    private void Sign(string keyId)
    {
        var (key, _) = NewKey(keyId);
        using (key)
        {
            var files = CatalogSignature.BuildFileList(_root);
            var signature = new CatalogSignature
            {
                KeyId = keyId,
                Files = files,
                Signature = Convert.ToBase64String(
                    key.SignData(CatalogSignature.ComputePayload(files), HashAlgorithmName.SHA256)),
            };

            File.WriteAllText(Path.Combine(_root, CatalogSignature.FileName), CatalogSignature.Serialize(signature));
        }
    }

    private CatalogVerdict VerifyWith(CatalogTrustKey trustKey)
        => CatalogVerifier.Verify(_root, Log, new[] { trustKey });

    /// <summary>Signs the files that are in the directory right now, with a key the caller owns.</summary>
    private void SignFilesWith(ECDsa key, string keyId)
    {
        var files = CatalogSignature.BuildFileList(_root);
        var signature = new CatalogSignature
        {
            KeyId = keyId,
            Files = files,
            Signature = Convert.ToBase64String(
                key.SignData(CatalogSignature.ComputePayload(files), HashAlgorithmName.SHA256)),
        };

        File.WriteAllText(Path.Combine(_root, CatalogSignature.FileName), CatalogSignature.Serialize(signature));
    }

    /// <summary>
    /// A key store as an attacker would ship it: the same shape <see cref="CatalogTrustKey"/> has, naming
    /// their own key, written where the data lives.
    /// </summary>
    private void WriteAttackerKeyStore(string fileName, CatalogTrustKey attacker)
    {
        File.WriteAllText(Path.Combine(_root, fileName),
            $$"""
            {
              "keys": [
                {
                  "keyId": "{{attacker.KeyId}}",
                  "algorithm": "{{attacker.Algorithm}}",
                  "publicKey": "{{attacker.PublicKey}}",
                  "publisher": "EriReborn"
                }
              ]
            }
            """);
    }

    // ---------------------------------------------------------------- signatures

    [Fact]
    public void A_valid_signature_grants_official_authority()
    {
        WriteCatalog("official.json", "official", "demo", "Demo");
        var (key, trustKey) = NewKey("test-key");
        using (key)
        {
            var files = CatalogSignature.BuildFileList(_root);
            var signature = new CatalogSignature
            {
                KeyId = "test-key",
                Files = files,
                Signature = Convert.ToBase64String(
                    key.SignData(CatalogSignature.ComputePayload(files), HashAlgorithmName.SHA256)),
            };
            File.WriteAllText(Path.Combine(_root, CatalogSignature.FileName), CatalogSignature.Serialize(signature));

            var verdict = VerifyWith(trustKey);

            Assert.Equal(CatalogSignatureState.Verified, verdict.State);
            Assert.True(verdict.GrantsOfficialAuthority);
            Assert.Contains("official.json", verdict.OfficialFiles);
        }
    }

    [Fact]
    public void A_tampered_official_file_is_detected()
    {
        WriteCatalog("official.json", "official", "demo", "Demo");
        var (key, trustKey) = NewKey("test-key");
        using (key)
        {
            var files = CatalogSignature.BuildFileList(_root);
            var signature = new CatalogSignature
            {
                KeyId = "test-key",
                Files = files,
                Signature = Convert.ToBase64String(
                    key.SignData(CatalogSignature.ComputePayload(files), HashAlgorithmName.SHA256)),
            };
            File.WriteAllText(Path.Combine(_root, CatalogSignature.FileName), CatalogSignature.Serialize(signature));

            // Edit the data after signing, the way a cracked build would.
            WriteCatalog("official.json", "official", "demo", "Hijacked", trust: "Official");

            var verdict = VerifyWith(trustKey);

            Assert.Equal(CatalogSignatureState.Invalid, verdict.State);
            Assert.False(verdict.GrantsOfficialAuthority);
            Assert.Contains("official.json", verdict.Message);
        }
    }

    [Fact]
    public void A_signature_from_a_key_that_is_not_pinned_is_rejected()
    {
        WriteCatalog("official.json", "official", "demo", "Demo");
        var (key, _) = NewKey("attacker-key");
        using (key)
        {
            var files = CatalogSignature.BuildFileList(_root);
            var signature = new CatalogSignature
            {
                KeyId = "attacker-key",
                Files = files,
                Signature = Convert.ToBase64String(
                    key.SignData(CatalogSignature.ComputePayload(files), HashAlgorithmName.SHA256)),
            };
            File.WriteAllText(Path.Combine(_root, CatalogSignature.FileName), CatalogSignature.Serialize(signature));

            // The anchor does not contain that key, so swapping in a key store and
            // signing with it gains nothing.
            var (_, pinned) = NewKey("erireborn-catalog-2026");
            var verdict = VerifyWith(pinned);

            Assert.Equal(CatalogSignatureState.Invalid, verdict.State);
            Assert.False(verdict.GrantsOfficialAuthority);
        }
    }

    [Fact]
    public void A_missing_signature_grants_no_authority()
    {
        WriteCatalog("official.json", "official", "demo", "Demo");

        var verdict = CatalogVerifier.Verify(_root, Log, Array.Empty<CatalogTrustKey>());

        Assert.Equal(CatalogSignatureState.NoSignature, verdict.State);
        Assert.False(verdict.GrantsOfficialAuthority);
    }

    [Fact]
    public void The_pinned_anchor_actually_contains_a_key()
    {
        // A silently empty anchor would make every catalog untrusted while still
        // looking like a working signature system.
        Assert.NotEmpty(CatalogTrustAnchor.Keys);
        Assert.NotNull(CatalogTrustAnchor.Find("erireborn-catalog-2026"));
    }

    /// <summary>
    /// The pinned list cannot be edited, so the attack moves to the file that travels with the data:
    /// the anchor's own documentation names <c>keys.json</c>, and a key store sitting next to the catalog
    /// is the one thing an attacker who can replace the catalog can also replace. Nothing reads it, so
    /// the signature they made with the key it names earns exactly what an unknown key always earns.
    /// </summary>
    [Fact]
    public void Replacing_the_key_store_beside_the_catalog_grants_no_authority()
    {
        WriteCatalog("official.json", "official", "demo", "Demo", trust: "Official");

        var (key, attacker) = NewKey("attacker-key");
        using (key)
        {
            // Written before signing so the signature covers them: the refusal is then about the key and
            // not about a hash, which is the one thing that must not be able to change the outcome.
            foreach (var name in new[] { "keys.json", "trust.json", "catalog-trust.json" })
            {
                WriteAttackerKeyStore(name, attacker);
            }

            SignFilesWith(key, attacker.KeyId);

            // The production call: no anchor is injected, so this is the pinned list and nothing else.
            var verdict = CatalogVerifier.Verify(_root, Log);

            Assert.Equal(CatalogSignatureState.Invalid, verdict.State);
            Assert.False(verdict.GrantsOfficialAuthority);
            Assert.Contains(attacker.KeyId, verdict.Message);
            Assert.Contains("内置信任根", verdict.Message);
        }
    }

    [Fact]
    public void A_key_store_beside_the_catalog_cannot_demote_a_verdict_that_already_verified()
    {
        WriteCatalog("official.json", "official", "demo", "Demo");

        var (key, trustKey) = NewKey("test-key");
        using (key)
        {
            SignFilesWith(key, trustKey.KeyId);

            var before = VerifyWith(trustKey);
            Assert.Equal(CatalogSignatureState.Verified, before.State);

            // A store beside the data can neither promote nor demote: the verdict is decided before any
            // file in that directory is considered, so adding one changes nothing either way.
            var (other, attacker) = NewKey("attacker-key");
            using (other)
            {
                WriteAttackerKeyStore("keys.json", attacker);
            }

            var after = VerifyWith(trustKey);

            Assert.Equal(before.State, after.State);
            Assert.Equal(before.KeyId, after.KeyId);
            Assert.Equal(before.OfficialFiles, after.OfficialFiles);
        }
    }

    [Fact]
    public void The_anchor_cannot_be_swapped_at_runtime()
    {
        // A read-only view over a plain array is still an array underneath, and anyone who can cast it
        // back can write their own key into the root of trust — which would make every signature in this
        // class decorative. Compiled in has to mean compiled in.
        Assert.IsNotType<CatalogTrustKey[]>(CatalogTrustAnchor.Keys);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CatalogTrustKey>)CatalogTrustAnchor.Keys)[0] = new CatalogTrustKey("x", "y", "z", null));
    }

    // ------------------------------------------------------------- authority (C)

    private static CatalogVerdict OfficialVerdict(params string[] officialFiles)
        => new(
            CatalogSignatureState.Verified,
            "test",
            "test-key",
            "Test",
            officialFiles.ToHashSet(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public async Task Official_definitions_win_over_third_party_even_when_read_first()
    {
        // The third-party file sorts BEFORE the official one, so anything that
        // decides by load order would pick the wrong winner.
        var third = WriteCatalog("aaa-third.json", "third", "demo", "Hijacked");
        var official = WriteCatalog("official.json", "official", "demo", "Official Demo");

        var catalog = await new CatalogReader(Log).LoadFilesAsync(
            new[] { third, official },
            OfficialVerdict("official.json"));

        var winner = Assert.Single(catalog.Software);
        Assert.Equal("Official Demo", winner.Name);
        Assert.Equal(CatalogProvenance.Official, winner.Provenance);
        Assert.Contains(catalog.Report.Rejected, i => i.Code == "catalog.third_party_conflict");
    }

    [Fact]
    public async Task The_same_outcome_holds_when_the_official_file_is_read_first()
    {
        var official = WriteCatalog("official.json", "official", "demo", "Official Demo");
        var third = WriteCatalog("aaa-third.json", "third", "demo", "Hijacked");

        var catalog = await new CatalogReader(Log).LoadFilesAsync(
            new[] { official, third },
            OfficialVerdict("official.json"));

        var winner = Assert.Single(catalog.Software);
        Assert.Equal("Official Demo", winner.Name);
        Assert.Contains(catalog.Report.Rejected, i => i.Code == "catalog.third_party_conflict");
    }

    [Fact]
    public async Task A_third_party_entry_may_add_an_id_the_official_catalog_does_not_define()
    {
        var official = WriteCatalog("official.json", "official", "demo", "Demo");
        var extra = WriteCatalog("extra.json", "third", "extra-tool", "Extra Tool");

        var catalog = await new CatalogReader(Log).LoadFilesAsync(
            new[] { official, extra },
            OfficialVerdict("official.json"));

        Assert.Equal(2, catalog.Software.Count);
        Assert.Equal(1, catalog.OfficialCount);
        Assert.Equal(1, catalog.ThirdPartyCount);
        Assert.Equal(CatalogProvenance.ThirdParty, catalog.Software.Single(s => s.Id == "extra-tool").Provenance);
    }

    [Fact]
    public async Task A_third_party_entry_can_never_claim_official_trust()
    {
        var official = WriteCatalog("official.json", "official", "demo", "Demo");
        var extra = WriteCatalog("extra.json", "third", "extra-tool", "Extra Tool", trust: "Official");

        var catalog = await new CatalogReader(Log).LoadFilesAsync(
            new[] { official, extra },
            OfficialVerdict("official.json"));

        var entry = catalog.Software.Single(s => s.Id == "extra-tool");

        // The file says "Official"; verification says otherwise, and verification wins.
        Assert.Equal(SoftwareTrust.Community, entry.Trust);
    }

    // ------------------------------------------------------ verification failure (B)

    [Fact]
    public async Task Verification_failure_leaves_every_entry_untrusted()
    {
        var official = WriteCatalog("official.json", "official", "demo", "Demo", trust: "Official");

        var catalog = await new CatalogReader(Log).LoadFilesAsync(
            new[] { official },
            CatalogVerdict.Invalid("签名不匹配"));

        var entry = Assert.Single(catalog.Software);

        Assert.Equal(CatalogProvenance.Untrusted, entry.Provenance);
        Assert.Equal(SoftwareTrust.Community, entry.Trust);
        Assert.False(catalog.HasOfficialAuthority);
        Assert.Equal(1, catalog.UntrustedCount);
    }

    [Fact]
    public async Task Untrusted_software_cannot_be_installed()
    {
        var temp = Path.Combine(_root, "install");
        Directory.CreateDirectory(temp);

        var platform = new TestSupport.TestPlatform(
            new TestSupport.TestFileSystemService(temp),
            new TestSupport.TestNetworkService(new HttpClient()),
            new TestSupport.InMemoryCredentialStore());

        var engine = new EriReborn.Engine.Software.SoftwareEngine(
            platform,
            new EriReborn.Core.Paths.PathResolver(),
            new EriReborn.Engine.Software.InstallationRegistry(Path.Combine(_root, "i.json"), Log),
            new EriReborn.Engine.Software.DetectionHintStore(Path.Combine(_root, "h.json"), Log),
            Log);

        var software = new SoftwareDefinition
        {
            Id = "demo",
            Name = "Demo",
            CategoryId = "Utility",
            DirectoryName = "Demo",
            Trust = SoftwareTrust.Verified,
            Provenance = CatalogProvenance.Untrusted,
            Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/x.exe" } },
        };

        var result = await engine.InstallAsync(software, new EnvironmentContext { RootPath = temp });

        Assert.Equal(InstallState.UntrustedDefinition, result.State);
        Assert.False(result.IsSuccess);
        Assert.False(Directory.Exists(Path.Combine(temp, "Demo")));
    }
}
