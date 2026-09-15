using System.Runtime.InteropServices;

namespace XBridge.Interop;

// The RawInput device list is built by the kernel when a device arrives and is not affected
// by HidHide afterwards, so a listed-but-blocked device is visible to RawInput apps (browsers)
// until it is reconnected.
internal static class RawInputDevices
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint command, IntPtr data, ref uint size);

    private const uint RIM_TYPEHID = 2;
    private const uint RIDI_DEVICENAME = 0x20000007;

    public static bool IsListed(string hidDevicePath)
    {
        try
        {
            uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            uint count = 0;
            GetRawInputDeviceList(null, ref count, size);
            if (count == 0) return false;
            var list = new RAWINPUTDEVICELIST[count];
            count = GetRawInputDeviceList(list, ref count, size);
            if (count == unchecked((uint)-1)) return false;

            for (int i = 0; i < count; i++)
            {
                if (list[i].dwType != RIM_TYPEHID) continue;
                if (string.Equals(NameOf(list[i].hDevice), hidDevicePath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static string NameOf(IntPtr hDevice)
    {
        uint len = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref len);
        if (len == 0) return "";
        IntPtr buf = Marshal.AllocHGlobal((int)(len + 1) * 2);
        try
        {
            if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref len) == unchecked((uint)-1)) return "";
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
