using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace TVCitation.VirtualDisplay
{
    public sealed class VddInfSignatureEvidence
    {
        public string InfPath { get; set; }
        public bool NativeSuccess { get; set; }
        public uint Error { get; set; }
        public string ErrorHex { get; set; }
        public string ErrorMessage { get; set; }
        public string Category { get; set; }
        public bool TrustedSignature { get; set; }
        public string CatalogPath { get; set; }
        public string DigitalSigner { get; set; }
        public string DigitalSignerVersion { get; set; }
        public uint SignerScore { get; set; }
    }
    // Read-only SetupAPI verification; no catalog or certificate is installed here.
    public static class VddSignature
    {
        public const uint AuthenticodeTrustedPublisher = 0xE0000241;
        public const uint AuthenticodeTrustNotEstablished = 0xE0000242;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct InfSignerInfo
        {
            public uint Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string CatalogFile;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DigitalSigner;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DigitalSignerVersion;
            public uint SignerScore;
        }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupVerifyInfFileW(string infName, IntPtr alternatePlatform, ref InfSignerInfo signerInfo);

        public static VddInfSignatureEvidence VerifyInf(string infPath)
        {
            string path = Path.GetFullPath(infPath);
            if (!File.Exists(path)) throw new FileNotFoundException("INF to verify does not exist.", path);
            var info = new InfSignerInfo { Size = (uint)Marshal.SizeOf(typeof(InfSignerInfo)) };
            bool success = SetupVerifyInfFileW(path, IntPtr.Zero, ref info);
            // FALSE + ERROR_AUTHENTICODE_TRUSTED_PUBLISHER is documented success for
            // trusted Authenticode-only packages. Never interpret arbitrary FALSE as valid.
            uint error = success ? 0 : unchecked((uint)Marshal.GetLastWin32Error());
            string category = success ? "SystemOrWhql" : error == AuthenticodeTrustedPublisher ? "AuthenticodeTrustedPublisher" : error == AuthenticodeTrustNotEstablished ? "AuthenticodePublisherNotEstablished" : "Rejected";
            return new VddInfSignatureEvidence {
                InfPath = path, NativeSuccess = success, Error = error,
                ErrorHex = "0x" + error.ToString("X8"), ErrorMessage = new Win32Exception(unchecked((int)error)).Message,
                Category = category, TrustedSignature = success || error == AuthenticodeTrustedPublisher,
                CatalogPath = info.CatalogFile, DigitalSigner = info.DigitalSigner,
                DigitalSignerVersion = info.DigitalSignerVersion, SignerScore = info.SignerScore
            };
        }
    }
}
