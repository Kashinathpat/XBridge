using HidSharp;
using XBridge.Diagnostics;
using Vortice.DirectInput;

namespace XBridge.Core;

// One physical gamepad with both halves resolved: DirectInput (input) + raw HID (rumble) + PnP
// instance path (HidHide cloak). MatchKind is "exact" | "positional" | "none".
internal sealed record GamepadUnit(
    Guid DiInstanceGuid,
    string ProductName,
    string DiInterfacePath,
    string? HidDevicePath,
    int HidMaxOutputLength,
    string? HidHideInstancePath,
    string MatchKind)
{
    public bool HasRumblePath => !string.IsNullOrEmpty(HidDevicePath);
    public string Short => DiInstanceGuid.ToString()[..8];
}

// Pairs each DirectInput gamepad to its raw HID device by interface path, falling back to
// positional pairing when identical units make that path ambiguous.
internal static class DevicePairer
{
    public const ushort Vid = 0x0079;
    public const ushort Pid = 0x0006;

    private static string _lastSignature = "";   // so the per-unit line isn't logged on every re-scan

    public static List<GamepadUnit> Enumerate()
    {
        var diList = EnumerateDirectInput();
        var hidList = EnumerateHid();
        var pnpIds = HidHideManager.EnumerateInstancePaths().ToList();

        // Exact 1:1 on interface path (DIPROP_GUIDANDPATH <-> HidDevice.DevicePath).
        var exact = new Dictionary<Guid, HidDevice>();
        foreach (var (guid, _, ifacePath) in diList)
        {
            string n = Normalize(ifacePath);
            if (n.Length == 0) continue;
            var hid = hidList.FirstOrDefault(h => Normalize(h.DevicePath) == n);
            if (hid is not null) exact[guid] = hid;
        }

        bool distinct = exact.Values.Select(h => h.DevicePath)
                                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() == exact.Count;
        bool clean = diList.Count > 0 && exact.Count == diList.Count && distinct;

        // Identical units can make two DI instances report the same interface path and collapse
        // onto one HID node - when the match isn't a clean 1:1, pair by sorted order instead.
        var diOrdered = diList.OrderBy(d => d.guid).ToList();
        var pairing = new Dictionary<Guid, HidDevice?>();
        string mode;
        if (clean)
        {
            mode = "exact";
            foreach (var d in diOrdered) pairing[d.guid] = exact[d.guid];
        }
        else
        {
            mode = "positional";
            var hidOrdered = hidList.OrderBy(h => h.DevicePath, StringComparer.OrdinalIgnoreCase).ToList();
            Log.Warn($"Pairing: exact DI<->HID match is not 1:1 " +
                     $"({exact.Count} matched / {diList.Count} DI / {hidList.Count} HID) - pairing positionally");
            foreach (var h in hidList) Log.Warn($"  HID='{h.DevicePath}'");
            for (int i = 0; i < diOrdered.Count; i++)
                pairing[diOrdered[i].guid] = i < hidOrdered.Count ? hidOrdered[i] : null;
        }

        var pnpOrdered = pnpIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var units = new List<GamepadUnit>(diList.Count);
        for (int i = 0; i < diOrdered.Count; i++)
        {
            var (guid, name, ifacePath) = diOrdered[i];
            var hid = pairing.GetValueOrDefault(guid);

            // Derive the cloak id from the HID device path (reliable) not the DI path (may collide).
            string? cloakId = hid is not null
                ? DeriveInstanceId(hid.DevicePath, pnpIds, InstanceToken(hid.DevicePath))
                : DeriveInstanceId(ifacePath, pnpIds, InstanceToken(ifacePath));
            cloakId ??= i < pnpOrdered.Count ? pnpOrdered[i] : null;   // last resort: a distinct WMI id by position

            if (hid is null)
            {
                Log.Error($"Pairing[{guid.ToString()[..8]}]: no raw HID device paired (name='{name.Trim()}').");
                units.Add(new GamepadUnit(guid, name, ifacePath, null, 0, cloakId, "none"));
                continue;
            }
            units.Add(new GamepadUnit(guid, name, ifacePath, hid.DevicePath, hid.SafeMaxOut(), cloakId, mode));
        }

        // Log the per-unit summary only when the pairing set changes (not every re-scan).
        string sig = string.Join("|", units.Select(u => $"{u.Short}:{u.MatchKind}:{u.HidDevicePath}:{u.HidHideInstancePath}"));
        if (sig != _lastSignature)
        {
            _lastSignature = sig;
            foreach (var u in units)
                Log.Info($"Pairing[{u.Short}]: {u.MatchKind}  DI='{Ellipsis(u.DiInterfacePath)}'  <->  " +
                         $"HID='{Ellipsis(u.HidDevicePath ?? "<none>")}'  hidHide='{u.HidHideInstancePath ?? "<none>"}'  maxOut={u.HidMaxOutputLength}");
        }
        return units;
    }

