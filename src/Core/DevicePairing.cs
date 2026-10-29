using HidSharp;
using XBridge.Config;
using XBridge.Diagnostics;
using Vortice.DirectInput;

namespace XBridge.Core;

// One physical gamepad: its DirectInput identity (enumeration), raw HID device (input + rumble) and PnP instance
// path (HidHide cloak). MatchKind is "exact" | "positional" | "none".
internal sealed record GamepadUnit(
    Guid DiInstanceGuid,
    ushort Vid,
    ushort Pid,
    string ProductName,
    string DiInterfacePath,
    string? HidDevicePath,
    int HidMaxOutputLength,
    string? HidHideInstancePath,
    string MatchKind)
{
    // A paired raw HID device exists (used for the stable session key), regardless of rumble.
    public bool HasHidPath => !string.IsNullOrEmpty(HidDevicePath);
    public string Short => DiInstanceGuid.ToString()[..8];
}

// Pairs each DirectInput gamepad to its raw HID device by interface path, falling back to
// positional pairing when identical units make that path ambiguous.
internal static class DevicePairer
{
    private static string _lastSignature = "";   // so the per-unit line isn't logged on every re-scan
    private static HashSet<string> _lastHidPaths = new(StringComparer.OrdinalIgnoreCase);
    private static List<GamepadUnit> _lastUnits = new();

    public static List<GamepadUnit> Enumerate(
        IReadOnlyList<DeviceId> devices,
        bool allSessionsHealthy = false,
        bool forceRefresh = false,
        bool hasPending = false,
        bool hasRunningSessions = false)
    {
        bool allowRetry = forceRefresh || hasPending || hasRunningSessions;
        var hidList = EnumerateHid(devices, allowRetry);
        var currentPaths = new HashSet<string>(hidList.Select(h => h.DevicePath), StringComparer.OrdinalIgnoreCase);
        if (!forceRefresh && allSessionsHealthy && currentPaths.SetEquals(_lastHidPaths))
            return _lastUnits;

        var diList = EnumerateDirectInput(devices, allowRetry);
        var pnpIds = HidHideManager.EnumerateInstancePaths(devices, forceRefresh: forceRefresh).ToList();
        bool wmiRefreshed = forceRefresh;

        // Pair within each VID/PID group so a pad is never matched to another model's HID node or
        // cloak id. Unambiguous exact matches (DIPROP_GUIDANDPATH == HidDevice.DevicePath) are kept;
        // identical units can report the same interface path and collapse onto one HID node, so the
        // rest of the group is paired by sorted order.
        var units = new List<GamepadUnit>(diList.Count);
        foreach (var group in diList.GroupBy(d => (d.vid, d.pid)))
        {
            var dis = group.OrderBy(d => d.guid).ToList();
            var hids = hidList.Where(h => h.VendorID == group.Key.vid && h.ProductID == group.Key.pid)
                              .OrderBy(h => h.DevicePath, StringComparer.OrdinalIgnoreCase).ToList();
            var groupPnp = pnpIds.Where(id => HidHideManager.TryExtractVidPid(id, out var v, out var p)
                                              && v == group.Key.vid && p == group.Key.pid)
                                 .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            var matchedBy = dis.ToDictionary(d => d.guid,
                d => Normalize(d.ifacePath) is { Length: > 0 } n ? hids.FirstOrDefault(h => Normalize(h.DevicePath) == n) : null);
            var unique = matchedBy.Where(kv => kv.Value is not null
                                               && matchedBy.Count(o => ReferenceEquals(o.Value, kv.Value)) == 1)
                                  .ToDictionary(kv => kv.Key, kv => kv.Value!);
            var spareHids = hids.Where(h => !unique.ContainsValue(h)).ToList();
            var leftover = dis.Where(d => !unique.ContainsKey(d.guid)).ToList();
            if (leftover.Count > 0)
                Log.Warn($"Pairing VID {group.Key.vid:X4}/PID {group.Key.pid:X4}: {leftover.Count} of {dis.Count} pad(s) " +
                         $"have no unambiguous HID match - pairing those by order ({spareHids.Count} spare HID node(s))");

            for (int i = 0; i < dis.Count; i++)
            {
                var (guid, vid, pid, name, ifacePath) = dis[i];
                HidDevice? hid;
                string mode;
                if (unique.TryGetValue(guid, out var exactHid)) { hid = exactHid; mode = "exact"; }
                else
                {
                    int slot = leftover.FindIndex(d => d.guid == guid);
                    hid = slot < spareHids.Count ? spareHids[slot] : null;
                    mode = "positional";
                }

                // Derive the cloak id from the HID device path (reliable) not the DI path (may collide).
                string token = hid is not null ? InstanceToken(hid.DevicePath) : InstanceToken(ifacePath);
                string? cloakId = hid is not null
                    ? DeriveInstanceId(hid.DevicePath, pnpIds, token)
                    : DeriveInstanceId(ifacePath, pnpIds, token);

                if (cloakId is null && token.Length > 0 && !wmiRefreshed)
                {
                    pnpIds = HidHideManager.EnumerateInstancePaths(devices, forceRefresh: true).ToList();
                    wmiRefreshed = true;
                    cloakId = hid is not null
                        ? DeriveInstanceId(hid.DevicePath, pnpIds, token)
                        : DeriveInstanceId(ifacePath, pnpIds, token);
                }

                cloakId ??= i < groupPnp.Count ? groupPnp[i] : null;   // last resort: a same-model WMI id by position

                if (hid is null)
                {
                    Log.Error($"Pairing[{guid.ToString()[..8]}]: no raw HID device paired (name='{name.Trim()}').");
                    units.Add(new GamepadUnit(guid, vid, pid, name, ifacePath, null, 0, cloakId, "none"));
                    continue;
                }
                units.Add(new GamepadUnit(guid, vid, pid, name, ifacePath, hid.DevicePath, hid.SafeMaxOut(), cloakId, mode));
            }
        }

        // Log the per-unit summary only when the pairing set changes (not every re-scan).
        string sig = string.Join("|", units.Select(u => $"{u.Short}:{u.MatchKind}:{u.HidDevicePath}:{u.HidHideInstancePath}"));
        if (sig != _lastSignature)
        {
            _lastSignature = sig;
            foreach (var u in units)
                Log.Info($"Pairing[{u.Short}]: {u.MatchKind}  VID {u.Vid:X4}/PID {u.Pid:X4}  DI='{Ellipsis(u.DiInterfacePath)}'  <->  " +
                         $"HID='{Ellipsis(u.HidDevicePath ?? "<none>")}'  hidHide='{u.HidHideInstancePath ?? "<none>"}'  maxOut={u.HidMaxOutputLength}");
        }
        _lastHidPaths = currentPaths;
        _lastUnits = units;
        return units;
    }

