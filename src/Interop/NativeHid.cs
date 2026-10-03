using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XBridge.Interop;

// Raw HID output via HidD_SetOutputReport (SET_REPORT control transfer) - the only method that
// drives this pad's motors. Inbox hidusb.sys stack, no vendor driver.
internal static class NativeHid
{
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte[] lpReportBuffer, int reportBufferLength);

    // Shared handle so it coexists with DirectInput's grip on the same device.
    public static SafeFileHandle? OpenForWrite(string devicePath, out string? error)
    {
        error = null;

        var h = CreateFileW(devicePath, GENERIC_WRITE | GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        int e1 = Marshal.GetLastWin32Error();
        h.Dispose();

        h = CreateFileW(devicePath, GENERIC_WRITE,   // some stacks refuse GENERIC_READ on the output collection
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        int e2 = Marshal.GetLastWin32Error();
        h.Dispose();

        error = $"CreateFile failed (rw: {new Win32Exception(e1).Message}; wo: {new Win32Exception(e2).Message})";
        return null;
    }

    // Zero-access handle - enough to read the descriptor, never conflicts with readers or writers.
    public static SafeFileHandle OpenForQuery(string devicePath) =>
        CreateFileW(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    public static bool SetOutputReport(SafeFileHandle handle, byte[] buffer, out string error)
    {
        error = "";
        bool ok = HidD_SetOutputReport(handle, buffer, buffer.Length);
        if (!ok) error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return ok;
    }
}
