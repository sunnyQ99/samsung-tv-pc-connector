using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace TVCitation.VirtualDisplay {
  public static class Inventory {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct DisplayDevice {
      public uint cb;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string DeviceName;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceString;
      public uint StateFlags;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceID;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceKey;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct DevMode {
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
      public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
      public uint dmFields;
      public int dmPositionX, dmPositionY;
      public uint dmDisplayOrientation, dmDisplayFixedOutput;
      public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
      public ushort dmLogPixels;
      public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
      public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice output, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool EnumDisplaySettingsEx(string device, int index, ref DevMode output, uint flags);
    public static DisplayDevice[] Devices(string device) {
      var result = new List<DisplayDevice>();
      if (String.IsNullOrEmpty(device)) device = null;
      for (uint i=0; i<64; i++) {
        var d = new DisplayDevice { cb=(uint)Marshal.SizeOf<DisplayDevice>() };
        // EDD_GET_DEVICE_INTERFACE_NAME applies to monitor enumeration, not adapter IDs.
        if (!EnumDisplayDevices(device, i, ref d, device == null ? 0u : 1u)) break;
        result.Add(d);
      }
      return result.ToArray();
    }
    public static DevMode[] Modes(string device, uint flags) {
      var result = new List<DevMode>();
      for (int i=0; i<10000; i++) {
        var d = new DevMode { dmSize=(ushort)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettingsEx(device, i, ref d, flags)) break;
        result.Add(d);
      }
      return result.ToArray();
    }
    public static DevMode Current(string device) {
      var d = new DevMode { dmSize=(ushort)Marshal.SizeOf<DevMode>() };
      if (!EnumDisplaySettingsEx(device, -1, ref d, 0)) throw new Exception("Cannot read display mode: "+device);
      return d;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    static extern int ChangeDisplaySettingsExW(string device, ref DevMode mode, IntPtr window, uint flags, IntPtr data);
    static void RequireOwnedVirtual(string device) {
      var roots=VddRootDevice.Find();
      if (roots.Length!=1 || !String.Equals(roots[0].InstanceId,VddRootDevice.FixedInstanceId,StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("No single owned VDD root device is present.");
      foreach (var adapter in Devices(null)) {
        if (!String.Equals(adapter.DeviceName,device,StringComparison.OrdinalIgnoreCase)) continue;
        if (!String.Equals(adapter.DeviceID,VddRootDevice.HardwareId,StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(adapter.DeviceID,VddRootDevice.FixedInstanceId,StringComparison.OrdinalIgnoreCase))
          throw new InvalidOperationException("The selected display is not the exact VDD root device.");
        if ((adapter.StateFlags & 4) != 0) throw new InvalidOperationException("Refusing to change a primary display.");
        return;
      }
      throw new InvalidOperationException("The VDD display disappeared.");
    }
    public static int Set4K120(string device, int x, int y, bool testOnly) {
      RequireOwnedVirtual(device);
      foreach (var enumerated in Modes(device,0)) {
        if (enumerated.dmPelsWidth!=3840 || enumerated.dmPelsHeight!=2160 || enumerated.dmDisplayFrequency!=120 ||
            enumerated.dmBitsPerPel!=32 || (enumerated.dmDisplayFlags & 2)!=0) continue;
        var requested=enumerated;
        requested.dmPositionX=x; requested.dmPositionY=y;
        requested.dmFields=0x7c0020; // position, BPP, width, height, display flags and refresh rate
        // No CDS_UPDATEREGISTRY or CDS_SET_PRIMARY: change only this virtual output dynamically.
        return ChangeDisplaySettingsExW(device,ref requested,IntPtr.Zero,testOnly ? 2u : 0u,IntPtr.Zero);
      }
      throw new InvalidOperationException("The VDD does not enumerate progressive 3840x2160 at 120 Hz, 32 BPP.");
    }
    public static int RestoreVirtual(string device, bool attached, uint width, uint height, uint hz, int x, int y) {
      RequireOwnedVirtual(device);
      var mode=new DevMode { dmSize=(ushort)Marshal.SizeOf<DevMode>(),dmPositionX=x,dmPositionY=y,
        dmPelsWidth=attached ? width : 0,dmPelsHeight=attached ? height : 0,
        dmBitsPerPel=32,dmDisplayFrequency=hz,dmFields=attached ? 0x7c0020u : 0x180020u };
      return ChangeDisplaySettingsExW(device,ref mode,IntPtr.Zero,0,IntPtr.Zero);
    }
  }
}
