using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Extension.Signing;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Trust must come from a real signature, not from what a package says about
/// itself (spec 35). These tests sign with a generated key pair and verify with
/// real ECDSA, then tamper with the package to prove the check bites.
/// </summary>
public sealed class PackageSignatureTests : IDisposable
{
    private readonly string _root;

    public PackageSignatureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string NewPackage(params (string Name, string Content)[] files)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return directory;
    }

    private static TrustedKeyStore StoreWith(string keyId, string publicKey, SoftwareTrust trust = SoftwareTrust.Official)
    {
        var store = new TrustedKeyStore(AppLog.For("Test"));
        store.Add(new TrustedKey { KeyId = keyId, PublicKey = publicKey, Trust = trust, Publisher = "EriReborn" });
        return store;
    }

    [Fact]
    public void A_signed_package_verifies_and_confers_the_keys_trust()
    {
        var directory = NewPackage(("manifest.json", "{\"id\":\"demo\"}"), ("demo.dll", "binary"));
        var (publicKey, privateKey) = PackageSigner.CreateKeyPair();
        var signature = PackageSigner.Sign(directory, "anchor", privateKey, "EriReborn");

        var check = SignatureVerifier.Verify(directory, signature, StoreWith("anchor", publicKey, SoftwareTrust.Official));

        Assert.True(check.IsValid, check.Message);
        Assert.Equal(SoftwareTrust.Official, check.Trust);
        Assert.Equal("EriReborn", check.Publisher);
    }

    [Fact]
    public void Tampering_with_the_assembly_invalidates_the_signature()
    {
        var directory = NewPackage(("manifest.json", "{\"id\":\"demo\"}"), ("demo.dll", "binary"));
        var (publicKey, privateKey) = PackageSigner.CreateKeyPair();
        var signature = PackageSigner.Sign(directory, "anchor", privateKey);

        // Swap the payload after signing.
        File.WriteAllText(Path.Combine(directory, "demo.dll"), "malicious");

        var check = SignatureVerifier.Verify(directory, signature, StoreWith("anchor", publicKey));

        Assert.False(check.IsValid);
        Assert.Contains("篡改", check.Message);
    }

    [Fact]
    public void Tampering_with_the_manifest_invalidates_the_signature()
    {
        var directory = NewPackage(("manifest.json", "{\"id\":\"demo\"}"));
        var (publicKey, privateKey) = PackageSigner.CreateKeyPair();
        var signature = PackageSigner.Sign(directory, "anchor", privateKey);

        // Escalate the declared permissions after signing.
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "{\"id\":\"demo\",\"permissions\":[\"process.run\"]}");

        Assert.False(SignatureVerifier.Verify(directory, signature, StoreWith("anchor", publicKey)).IsValid);
    }

    [Fact]
    public void Adding_a_file_after_signing_invalidates_the_signature()
    {
        var directory = NewPackage(("manifest.json", "{}"));
        var (publicKey, privateKey) = PackageSigner.CreateKeyPair();
        var signature = PackageSigner.Sign(directory, "anchor", privateKey);

        File.WriteAllText(Path.Combine(directory, "extra.dll"), "smuggled");

        Assert.False(SignatureVerifier.Verify(directory, signature, StoreWith("anchor", publicKey)).IsValid);
    }

    [Fact]
    public void A_signature_from_an_untrusted_key_is_rejected()
    {
        var directory = NewPackage(("manifest.json", "{}"));
        var (_, privateKey) = PackageSigner.CreateKeyPair();
        var signature = PackageSigner.Sign(directory, "stranger", privateKey);

        var (otherPublic, _) = PackageSigner.CreateKeyPair();
        var check = SignatureVerifier.Verify(directory, signature, StoreWith("stranger", otherPublic));

        Assert.False(check.IsValid);
    }

    [Fact]
    public void A_key_id_that_is_not_stored_is_rejected_without_touching_crypto()
    {
        var directory = NewPackage(("manifest.json", "{}"));
        var (publicKey, privateKey) = PackageSigner.CreateKeyPair();
        var signature = PackageSigner.Sign(directory, "known", privateKey);

        var check = SignatureVerifier.Verify(directory, signature, StoreWith("different-id", publicKey));

        Assert.False(check.IsValid);
        Assert.Contains("不在信任列表", check.Message);
    }

    [Fact]
    public void A_signature_that_is_not_base64_is_rejected()
    {
        var directory = NewPackage(("manifest.json", "{}"));
        var (publicKey, _) = PackageSigner.CreateKeyPair();
        var signature = new PackageSignature { KeyId = "anchor", Signature = "not base64 !!" };

        var check = SignatureVerifier.Verify(directory, signature, StoreWith("anchor", publicKey));

        Assert.False(check.IsValid);
        Assert.Contains("base64", check.Message);
    }

    [Fact]
    public void The_signed_payload_covers_every_file_and_is_order_independent()
    {
        var first = NewPackage(("a.txt", "1"), ("sub/b.txt", "2"), ("c.txt", "3"));
        var second = NewPackage(("c.txt", "3"), ("a.txt", "1"), ("sub/b.txt", "2"));

        Assert.Equal(
            PackageSignature.ComputePayload(first),
            PackageSignature.ComputePayload(second));
    }

    [Fact]
    public void The_signed_payload_excludes_the_signature_file_itself()
    {
        var directory = NewPackage(("manifest.json", "{}"));
        var before = PackageSignature.ComputePayload(directory);

        var (publicKey, privateKey) = PackageSigner.CreateKeyPair();
        PackageSigner.Write(directory, PackageSigner.Sign(directory, "anchor", privateKey));

        Assert.Equal(before, PackageSignature.ComputePayload(directory));
        Assert.True(File.Exists(Path.Combine(directory, PackageSignature.FileName)));
        Assert.False(SignatureVerifier.Verify(directory, PackageSignature.TryRead(directory)!, StoreWith("anchor", publicKey)) is { IsValid: false });
    }

    [Fact]
    public void An_unsigned_package_has_no_signature()
    {
        var directory = NewPackage(("manifest.json", "{}"));

        Assert.Null(PackageSignature.TryRead(directory));
    }

    [Fact]
    public void A_damaged_signature_file_reads_as_unsigned_rather_than_throwing()
    {
        var directory = NewPackage(("manifest.json", "{}"));
        File.WriteAllText(Path.Combine(directory, PackageSignature.FileName), "{ not json");

        Assert.Null(PackageSignature.TryRead(directory));
    }

    [Fact]
    public void An_empty_trust_store_trusts_nothing()
    {
        var store = new TrustedKeyStore(AppLog.For("Test"));
        store.LoadJson("{ \"keys\": [] }");

        Assert.Equal(0, store.Count);
        Assert.Null(store.Find("anything"));
    }

    [Fact]
    public void A_damaged_trust_store_yields_no_keys_instead_of_throwing()
    {
        var store = new TrustedKeyStore(AppLog.For("Test"));
        store.Add(new TrustedKey { KeyId = "x", PublicKey = "y" });
        store.LoadJson("{ this is not json");

        Assert.Equal(0, store.Count);
    }

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
}
