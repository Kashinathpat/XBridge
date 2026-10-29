using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XBridge.Interop;

internal sealed class PreparsedData : SafeHandleZeroOrMinusOneIsInvalid
{
    public PreparsedData() : base(true) { }
    protected override bool ReleaseHandle() => NativeHid.HidD_FreePreparsedData(handle);
}

// Raw HID output via HidD_SetOutputReport (SET_REPORT control transfer) - the only method that
// drives this pad's motors. Inbox hidusb.sys stack, no vendor driver.
internal static class NativeHid
{
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    private const uint GENERIC_WRITE = 0x40000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte[] lpReportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle device, out PreparsedData preparsed);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(PreparsedData preparsed, out HIDP_CAPS caps);

    private static readonly ConcurrentDictionary<string, bool> _gameControllerCache = new(StringComparer.OrdinalIgnoreCase);

    // Shared handle so it coexists with HidSharp's reader on the same device.
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

    // Generic Desktop Joystick/GamePad/Multi-axis usage. Returns null on probe failure (not cached).
    public static bool? IsGameController(string devicePath)
    {
        if (_gameControllerCache.TryGetValue(devicePath, out bool cached))
            return cached;

        var result = ProbeGameController(devicePath);
        if (result.HasValue)
            _gameControllerCache[devicePath] = result.Value;

        return result;
    }

    public static void PruneGameControllerCache(IEnumerable<string> activePaths)
    {
        var active = new HashSet<string>(activePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _gameControllerCache.Keys)
        {
            if (!active.Contains(key))
                _gameControllerCache.TryRemove(key, out _);
        }
    }

    private static bool? ProbeGameController(string devicePath)
    {
        using var h = OpenForQuery(devicePath);
        if (h.IsInvalid) return null;

        if (!HidD_GetPreparsedData(h, out var prep) || prep.IsInvalid) return null;
        using (prep)
        {
            if (HidP_GetCaps(prep, out var caps) != HIDP_STATUS_SUCCESS) return null;
            return caps.UsagePage == 0x01 && (caps.Usage is 0x04 or 0x05 or 0x08);
        }
    }

    public static bool SetOutputReport(SafeFileHandle handle, byte[] buffer, out string error)
    {
        error = "";
        bool ok = HidD_SetOutputReport(handle, buffer, buffer.Length);
        if (!ok) error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return ok;
    }
}
