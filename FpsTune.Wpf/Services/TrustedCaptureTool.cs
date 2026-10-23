using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace FpsTune.Wpf.Services;

/// <summary>只执行通过 Windows Authenticode 验证的 Intel / NVIDIA 采样工具。</summary>
internal static class TrustedCaptureTool
{
    internal enum FileSignatureState { Valid, Unsigned, Invalid }

    internal static FileStream OpenVerified(string path)
    {
        var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!VerifySignature(file.Name, file.SafeFileHandle.DangerousGetHandle()))
                throw new InvalidDataException("PresentMon signature verification failed.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal static bool IsAllowedPublisher(string name) => name.Equals("Intel Corporation", StringComparison.OrdinalIgnoreCase)
        || name.Equals("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTrusted(string path)
    {
        try { using var file = OpenVerified(path); return true; }
        catch { return false; }
    }

    /// <summary>判定任意文件的 Authenticode 状态与签名者；不套用采样工具的发布者白名单。</summary>
    internal static (FileSignatureState State, string Publisher) InspectSignature(string path)
    {
        try
        {
            using var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            var state = VerifyFile(file.Name, file.SafeFileHandle.DangerousGetHandle(), out var publisher);
            return (state, publisher ?? "");
        }
        catch { return (FileSignatureState.Invalid, ""); }
    }

    private static bool VerifySignature(string path, IntPtr handle)
    {
        var state = VerifyFile(path, handle, out var publisher);
        return state == FileSignatureState.Valid && publisher is not null && IsAllowedPublisher(publisher);
    }

    // 无签名数据 / SIP 不认识该文件（非 PE、哑文件、仅目录签名）都按「未签名」处理，
    // 只有验签链条失败才算 Invalid（实测：哑文件返回 TRUST_E_SUBJECT_UNKNOWN 0x800B0003）。
    private const int TrustESubjectUnknown = unchecked((int)0x800B0003);
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectNotTainted = unchecked((int)0x800B17D1);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B17D7);

    private static FileSignatureState VerifyFile(string path, IntPtr handle, out string? publisher)
    {
        publisher = null;
        var fileInfo = new FileInfoNative { Size = (uint)Marshal.SizeOf<FileInfoNative>(), Path = Marshal.StringToCoTaskMemUni(path), Handle = handle };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoNative>());
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2,
            UnionChoice = 1, File = pointer, ProviderFlags = 0x1000, StateAction = 1 };
        try
        {
            Marshal.StructureToPtr(fileInfo, pointer, false);
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result is TrustESubjectUnknown or TrustENoSignature or TrustESubjectNotTainted or TrustESubjectFormUnknown)
                return FileSignatureState.Unsigned;
            if (result != 0) return FileSignatureState.Invalid;
            var provider = WTHelperProvDataFromStateData(data.StateData);
            if (provider == IntPtr.Zero) return FileSignatureState.Invalid;
            var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return FileSignatureState.Invalid;
            var certificate = WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero) return FileSignatureState.Invalid;
            var context = Marshal.PtrToStructure<ProviderCertPrefix>(certificate).Certificate;
            if (context == IntPtr.Zero) return FileSignatureState.Invalid;
            var encoded = Marshal.PtrToStructure<CertContextPrefix>(context);
            if (encoded.Size == 0 || encoded.Size > 1024 * 1024) return FileSignatureState.Invalid;
            var bytes = new byte[encoded.Size];
            Marshal.Copy(encoded.Encoded, bytes, 0, bytes.Length);
            using var subject = X509CertificateLoader.LoadCertificate(bytes);
            publisher = subject.GetNameInfo(X509NameType.SimpleName, false);
            return FileSignatureState.Valid;
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.FreeHGlobal(pointer); Marshal.FreeCoTaskMem(fileInfo.Path);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative { public uint Size; public IntPtr Path, Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertPrefix { public uint Size; public IntPtr Certificate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CertContextPrefix { public uint Encoding; public IntPtr Encoded; public uint Size; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr Policy, Sip;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, Url;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
}
