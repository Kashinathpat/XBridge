using System.Security.Cryptography;
using System.Text;
using HidSharp;
using XBridge.Config;
using XBridge.Diagnostics;
using XBridge.Interop;

namespace XBridge.Core;

// One physical gamepad: raw HID device (input + rumble) and PnP instance path (HidHide cloak).
internal sealed record GamepadUnit(
    Guid InstanceGuid,
    ushort Vid,
    ushort Pid,
    string ProductName,
    string HidDevicePath,
    int HidMaxOutputLength,
    string? HidHideInstancePath)
{
    public string Short => InstanceGuid.ToString()[..8];
}

internal static class DevicePairer
{
    private static string _lastSignature = "";
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

        var pnpIds = HidHideManager.EnumerateInstancePaths(devices, forceRefresh: forceRefresh).ToList();
        bool pnpRefreshed = forceRefresh;

        var units = new List<GamepadUnit>(hidList.Count);
        var assignedCloakIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < hidList.Count; i++)
        {
            var hid = hidList[i];
            ushort vid = (ushort)hid.VendorID;
            ushort pid = (ushort)hid.ProductID;
            string name = GetSafeProductName(hid);
            Guid guid = CreateGuidFromPath(hid.DevicePath);
            int maxOut = hid.SafeMaxOut();

            string token = InstanceToken(hid.DevicePath);
            string? cloakId = DeriveInstanceId(hid.DevicePath, pnpIds, token);
            if (cloakId is not null && assignedCloakIds.Contains(cloakId))
                cloakId = null;

            if (cloakId is null && token.Length > 0 && !pnpRefreshed)
            {
                pnpIds = HidHideManager.EnumerateInstancePaths(devices, forceRefresh: true).ToList();
                pnpRefreshed = true;
                cloakId = DeriveInstanceId(hid.DevicePath, pnpIds, token);
                if (cloakId is not null && assignedCloakIds.Contains(cloakId))
                    cloakId = null;
            }

            if (cloakId is null)
            {
                cloakId = pnpIds.FirstOrDefault(id =>
                    HidHideManager.TryExtractVidPid(id, out var v, out var p)
                    && v == vid && p == pid
                    && !assignedCloakIds.Contains(id));
            }

            if (cloakId is not null)
                assignedCloakIds.Add(cloakId);

            units.Add(new GamepadUnit(guid, vid, pid, name, hid.DevicePath, maxOut, cloakId));
        }

        string sig = string.Join("|", units.Select(u => $"{u.Short}:{u.HidDevicePath}:{u.HidHideInstancePath}"));
        if (sig != _lastSignature)
        {
            _lastSignature = sig;
            foreach (var u in units)
                Log.Info($"Device[{u.Short}]: VID {u.Vid:X4}/PID {u.Pid:X4} name=\"{u.ProductName}\" HID='{Ellipsis(u.HidDevicePath)}' hidHide='{u.HidHideInstancePath ?? "<none>"}' maxOut={u.HidMaxOutputLength}");
        }
        _lastHidPaths = currentPaths;
        _lastUnits = units;
        return units;
    }

    private static List<HidDevice> EnumerateHid(IReadOnlyList<DeviceId> devices, bool allowRetry)
    {
        int maxAttempts = allowRetry ? 2 : 1;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var matching = DeviceList.Local.GetHidDevices()
                    .Where(h => devices.Any(x => x.Matches((ushort)h.VendorID, (ushort)h.ProductID)))
                    .ToList();

                NativeHid.PruneGameControllerCache(matching.Select(m => m.DevicePath));

                bool anyProbeFailed = false;
                var list = new List<HidDevice>(matching.Count);

                foreach (var h in matching)
                {
                    bool? isController = NativeHid.IsGameController(h.DevicePath);
                    if (isController == true)
                        list.Add(h);
                    else if (isController == null)
                        anyProbeFailed = true;
                }

                if ((list.Count > 0 && !anyProbeFailed) || attempt == maxAttempts - 1)
                    return list;
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

    private static string GetSafeProductName(HidDevice dev)
    {
        try
        {
            string name = dev.GetProductName();
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { }
        return $"Gamepad {dev.VendorID:X4}:{dev.ProductID:X4}";
    }

    private static Guid CreateGuidFromPath(string path)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(path.Trim().ToLowerInvariant()));
        return new Guid(hash);
    }

    private static string? ResolveHidHidePath(string token, List<string> pnpIds) =>
        token.Length == 0 ? null : pnpIds.FirstOrDefault(id => InstanceToken(id) == token);

    private static string? DeriveInstanceId(string path, List<string> pnpIds, string token)
    {
        var m = ResolveHidHidePath(token, pnpIds);
        if (m is not null) return m;
        if (string.IsNullOrEmpty(path) || !path.Contains('#')) return null;
        var parts = path.Split('#', StringSplitOptions.RemoveEmptyEntries)
                             .Where(p => !p.StartsWith("{"))
                             .Select(p => p.Replace("\\\\?\\", "").Replace("\\?\\", ""))
                             .ToArray();
        return parts.Length >= 3 ? string.Join('\\', parts[..3]).ToUpperInvariant() : null;
    }

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

    private static string Ellipsis(string s) => s.Length <= 72 ? s : "..." + s[^69..];
}

internal static class HidDeviceExtensions
{
    public static int SafeMaxOut(this HidDevice d)
    {
        try { return d.GetMaxOutputReportLength(); } catch { return 0; }
    }
}