    // Direct "is this instance still attached?" query - less racy than a full EnumDevices.
    public static bool IsAttached(Guid instanceGuid)
    {
        IDirectInput8? di = null;
        try { di = DInput.DirectInput8Create(); return di.IsDeviceAttached(instanceGuid); }
        catch { return true; }   // can't tell -> assume still there, don't tear a healthy session down
        finally { try { di?.Dispose(); } catch { } }
    }

    // Never cache DIPROP_GUIDANDPATH by guid: DirectInput reassigns InstanceGuids among
    // identical (same VID/PID, no serial) devices when the attached set changes.
    private static List<(Guid guid, string name, string ifacePath)> EnumerateDirectInput()
    {
        var result = new List<(Guid, string, string)>();

        // DI enumeration is racy right after a USB change; on an empty result, retry once.
        for (int attempt = 0; attempt < 2 && result.Count == 0; attempt++)
        {
            if (attempt == 1) Thread.Sleep(200);
            IDirectInput8? di = null;
            try
            {
                di = DInput.DirectInput8Create();
                foreach (var d in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
                {
                    var (vid, pid) = DecodeVidPid(d.ProductGuid);
                    if (vid != Vid || pid != Pid) continue;

                    string name = string.IsNullOrWhiteSpace(d.ProductName) ? "USB Joystick" : d.ProductName;
                    result.Add((d.InstanceGuid, name, ReadInterfacePath(di, d.InstanceGuid)));
                }
            }
            catch (Exception ex)
            {
                Log.Error("DevicePairer: DirectInput enumeration failed", ex);
            }
            finally
            {
                try { di?.Dispose(); } catch { }
            }
        }
        return result;
    }

    private static string ReadInterfacePath(IDirectInput8 di, Guid instanceGuid)
    {
        IDirectInputDevice8? dev = null;
        try
        {
            dev = di.CreateDevice(instanceGuid);
            try { dev.SetDataFormat<RawJoystickState>(); } catch { }
            return dev.Properties.InterfacePath ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn($"DevicePairer: could not read DIPROP_GUIDANDPATH for {instanceGuid.ToString()[..8]}: {ex.Message}");
            return "";
        }
        finally
        {
            try { dev?.Dispose(); } catch { }
        }
    }

    private static List<HidDevice> EnumerateHid()
    {
        // HidSharp can briefly return nothing while Windows re-enumerates the HID stack
        // after a sibling on the same hub/controller is unplugged - retry once.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var list = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
                if (list.Count > 0 || attempt == 1) return list;
            }
            catch (Exception ex)
            {
                Log.Error("DevicePairer: HidSharp enumeration failed", ex);
                if (attempt == 1) return new();
            }
            Thread.Sleep(200);
        }
    }

    private static string? ResolveHidHidePath(string token, List<string> pnpIds) =>
        token.Length == 0 ? null : pnpIds.FirstOrDefault(id => InstanceToken(id) == token);

    // \\?\hid#vid_0079&pid_0006#7&24717df1&0&0000#{guid}  ->  HID\VID_0079&PID_0006\7&24717DF1&0&0000
    private static string? DeriveInstanceId(string ifacePath, List<string> pnpIds, string token)
    {
        var m = ResolveHidHidePath(token, pnpIds);
        if (m is not null) return m;
        if (string.IsNullOrEmpty(ifacePath) || !ifacePath.Contains('#')) return null;
        var parts = ifacePath.Split('#', StringSplitOptions.RemoveEmptyEntries)
                             .Where(p => !p.StartsWith("{"))
                             .Select(p => p.Replace("\\\\?\\", "").Replace("\\?\\", ""))
                             .ToArray();
        return parts.Length >= 3 ? string.Join('\\', parts[..3]).ToUpperInvariant() : null;
    }

    private static string Normalize(string? path) => (path ?? "").Trim().ToLowerInvariant();

    // The PnP instance token, e.g. "7&24717df1&0&0000".
    private static string InstanceToken(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string p = path.Trim();

        if (p.Contains('#'))
        {
            foreach (var seg in p.Split('#', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seg.StartsWith("{")) continue;
                if (seg.Contains("vid_", StringComparison.OrdinalIgnoreCase) &&
                    seg.Contains("pid_", StringComparison.OrdinalIgnoreCase)) continue;
                if (seg.Replace("\\", "").Replace("?", "").Equals("hid", StringComparison.OrdinalIgnoreCase)) continue;
                return seg.ToLowerInvariant();
            }
            return "";
        }

        var s = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return s.Length > 0 ? s[^1].ToLowerInvariant() : "";
    }

    private static (ushort vid, ushort pid) DecodeVidPid(Guid productGuid)
    {
        Span<byte> b = stackalloc byte[16];
        productGuid.TryWriteBytes(b);
        uint dword = BitConverter.ToUInt32(b);
        return ((ushort)(dword & 0xFFFF), (ushort)(dword >> 16));
    }

    private static string Ellipsis(string s) => s.Length <= 72 ? s : "..." + s[^69..];
}

internal static class HidDeviceExtensions
{
    public static int SafeMaxOut(this HidDevice d)
    {
        try { return d.GetMaxOutputReportLength(); } catch { return 0; }
    }
}
