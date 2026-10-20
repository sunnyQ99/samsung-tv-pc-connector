using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace TVCitation.CastAudio {
    public sealed class Endpoint {
        public string Id { get; set; }
        public string Name { get; set; }
        public string InterfaceName { get; set; }
        public uint State { get; set; }
    }
    public sealed class DefaultEndpoint {
        public int Role { get; set; }
        public string RoleName { get; set; }
        public string Id { get; set; }
    }
    public sealed class RestoreResult {
        public int Role { get; set; }
        public string Id { get; set; }
        public bool Changed { get; set; }
        public int HResult { get; set; }
    }
    public sealed class PublishedDriver {
        public string DriverStoreInf { get; set; }
        public string PublishedInf { get; set; }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey {
        public Guid Format; public uint Id;
        public PropertyKey(string format, uint id) { Format = new Guid(format); Id = id; }
    }
    // This module deliberately requires a 64-bit host.
    [StructLayout(LayoutKind.Explicit, Size=24)] internal struct PropVariant {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Value;
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class EnumeratorCom {}
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceCollection {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDevice {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] internal class PolicyCom {}
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr period, IntPtr minimum);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
    public static class Audio {
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
        static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        static string Text(IPropertyStore store, PropertyKey key) {
            PropVariant value; int hr = store.GetValue(ref key, out value);
            if (hr < 0) return null;
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Value) : null; }
            finally { PropVariantClear(ref value); }
        }
        public static Endpoint[] GetEndpoints() {
            var result = new List<Endpoint>();
            var enumerator = (IDeviceEnumerator)new EnumeratorCom(); IDeviceCollection collection = null;
            try {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out collection));
                uint count; Marshal.ThrowExceptionForHR(collection.GetCount(out count));
                for (uint i = 0; i < count; i++) {
                    IDevice device = null; IPropertyStore store = null;
                    try {
                        Marshal.ThrowExceptionForHR(collection.Item(i, out device));
                        string id; uint state; Marshal.ThrowExceptionForHR(device.GetId(out id));
                        Marshal.ThrowExceptionForHR(device.GetState(out state));
                        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out store));
                        result.Add(new Endpoint { Id = id, State = state,
                            Name = Text(store, new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14)),
                            InterfaceName = Text(store, new PropertyKey("026e516e-b814-414b-83cd-856d6fef4822", 2)) });
                    } finally { Release(store); Release(device); }
                }
            } finally { Release(collection); Release(enumerator); }
            return result.ToArray();
        }
        public static DefaultEndpoint[] GetDefaults() {
            var result = new List<DefaultEndpoint>(); var enumerator = (IDeviceEnumerator)new EnumeratorCom();
            string[] names = { "Console", "Multimedia", "Communications" };
            try {
                for (int role = 0; role < 3; role++) {
                    IDevice device = null; string id = null;
                    try {
                        int hr = enumerator.GetDefaultAudioEndpoint(0, role, out device);
                        if (hr >= 0) Marshal.ThrowExceptionForHR(device.GetId(out id));
                        else if (hr != unchecked((int)0x80070490)) Marshal.ThrowExceptionForHR(hr);
                        result.Add(new DefaultEndpoint { Role = role, RoleName = names[role], Id = id });
                    } finally { Release(device); }
                }
            } finally { Release(enumerator); }
            return result.ToArray();
        }
        public static RestoreResult Restore(int role, string id) {
            if (role < 0 || role > 2 || String.IsNullOrWhiteSpace(id)) throw new ArgumentException("Invalid endpoint or role");
            foreach (var item in GetDefaults()) if (item.Role == role && item.Id == id)
                return new RestoreResult { Role = role, Id = id, Changed = false, HResult = 0 };
            bool active = false; foreach (var endpoint in GetEndpoints()) if (endpoint.Id == id) active = true;
            if (!active) return new RestoreResult { Role = role, Id = id, HResult = unchecked((int)0x80070490) };
            var policy = (IPolicyConfig)new PolicyCom();
            try {
                int hr = policy.SetDefaultEndpoint(id, role);
                if (hr >= 0) {
                    bool matched = false;
                    foreach (var item in GetDefaults()) if (item.Role == role && item.Id == id) matched = true;
                    if (!matched) hr = unchecked((int)0x80004005);
                }
                return new RestoreResult { Role = role, Id = id, Changed = hr >= 0, HResult = hr };
            }
            finally { Release(policy); }
        }
    }
    public static class Driver {
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupGetInfPublishedNameW(string inf, StringBuilder path, uint size, out uint required);
        static string FileDigest(string path) {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream));
        }
        public static PublishedDriver GetPublishedDriver(string inf) {
            // SetupGetInfPublishedName requires a store INF, not an arbitrary source-package path.
            var storeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"DriverStore","FileRepository");
            var infName = Path.GetFileName(inf); var infHash = FileDigest(inf);
            var catName = "steamstreamingspeakers.cat"; var catHash = FileDigest(Path.Combine(Path.GetDirectoryName(inf),catName));
            PublishedDriver result = null;
            foreach (var directory in Directory.GetDirectories(storeRoot,infName + "_*",SearchOption.TopDirectoryOnly)) {
                var candidate = Path.Combine(directory,infName); var catalog = Path.Combine(directory,catName);
                if (!File.Exists(candidate) || !File.Exists(catalog) || FileDigest(candidate) != infHash || FileDigest(catalog) != catHash) continue;
                var published = new StringBuilder(32768); uint required;
                if (!SetupGetInfPublishedNameW(candidate,published,(uint)published.Capacity,out required)) continue;
                if (result != null) throw new InvalidOperationException("More than one identical Steam driver package is published.");
                result = new PublishedDriver { DriverStoreInf=candidate, PublishedInf=published.ToString() };
            }
            return result;
        }
        [DllImport("newdev.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool DiInstallDriverW(IntPtr parent, string path, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
        public static int Install(string path, out bool reboot) { return DiInstallDriverW(IntPtr.Zero, path, 0, out reboot) ? 0 : Marshal.GetLastWin32Error(); }
        [DllImport("wintrust.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool CryptCATAdminAcquireContext2(out IntPtr ctx, IntPtr subsystem, string algorithm, IntPtr policy, uint flags);
        [DllImport("wintrust.dll", SetLastError=true)] static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr ctx, IntPtr file, ref uint size, byte[] hash, uint flags);
        [DllImport("wintrust.dll")] static extern bool CryptCATAdminReleaseContext(IntPtr ctx, uint flags);
        [DllImport("wintrust.dll", ExactSpelling=true)] static extern int WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct CatalogInfo {
            public uint Size, Version;
            [MarshalAs(UnmanagedType.LPWStr)] public string Catalog;
            [MarshalAs(UnmanagedType.LPWStr)] public string MemberTag;
            [MarshalAs(UnmanagedType.LPWStr)] public string MemberFile;
            public IntPtr File, Hash; public uint HashSize; public IntPtr Context, Admin;
        }
        [StructLayout(LayoutKind.Sequential)] struct TrustData {
            public uint Size; public IntPtr Policy, Sip; public uint UI, Revocation, Choice;
            public IntPtr Info; public uint StateAction; public IntPtr State, Url;
            public uint Flags, UIContext;
        }
        public static bool IsCatalogMember(string catalog, string member) {
            foreach (string algorithm in new [] { "SHA256", "SHA1" }) {
                IntPtr ctx;
                if (!CryptCATAdminAcquireContext2(out ctx, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
                try {
                    using (var stream = new FileStream(member, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        uint length = 0; IntPtr handle = stream.SafeFileHandle.DangerousGetHandle();
                        if (!CryptCATAdminCalcHashFromFileHandle2(ctx, handle, ref length, null, 0) || length == 0) continue;
                        byte[] hash = new byte[length];
                        if (!CryptCATAdminCalcHashFromFileHandle2(ctx, handle, ref length, hash, 0)) continue;
                        var pinned = GCHandle.Alloc(hash, GCHandleType.Pinned);
                        IntPtr catalogPtr = IntPtr.Zero, trustPtr = IntPtr.Zero;
                        try {
                            var info = new CatalogInfo { Size=(uint)Marshal.SizeOf(typeof(CatalogInfo)), Catalog=catalog,
                                MemberTag=BitConverter.ToString(hash).Replace("-", ""), MemberFile=member, File=handle,
                                Hash=pinned.AddrOfPinnedObject(), HashSize=length, Admin=ctx };
                            catalogPtr=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(CatalogInfo))); Marshal.StructureToPtr(info,catalogPtr,false);
                            var data = new TrustData { Size=(uint)Marshal.SizeOf(typeof(TrustData)), UI=2, Choice=2, Info=catalogPtr, StateAction=1, Flags=0x1000 };
                            trustPtr=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustData))); Marshal.StructureToPtr(data,trustPtr,false);
                            Guid action = new Guid("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
                            int status=WinVerifyTrust(new IntPtr(-1),ref action,trustPtr);
                            data=(TrustData)Marshal.PtrToStructure(trustPtr,typeof(TrustData)); data.StateAction=2; Marshal.StructureToPtr(data,trustPtr,false);
                            WinVerifyTrust(new IntPtr(-1),ref action,trustPtr);
                            if (status == 0) return true;
                        } finally {
                            if (trustPtr != IntPtr.Zero) Marshal.FreeHGlobal(trustPtr);
                            if (catalogPtr != IntPtr.Zero) { Marshal.DestroyStructure(catalogPtr,typeof(CatalogInfo)); Marshal.FreeHGlobal(catalogPtr); }
                            pinned.Free();
                        }
                    }
                } finally { CryptCATAdminReleaseContext(ctx,0); }
            }
            return false;
        }
    }
}
