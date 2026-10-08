using EriReborn.Platform.Windows;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Exercises Authenticode verification against real files: a signed system
/// binary, a byte-modified copy of it, and an unsigned file we create.
/// </summary>
internal static class AuthenticodeCheck
{
    public static int Run()
    {
        var failures = 0;
        var work = Path.Combine(Path.GetTempPath(), "erireborn-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            // 1) A real binary with an EMBEDDED signature. Several Windows
            //    system files are catalog-signed instead, so they correctly
            //    report NotSigned and are not useful as a positive sample.
            var signed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "kernel32.dll");

            var valid = AuthenticodeVerifier.Verify(signed);
            Console.WriteLine($"      {Path.GetFileName(signed)} -> {valid.Trust} (0x{AuthenticodeVerifier.LastStatus:X8}); signer={valid.Signer ?? "(none)"}");
            foreach (var probe in new[]
            {
                @"C:\Windows\System32\kernel32.dll",
                @"C:\Windows\System32\cmd.exe",
                @"C:\Windows\explorer.exe",
            })
            {
                var r = AuthenticodeVerifier.Verify(probe);
                Console.WriteLine($"      probe {Path.GetFileName(probe)} -> {r.Trust} (0x{AuthenticodeVerifier.LastStatus:X8}) signer={r.Signer ?? "(none)"}");
            }
            failures += Report("a Microsoft-signed binary verifies", valid.IsValid);

            if (valid.IsValid)
            {
                failures += Report("the signer is reported", !string.IsNullOrWhiteSpace(valid.Signer));
            }

            // 2) Same file with one byte changed: the signature must break.
            // Flip a byte well inside the mapped image. Changing the trailing
            // bytes would prove nothing: the certificate table lives there and is
            // excluded from the Authenticode hash by design.
            var tampered = Path.Combine(work, "kernel32-tampered.dll");
            var bytes = File.ReadAllBytes(signed);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(tampered, bytes);

            var broken = AuthenticodeVerifier.Verify(tampered);
            Console.WriteLine($"      tampered copy -> {broken.Trust} (0x{AuthenticodeVerifier.LastStatus:X8}); {broken.Message}");
            failures += Report("modifying a signed file breaks its signature", !broken.IsValid);
            failures += Report("the failure is attributed to the signature, not to a missing one",
                broken.Trust == AuthenticodeTrust.InvalidSignature);

            // 3) An unsigned file.
            var unsigned = Path.Combine(work, "unsigned.bin");
            File.WriteAllText(unsigned, "not signed at all");

            var none = AuthenticodeVerifier.Verify(unsigned);
            Console.WriteLine($"      unsigned file -> {none.Trust} (0x{AuthenticodeVerifier.LastStatus:X8})");
            failures += Report("an unsigned file is recognised as unsigned",
                none.Trust == AuthenticodeTrust.NotSigned);

            // 4) A missing file must not be reported as trusted.
            var missing = AuthenticodeVerifier.Verify(Path.Combine(work, "does-not-exist.exe"));
            failures += Report("a missing file is not reported as valid", !missing.IsValid);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }

        return failures;
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
