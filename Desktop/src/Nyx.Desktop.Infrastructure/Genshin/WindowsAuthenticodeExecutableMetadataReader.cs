using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nyx.Desktop.Infrastructure.Genshin;

[SupportedOSPlatform("windows")]
public sealed class WindowsAuthenticodeExecutableMetadataReader : IExecutableMetadataReader
{
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    public ExecutableMetadata Read(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        var signatureIsValid = VerifyEmbeddedSignature(executablePath);
        string? publisher = null;

        if (signatureIsValid)
        {
            try
            {
#pragma warning disable SYSLIB0057
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(executablePath));
#pragma warning restore SYSLIB0057
                publisher = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            }
            catch (CryptographicException)
            {
                signatureIsValid = false;
            }
        }

        return new(
            signatureIsValid,
            publisher,
            versionInfo.ProductName,
            versionInfo.FileDescription,
            versionInfo.ProductVersion);
    }

    private static bool VerifyEmbeddedSignature(string filePath)
    {
        var filePathPointer = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfo = new WinTrustFileInfo(filePathPointer);
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var data = new WinTrustData(fileInfoPointer);
            return WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, ref data) == 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(filePathPointer);
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        private readonly uint structureSize;
        private readonly IntPtr filePathPointer;
        private readonly IntPtr fileHandle;
        private readonly IntPtr knownSubjectPointer;

        public WinTrustFileInfo(IntPtr filePathPointer)
        {
            structureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            this.filePathPointer = filePathPointer;
            fileHandle = IntPtr.Zero;
            knownSubjectPointer = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        private readonly uint structureSize;
        private readonly IntPtr policyCallbackData;
        private readonly IntPtr sipClientData;
        private readonly uint uiChoice;
        private readonly uint revocationChecks;
        private readonly uint unionChoice;
        private readonly IntPtr fileInfoPointer;
        private readonly uint stateAction;
        private readonly IntPtr stateData;
        private readonly IntPtr urlReference;
        private readonly uint providerFlags;
        private readonly uint uiContext;

        public WinTrustData(IntPtr fileInfoPointer)
        {
            structureSize = (uint)Marshal.SizeOf<WinTrustData>();
            policyCallbackData = IntPtr.Zero;
            sipClientData = IntPtr.Zero;
            uiChoice = 2; // WTD_UI_NONE
            revocationChecks = 0; // WTD_REVOKE_NONE
            unionChoice = 1; // WTD_CHOICE_FILE
            this.fileInfoPointer = fileInfoPointer;
            stateAction = 0; // WTD_STATEACTION_IGNORE
            stateData = IntPtr.Zero;
            urlReference = IntPtr.Zero;
            // WTD_CACHE_ONLY_URL_RETRIEVAL: signature verification may use cached data only
            // and cannot fetch certificate or revocation information from the network.
            providerFlags = WtdCacheOnlyUrlRetrieval;
            uiContext = 0;
        }
    }
}
