using System.Runtime.InteropServices;

namespace XBridge.Interop;

internal static class NativeMethods
{
    [DllImport("winmm.dll", ExactSpelling = true)]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", ExactSpelling = true)]
    public static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    public const int WM_DEVICECHANGE = 0x0219;
    public const int DBT_DEVICEARRIVAL = 0x8000;
    public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public const int DBT_DEVNODES_CHANGED = 0x0007;

    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    private static readonly Guid GuidDevInterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public short dbcc_name;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    public static IntPtr RegisterHidNotification(IntPtr hwnd)
    {
        var dbi = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = GuidDevInterfaceHid,
        };

        IntPtr buffer = Marshal.AllocHGlobal(dbi.dbcc_size);
        try
        {
            Marshal.StructureToPtr(dbi, buffer, false);
            return RegisterDeviceNotification(hwnd, buffer, DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void UnregisterHidNotification(IntPtr handle)
    {
        if (handle != IntPtr.Zero) UnregisterDeviceNotification(handle);
    }
}
