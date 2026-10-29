using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using XBridge.Diagnostics;
using XBridge.Input;

namespace XBridge.Interop;

// Decodes input reports with Windows' own HID parser - the one DirectInput uses - so decoded values,
// including signed logical ranges such as -127..127 sticks, match DirectInput exactly.
internal sealed class HidInputParser : IDisposable
{
    private const int HidP_Input = 0;
    private const ushort PageGenericDesktop = 0x01;
    private const ushort PageButton = 0x09;

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort UsageMin;   // NotRange.Usage when IsRange == 0
        [FieldOffset(58)] public ushort UsageMax;
    }

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] caps, ref ushort length, PreparsedData preparsed);

    [DllImport("hid.dll")]
    private static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, PreparsedData preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, PreparsedData preparsed, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint value, PreparsedData preparsed, byte[] report, uint reportLength);

    private readonly record struct ValueField(byte ReportId, ushort LinkCollection, ushort Usage, int Bits, int Min, int Max);

    private readonly PreparsedData _preparsed;
    private readonly ushort[] _pressed;
    private readonly Dictionary<byte, bool[]> _buttonsByReport = new();
    private readonly ValueField[] _values;

    public int ReportLength { get; }

    public HidInputParser(string devicePath)
    {
        using (var h = NativeHid.OpenForQuery(devicePath))
        {
            if (h.IsInvalid)
                throw new IOException("Could not open the HID device: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (!NativeHid.HidD_GetPreparsedData(h, out _preparsed))
            {
                int err = Marshal.GetLastWin32Error();
                _preparsed.Dispose();
                throw new IOException("Could not read the HID descriptor: " + new Win32Exception(err).Message);
            }
        }

        try
        {
            if (NativeHid.HidP_GetCaps(_preparsed, out var caps) != NativeHid.HIDP_STATUS_SUCCESS)
                throw new IOException("Could not parse the HID descriptor.");
            ReportLength = caps.InputReportByteLength;
            _pressed = new ushort[Math.Max(1, HidP_MaxUsageListLength(HidP_Input, PageButton, _preparsed))];

            var valueCaps = new HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
            ushort count = (ushort)valueCaps.Length;
            if (count > 0 && HidP_GetValueCaps(HidP_Input, valueCaps, ref count, _preparsed) != NativeHid.HIDP_STATUS_SUCCESS)
                count = 0;

            // One usage carrying several values (a value array) has a single JoystickState slot, so only its
            // first value is used - the element HidP_GetUsageValue returns.
            var fields = new List<ValueField>();
            for (int i = 0; i < count; i++)
            {
                var vc = valueCaps[i];
                if (vc.UsagePage != PageGenericDesktop) continue;
                if (vc.IsRange == 0 && vc.ReportCount > 1)
                    Log.Warn($"HID usage 0x{vc.UsageMin:X2} reports {vc.ReportCount} values as an array - only the first is used");
                ushort last = vc.IsRange != 0 ? vc.UsageMax : vc.UsageMin;
                for (int u = vc.UsageMin; u <= last; u++)
                    fields.Add(new ValueField(vc.ReportID, vc.LinkCollection, (ushort)u, vc.BitSize, vc.LogicalMin, vc.LogicalMax));
            }
            _values = fields.ToArray();
        }
        catch
        {
            _preparsed.Dispose();
            throw;
        }
    }

    // Updates only what this report carries, so a report that lacks buttons or an axis leaves it as-is.
    // Conventions match DirectInput: Button N -> Buttons[N-1], axes scaled to 0..65535, hat -> POV in
    // hundredths of a degree (-1 = centred).
    public void Parse(byte[] report, JoystickState state)
    {
        uint len = (uint)ReportLength;

        byte reportId = report[0];

        // HidP_GetUsages reports only the buttons in this report, so a pad that splits buttons across
        // report IDs would release the others on every report; keep each report's set and merge.
        uint n = (uint)_pressed.Length;
        if (HidP_GetUsages(HidP_Input, PageButton, 0, _pressed, ref n, _preparsed, report, len) == NativeHid.HIDP_STATUS_SUCCESS)
        {
            if (!_buttonsByReport.TryGetValue(reportId, out var mine))
                _buttonsByReport[reportId] = mine = new bool[state.Buttons.Length];
            Array.Clear(mine);
            for (int i = 0; i < n; i++)
            {
                int b = _pressed[i] - 1;
                if ((uint)b < (uint)mine.Length) mine[b] = true;
            }

            Array.Clear(state.Buttons);
            foreach (var set in _buttonsByReport.Values)
                for (int b = 0; b < set.Length && b < state.Buttons.Length; b++)
                    if (set[b]) state.Buttons[b] = true;
        }

        // Each field belongs to one report and link collection; matching both keeps a usage that
        // appears in several reports from being scaled with another report's range.
        foreach (var f in _values)
        {
            if (f.ReportId != reportId) continue;
            if (HidP_GetUsageValue(HidP_Input, PageGenericDesktop, f.LinkCollection, f.Usage, out uint raw,
                    _preparsed, report, len) != NativeHid.HIDP_STATUS_SUCCESS)
                continue;
            long v = SignExtend(raw, f.Bits, f.Min);

            switch (f.Usage)
            {
                case 0x30: state.X = ScaleAxis(v, f.Min, f.Max); break;
                case 0x31: state.Y = ScaleAxis(v, f.Min, f.Max); break;
                case 0x32: state.Z = ScaleAxis(v, f.Min, f.Max); break;
                case 0x33: state.RotationX = ScaleAxis(v, f.Min, f.Max); break;
                case 0x34: state.RotationY = ScaleAxis(v, f.Min, f.Max); break;
                case 0x35: state.RotationZ = ScaleAxis(v, f.Min, f.Max); break;
                case 0x39:
                    state.PointOfViewControllers[0] = v < f.Min || v > f.Max
                        ? -1
                        : (int)((v - f.Min) * 36000 / ((long)f.Max - f.Min + 1));
                    break;
            }
        }
    }

    private static long SignExtend(uint raw, int bits, int min)
    {
        if (min >= 0 || bits is <= 0 or > 32) return raw;
        if (bits == 32) return (int)raw;
        return (raw & (1u << (bits - 1))) != 0 ? (long)raw - (1L << bits) : raw;
    }

    // Piecewise around the centre like DirectInput's default calibration, so rest lands on exactly 32767.
    // 64-bit throughout: a descriptor is untrusted input and may declare a full 32-bit range.
    private static int ScaleAxis(long v, long min, long max)
    {
        if (max <= min) return 32767;
        v = Math.Clamp(v, min, max);
        long centre = (min + max + 1) >> 1;
        return (int)(v <= centre
            ? (v - min) * 32767 / Math.Max(1, centre - min)
            : 32767 + (v - centre) * 32768 / Math.Max(1, max - centre));
    }

    public void Dispose() => _preparsed.Dispose();
}
