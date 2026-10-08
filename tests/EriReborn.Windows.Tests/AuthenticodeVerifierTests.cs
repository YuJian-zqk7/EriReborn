using EriReborn.Platform.Windows;
using Xunit;

namespace EriReborn.Windows.Tests;

/// <summary>
/// Authenticode verification is the only thing that answers "who produced this
/// installer" as opposed to "are these the bytes I expected". A hash check alone
/// cannot tell an official repackage from a malicious one.
/// </summary>
public sealed class AuthenticodeVerifierTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

    public AuthenticodeVerifierTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static string SystemBinary(string name)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    [Fact]
    public void A_missing_file_is_never_reported_as_valid()
    {
        var result = AuthenticodeVerifier.Verify(Path.Combine(_work, "nope.exe"));

        Assert.False(result.IsValid);
        Assert.Equal(AuthenticodeTrust.UnknownError, result.Trust);
    }

    [Fact]
    public void An_unsigned_file_is_reported_as_unsigned_not_as_trusted()
    {
        var path = Path.Combine(_work, "plain.bin");
        File.WriteAllText(path, "not signed at all");

        var result = AuthenticodeVerifier.Verify(path);

        Assert.False(result.IsValid);
        Assert.Equal(AuthenticodeTrust.NotSigned, result.Trust);
    }

    [Fact]
    public void A_signed_windows_binary_verifies_and_names_its_publisher()
    {
        // kernel32.dll carries an embedded signature, unlike several system files
        // that are signed through a catalog instead.
        var result = AuthenticodeVerifier.Verify(SystemBinary("kernel32.dll"));

        Assert.True(result.IsValid, $"kernel32.dll did not verify: {result.Trust} 0x{AuthenticodeVerifier.LastStatus:X8}");
        Assert.False(string.IsNullOrWhiteSpace(result.Signer));
    }

    [Fact]
    public void Changing_bytes_inside_the_image_breaks_the_signature()
    {
        var source = SystemBinary("kernel32.dll");
        if (!AuthenticodeVerifier.Verify(source).IsValid)
        {
            // Nothing to tamper with on a machine where this file is not embedded-signed.
            return;
        }

        var tampered = Path.Combine(_work, "kernel32-tampered.dll");
        var bytes = File.ReadAllBytes(source);

        // Well inside the mapped image: the certificate table at the end is
        // deliberately excluded from the Authenticode hash.
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(tampered, bytes);

        var result = AuthenticodeVerifier.Verify(tampered);

        Assert.False(result.IsValid);
        Assert.Equal(AuthenticodeTrust.InvalidSignature, result.Trust);
    }

    [Fact]
    public void A_signature_failure_is_not_confused_with_a_missing_signature()
    {
        // These are different findings and lead to different messages: one says
        // "this is not signed", the other says "this was modified after signing".
        Assert.NotEqual(AuthenticodeTrust.NotSigned, AuthenticodeTrust.InvalidSignature);
        Assert.NotEqual(AuthenticodeTrust.NotSigned, AuthenticodeTrust.UntrustedRoot);
    }
}
