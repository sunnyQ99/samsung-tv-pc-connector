using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace TVCitation.VirtualDisplay
{
    // Fixed paths only. Existing HKLM\SOFTWARE is opened with CREATE_SUB_KEY, never KEY_WRITE.
    // No security descriptor, ACL, inheritance, privileges, or registry links are modified.
    public static class VddRegistry
    {
        private static readonly IntPtr Hklm = new IntPtr(unchecked((int)0x80000002));
        private const uint Query = 1, Set = 2, Create = 4, View64 = 0x100;
        private const string Parent = @"SOFTWARE\MikeTheTech";
        private const string Leaf = @"SOFTWARE\MikeTheTech\VirtualDisplayDriver";
        public static string ExpectedDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"TVCITATION\VirtualDisplayDriver"); } }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegOpenKeyExW(IntPtr key, string subKey, uint options, uint access, out IntPtr result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegCreateKeyExW(IntPtr key, string subKey, uint reserved, string keyClass, uint options, uint access, IntPtr security, out IntPtr result, out uint disposition);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegQueryInfoKeyW(IntPtr key, IntPtr keyClass, IntPtr classLength, IntPtr reserved, out uint subKeys, IntPtr maxSubKeyLength, IntPtr maxClassLength, out uint values, IntPtr maxValueNameLength, IntPtr maxValueLength, IntPtr securityLength, IntPtr lastWriteTime);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegQueryValueExW(IntPtr key, string name, IntPtr reserved, out uint type, byte[] data, ref uint bytes);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegSetValueExW(IntPtr key, string name, uint reserved, uint type, byte[] data, uint bytes);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegDeleteValueW(IntPtr key, string name);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegDeleteKeyExW(IntPtr key, string subKey, uint view, uint reserved);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern int RegCloseKey(IntPtr key);

        private static void RequireAdmin()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("VDD registry changes require an elevated administrator.");
        }
        private static void Check(int result, string operation)
        {
            if (result != 0) throw new Win32Exception(result, operation + ": " + new Win32Exception(result).Message);
        }
        private static IntPtr Open(string path, uint rights, bool absentAllowed)
        {
            IntPtr handle;
            int result = RegOpenKeyExW(Hklm, path, 0, rights | View64, out handle);
            if (absentAllowed && (result == 2 || result == 3)) return IntPtr.Zero;
            Check(result, "RegOpenKeyExW HKLM\\" + path);
            return handle;
        }
        private static bool CreateChild(string parent, string child, uint childRights)
        {
            RequireAdmin();
            IntPtr parentHandle = Open(parent, Create, false), childHandle = IntPtr.Zero;
            try
            {
                uint disposition;
                Check(RegCreateKeyExW(parentHandle, child, 0, null, 0, childRights | View64, IntPtr.Zero, out childHandle, out disposition), "RegCreateKeyExW " + child);
                if (disposition != 1 && disposition != 2) throw new InvalidOperationException("Unexpected registry creation disposition.");
                return disposition == 1;
            }
            finally { if (childHandle != IntPtr.Zero) RegCloseKey(childHandle); RegCloseKey(parentHandle); }
        }
        public static bool CreateParent() { return CreateChild("SOFTWARE", "MikeTheTech", Create); }
        public static bool CreateLeaf() { return CreateChild(Parent, "VirtualDisplayDriver", Query | Set); }
        private static bool Empty(IntPtr handle)
        {
            uint keys, values;
            Check(RegQueryInfoKeyW(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out keys, IntPtr.Zero, IntPtr.Zero, out values, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "RegQueryInfoKeyW VDD");
            return keys == 0 && values == 0;
        }
        public static void SetPathOnEmptyLeaf()
        {
            RequireAdmin();
            IntPtr handle = Open(Leaf, Query | Set, false);
            try
            {
                if (!Empty(handle)) throw new InvalidOperationException("VDD registry leaf contains existing data; no values were overwritten.");
                byte[] value = Encoding.Unicode.GetBytes(ExpectedDirectory + "\0");
                Check(RegSetValueExW(handle, "VDDPATH", 0, 1, value, (uint)value.Length), "RegSetValueExW VDDPATH");
            }
            finally { RegCloseKey(handle); }
        }
        public static void RemoveOwnedLeaf() { RemoveLeaf(true); }
        public static void RemoveEmptyLeaf() { RemoveLeaf(false); }
        private static void RemoveLeaf(bool mayRemoveOwnedValue)
        {
            RequireAdmin();
            IntPtr handle = Open(Leaf, Query | (mayRemoveOwnedValue ? Set : 0), true);
            if (handle == IntPtr.Zero) return;
            try
            {
                if (mayRemoveOwnedValue)
                {
                    uint type, size = 0;
                    int found = RegQueryValueExW(handle, "VDDPATH", IntPtr.Zero, out type, null, ref size);
                    if (found != 2)
                    {
                        Check(found, "RegQueryValueExW VDDPATH size");
                        if (type != 1 || size == 0 || size > 65536) throw new InvalidOperationException("VDDPATH type or size changed; configuration retained.");
                        byte[] value = new byte[size];
                        Check(RegQueryValueExW(handle, "VDDPATH", IntPtr.Zero, out type, value, ref size), "RegQueryValueExW VDDPATH");
                        if (type != 1 || (size % 2) != 0 || !String.Equals(Encoding.Unicode.GetString(value, 0, (int)size).TrimEnd('\0'), ExpectedDirectory, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("VDDPATH changed; configuration retained.");
                        Check(RegDeleteValueW(handle, "VDDPATH"), "RegDeleteValueW VDDPATH");
                    }
                }
                if (!Empty(handle)) throw new InvalidOperationException("VDD registry contains unrelated data; key retained.");
                // RegDeleteKeyEx checks DELETE on this fixed child, not KEY_WRITE on SOFTWARE.
                Check(RegDeleteKeyExW(Hklm, Leaf, View64, 0), "RegDeleteKeyExW VDD leaf");
            }
            finally { RegCloseKey(handle); }
        }
        public static bool RemoveEmptyOwnedParent()
        {
            RequireAdmin();
            IntPtr handle = Open(Parent, Query, true);
            if (handle == IntPtr.Zero) return false;
            try
            {
                if (!Empty(handle)) return false;
                Check(RegDeleteKeyExW(Hklm, Parent, View64, 0), "RegDeleteKeyExW VDD parent");
                return true;
            }
            finally { RegCloseKey(handle); }
        }
    }
}
