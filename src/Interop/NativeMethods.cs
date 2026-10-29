using System.Runtime.InteropServices;

namespace XBridge.Interop;

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;

    public static bool EnsureConsole()
    {
        bool allocated = false;
        if (!AttachConsole(-1))
        {
            AllocConsole();
            allocated = true;
        }

        RedirectStandardStream(STD_OUTPUT_HANDLE, Console.SetOut);
        RedirectStandardStream(STD_ERROR_HANDLE, Console.SetError);
        return allocated;
    }

    private static void RedirectStandardStream(int stdHandle, Action<TextWriter> setStream)
    {
        try
        {
            var handle = GetStdHandle(stdHandle);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                var safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(handle, ownsHandle: false);
                var stream = new FileStream(safeHandle, FileAccess.Write);
                setStream(new StreamWriter(stream) { AutoFlush = true });
            }
        }
        catch { }
    }

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

    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_Size(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    private const int CR_SUCCESS = 0x00000000;
    private const int CR_BUFFER_SMALL = 0x0000001A;

    public static List<string>? GetPresentHidDeviceIds()
    {
        uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int cr = CM_Get_Device_ID_List_Size(out uint len, "HID", flags);
            if (cr != CR_SUCCESS || len == 0) return null;

            char[] buf = new char[len];
            cr = CM_Get_Device_ID_List("HID", buf, len, flags);
            if (cr == CR_SUCCESS)
                return new List<string>(new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries));
            if (cr != CR_BUFFER_SMALL) break;
        }
        return null;
    }
}
