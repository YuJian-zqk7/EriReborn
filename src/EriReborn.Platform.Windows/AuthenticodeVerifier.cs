using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EriReborn.Platform.Windows;

public enum AuthenticodeTrust
{
    /// <summary>The embedded signature verified and its chain built.</summary>
    Valid,

    /// <summary>The file carries no signature at all.</summary>
    NotSigned,

    /// <summary>The signature does not match the file: it was modified after signing.</summary>
    InvalidSignature,

    /// <summary>Signed, but by a certificate that does not chain to a trusted root.</summary>
    UntrustedRoot,

    /// <summary>Signed, but the chain could not be built for another reason.</summary>
    ChainError,

    UnknownError,
}

public sealed record AuthenticodeResult(AuthenticodeTrust Trust, string Message, string? Signer = null, string? Issuer = null)
{
    public bool IsValid => Trust == AuthenticodeTrust.Valid;
}

/// <summary>
/// Verifies a downloaded installer's embedded Authenticode signature.
///
/// Note: many Windows system binaries are signed through a catalog rather than
/// embedded in the file, so they legitimately report <see cref="AuthenticodeTrust.NotSigned"/>
/// here. Vendor installers (MSI and EXE) carry embedded signatures, which is the
/// case this verifier exists for.
///
/// This answers a different question from the SHA-256 check: a hash proves the
/// bytes are the ones we expected, while this proves who produced them. A
/// tampered installer that was re-signed by someone else passes the first and
/// fails this one.
/// </summary>
public static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    public static AuthenticodeResult Verify(string path)
    {
        if (!File.Exists(path))
        {
            return new AuthenticodeResult(AuthenticodeTrust.UnknownError, $"文件不存在：{path}");
        }

        // The signer is read first so it can be reported even when the chain is
        // what failed.
        var (signer, issuer) = TryReadSigner(path);

        var fileInfoSize = Marshal.SizeOf<WinTrustFileInfo>();
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)fileInfoSize,
            pcwszFilePath = Marshal.StringToHGlobalUni(path),
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var fileInfoPtr = Marshal.AllocHGlobal(fileInfoSize);
        var dataSize = Marshal.SizeOf<WinTrustData>();
        var dataPtr = Marshal.AllocHGlobal(dataSize);

        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            var data = new WinTrustData
            {
                cbStruct = (uint)dataSize,
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };

            Marshal.StructureToPtr(data, dataPtr, fDeleteOld: false);

            uint status;
            try
            {
                status = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPtr);
            }
            catch (DllNotFoundException)
            {
                return new AuthenticodeResult(
                    AuthenticodeTrust.UnknownError,
                    "本机没有 wintrust.dll，无法验证安装包签名。",
                    signer,
                    issuer);
            }
            finally
            {
                // Always release the state, otherwise the trust provider leaks.
                var close = Marshal.PtrToStructure<WinTrustData>(dataPtr);
                close.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(close, dataPtr, fDeleteOld: false);
                try
                {
                    WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPtr);
                }
                catch (DllNotFoundException)
                {
                    // Nothing further to do.
                }
            }

            return Interpret(status, signer, issuer);
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo.pcwszFilePath);
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeHGlobal(dataPtr);
        }
    }

    /// <summary>Raw last status, for diagnostics. Not part of the decision.</summary>
    public static uint LastStatus { get; private set; }

    private static AuthenticodeResult Interpret(uint status, string? signer, string? issuer)
    {
        LastStatus = status;

        // ERROR_SUCCESS: the signature verified and the chain built.
        if (status == 0)
        {
            return new AuthenticodeResult(AuthenticodeTrust.Valid, $"数字签名有效：{signer ?? "未知发布者"}。", signer, issuer);
        }

        // No usable signature. The codes are distinct reasons, not the same one:
        //   TRUST_E_NOSIGNATURE       0x800B0100  the file simply is not signed
        //   TRUST_E_PROVIDER_UNKNOWN  0x800B0001  no provider handles this format
        //   TRUST_E_SUBJECT_FORM_UNKNOWN 0x800B0003 not a signable form (e.g. text)
        //   TRUST_E_NO_SIGNER_CERT    0x800B0002  signature present but no certificate
        if (status is 0x800B0100 or 0x800B0001 or 0x800B0003 or 0x800B0002)
        {
            return new AuthenticodeResult(
                AuthenticodeTrust.NotSigned,
                "安装包没有可验证的数字签名。",
                signer,
                issuer);
        }

        // TRUST_E_BAD_DIGEST 0x80096010 / TRUST_E_BAD_DIGEST alt 0x80091007: the
        // signature exists but does not match the bytes — the file was modified.
        if (status is 0x80096010 or 0x80091007)
        {
            const string text = "安装包的签名与文件内容不匹配，签名后已被修改。";
            return new AuthenticodeResult(AuthenticodeTrust.InvalidSignature, text, signer, issuer);
        }

        // CERT_E_UNTRUSTEDROOT 0x800B0109, CERT_E_WRONG_USAGE 0x800B0110,
        // TRUST_E_EXPLICIT_DISTRUST 0x800B0111, TRUST_E_SUBJECT_NOT_TRUSTED 0x800B0004.
        if (status is 0x800B0109 or 0x800B0110 or 0x800B0111 or 0x800B0004)
        {
            const string text = "安装包已签名，但证书链不受信任。";
            return new AuthenticodeResult(AuthenticodeTrust.UntrustedRoot, text, signer, issuer);
        }

        // CERT_E_CHAINING 0x800B010A, CERT_E_EXPIRED 0x800B0101,
        // CERT_E_REVOKED 0x800B010C, CRYPT_E_SECURITY_SETTINGS 0x80092026.
        if (status is 0x800B010A or 0x800B0101 or 0x800B010C or 0x80092026)
        {
            return new AuthenticodeResult(AuthenticodeTrust.ChainError, $"无法构建证书链（0x{status:X8}）。", signer, issuer);
        }

        var message = signer is null ? "签名验证未通过。" : $"签名验证未通过：{signer}。";
        return new AuthenticodeResult(AuthenticodeTrust.UnknownError, $"{message}（0x{status:X8}）", signer, issuer);
    }

    private static (string? Signer, string? Issuer) TryReadSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // The replacement does not read signed files.
            var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return (certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true));
        }
        catch (CryptographicException)
        {
            return (null, null);
        }
        catch (IOException)
        {
            return (null, null);
        }
    }

    [DllImport("wintrust.dll", PreserveSig = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
        IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