    // Never cache DIPROP_GUIDANDPATH by guid: DirectInput reassigns InstanceGuids among
    // identical (same VID/PID, no serial) devices when the attached set changes.
    private static List<(Guid guid, ushort vid, ushort pid, string name, string ifacePath)> EnumerateDirectInput(
        IReadOnlyList<DeviceId> devices, bool allowRetry)
    {
        var result = new List<(Guid, ushort, ushort, string, string)>();

        // DirectInput enum is racy after USB change; retries once when needed.
        int maxAttempts = allowRetry ? 2 : 1;
        for (int attempt = 0; attempt < maxAttempts && result.Count == 0; attempt++)
        {
            if (attempt > 0) Thread.Sleep(200);
            IDirectInput8? di = null;
            try
            {
                di = DInput.DirectInput8Create();
                foreach (var d in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
                {
                    var (vid, pid) = DecodeVidPid(d.ProductGuid);
                    if (!devices.Any(x => x.Matches(vid, pid))) continue;

                    string name = string.IsNullOrWhiteSpace(d.ProductName) ? "Gamepad" : d.ProductName;
                    result.Add((d.InstanceGuid, vid, pid, name, ReadInterfacePath(di, d.InstanceGuid)));
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

    private static List<HidDevice> EnumerateHid(IReadOnlyList<DeviceId> devices, bool allowRetry)
    {
        // HidSharp can briefly return empty; retries once when needed.
        int maxAttempts = allowRetry ? 2 : 1;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var list = DeviceList.Local.GetHidDevices()
                    .Where(h => devices.Any(x => x.Matches((ushort)h.VendorID, (ushort)h.ProductID)))
                    .ToList();
                if (list.Count > 0 || attempt == maxAttempts - 1) return list;
            }
            catch (Exception ex)
            {
                Log.Error("DevicePairer: HidSharp enumeration failed", ex);
                if (attempt == maxAttempts - 1) return new();
            }
            Thread.Sleep(200);
        }
        return new();
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
                if (seg.Contains("vid_", StringComparison.OrdinalIgnoreCase) ||
                    seg.Contains("vid&", StringComparison.OrdinalIgnoreCase)) continue;
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
