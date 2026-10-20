using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace TVCitation.VirtualDisplay {
    public sealed class VddRootDeviceInfo {
        public string InstanceId { get; set; }
        public string[] HardwareIds { get; set; }
        public uint ConfigManagerResult { get; set; }
        public uint StatusFlags { get; set; }
        public uint ProblemCode { get; set; }
    }
    public sealed class VddRootRegistration {
        public string InstanceId { get; set; }
        public bool Created { get; set; }
        public bool Registered { get; set; }
        public bool RebootRequired { get; set; }
        public int Error { get; set; }
        public string Stage { get; set; }
        public string Message { get; set; }
    }

    // Dedicated to the hardware ID in the pinned official VirtualDrivers package.
    // No caller-selected class, device ID, or arbitrary device removal is exposed.
    public static class VddRootDevice {
        public const string HardwareId = @"ROOT\MttVDD";
        public const string FixedInstanceId = @"ROOT\MttVDD\0000";
        static readonly Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        static readonly IntPtr InvalidHandle = new IntPtr(-1);
        const uint HardwareIdProperty = 1;
        const uint RegisterDevice = 0x19;
        const int InsufficientBuffer = 122;
        const int NoMoreItems = 259;
        const int InvalidData = 13;

        [StructLayout(LayoutKind.Sequential)] struct DeviceInfo {
            public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct InstallParameters {
            public uint Size, Flags, FlagsEx;
            public IntPtr Parent, MessageHandler, MessageContext, FileQueue, ClassInstallReserved;
            public uint Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string DriverPath;
        }
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo info, StringBuilder id, uint size, out uint required);
        [DllImport("setupapi.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", SetLastError=true)]
        static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid guid, IntPtr parent);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string id, ref Guid guid, string description, IntPtr parent, uint flags, ref DeviceInfo info);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo info, uint property, byte[] buffer, uint size);
        [DllImport("setupapi.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DeviceInfo info);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref DeviceInfo info, ref InstallParameters parameters);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

        static DeviceInfo NewInfo() { return new DeviceInfo { Size=(uint)Marshal.SizeOf(typeof(DeviceInfo)) }; }
        static string[] GetHardwareIds(IntPtr set, ref DeviceInfo info) {
            uint type, required;
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, HardwareIdProperty, out type, null, 0, out required)) {
                int error = Marshal.GetLastWin32Error();
                if (error == InvalidData) return new string[0];
                if (error != InsufficientBuffer) throw new Win32Exception(error, "Read device hardware IDs");
            }
            if (required == 0) return new string[0];
            if (required > 65536) throw new InvalidOperationException("Unexpected hardware ID property size.");
            var buffer = new byte[required];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, HardwareIdProperty, out type, buffer, (uint)buffer.Length, out required))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read device hardware IDs");
            if (type != 7) throw new InvalidOperationException("Hardware IDs must be REG_MULTI_SZ.");
            return Encoding.Unicode.GetString(buffer).Split(new [] {'\0'}, StringSplitOptions.RemoveEmptyEntries);
        }
        static string GetInstanceId(IntPtr set, ref DeviceInfo info) {
            var buffer = new StringBuilder(512); uint required;
            if (!SetupDiGetDeviceInstanceIdW(set, ref info, buffer, (uint)buffer.Capacity, out required))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read device instance ID");
            return buffer.ToString();
        }
        static bool IsVirtualDisplay(string[] ids) {
            foreach (var id in ids) if (String.Equals(id, HardwareId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        public static VddRootDeviceInfo[] Find() {
            var guid = DisplayClass;
            // Omitting DIGCF_PRESENT intentionally includes previously registered/disabled nodes.
            var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0);
            if (set == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error(), "Enumerate display devices");
            try {
                var results = new List<VddRootDeviceInfo>();
                for (uint index=0; ; index++) {
                    var info = NewInfo();
                    if (!SetupDiEnumDeviceInfo(set, index, ref info)) {
                        int error = Marshal.GetLastWin32Error();
                        if (error == NoMoreItems) break;
                        throw new Win32Exception(error, "Enumerate display devices");
                    }
                    var ids = GetHardwareIds(set, ref info);
                    if (!IsVirtualDisplay(ids)) continue;
                    uint status, problem;
                    uint code = CM_Get_DevNode_Status(out status, out problem, info.DevInst, 0);
                    results.Add(new VddRootDeviceInfo { InstanceId=GetInstanceId(set, ref info), HardwareIds=ids,
                        ConfigManagerResult=code, StatusFlags=status, ProblemCode=problem });
                }
                return results.ToArray();
            } finally { SetupDiDestroyDeviceInfoList(set); }
        }
        static VddRootRegistration Failure(VddRootRegistration result, string stage, int error) {
            result.Error=error; result.Stage=stage; result.Message=new Win32Exception(error).Message; return result;
        }
        public static VddRootRegistration EnsureRegistered() {
            var result = new VddRootRegistration { InstanceId=FixedInstanceId, Stage="administratorCheck" };
            using (var identity = WindowsIdentity.GetCurrent()) {
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return Failure(result, result.Stage, 5);
            }
            var existing = Find();
            if (existing.Length > 1) throw new InvalidOperationException("More than one MttVDD display hardware node exists; no device was created.");
            if (existing.Length == 1) {
                result.InstanceId=existing[0].InstanceId; result.Registered=true; result.Stage="existingDevice"; return result;
            }
            var guid = DisplayClass;
            var set = SetupDiCreateDeviceInfoList(ref guid, IntPtr.Zero);
            if (set == InvalidHandle) return Failure(result, "createDeviceInfoList", Marshal.GetLastWin32Error());
            try {
                var info = NewInfo();
                // A fixed instance avoids generating a second device on repeated/racing installs.
                // An existing conflicting instance fails closed instead of replacing its properties.
                if (!SetupDiCreateDeviceInfoW(set, FixedInstanceId, ref guid, "TVCITATION Virtual Display", IntPtr.Zero, 0, ref info))
                    return Failure(result, "createDeviceInfo", Marshal.GetLastWin32Error());
                var ids = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
                if (!SetupDiSetDeviceRegistryPropertyW(set, ref info, HardwareIdProperty, ids, (uint)ids.Length))
                    return Failure(result, "setHardwareId", Marshal.GetLastWin32Error());
                if (!SetupDiCallClassInstaller(RegisterDevice, set, ref info))
                    return Failure(result, "registerDevice", Marshal.GetLastWin32Error());
                result.Created=true; result.Registered=true; result.InstanceId=GetInstanceId(set, ref info);
                var parameters = new InstallParameters { Size=(uint)Marshal.SizeOf(typeof(InstallParameters)), DriverPath=String.Empty };
                if (!SetupDiGetDeviceInstallParamsW(set, ref info, ref parameters))
                    return Failure(result, "readInstallParameters", Marshal.GetLastWin32Error());
                result.RebootRequired=(parameters.Flags & (0x80u | 0x100u)) != 0;
                result.Stage="registeredDevice";
                return result;
            } finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }
}
