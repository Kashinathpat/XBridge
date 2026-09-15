using System.Management;
using System.Text.RegularExpressions;
using Nefarius.Drivers.HidHide;
using Nefarius.Drivers.HidHide.Exceptions;
using XBridge.Config;
using XBridge.Diagnostics;

namespace XBridge.Core;

internal enum HidHideAccess { Ok, NotInstalled, AccessDenied, ServiceUnavailable, Failed }

// Every call opens its own short-lived \\.\HidHide handle, so calls are serialized here and
// retried if the driver is momentarily held by another API consumer (e.g. its config GUI).
internal sealed class HidHideManager
{
    private const int OpenAttempts = 6;
    private const int OpenRetryMs = 200;

    private readonly object _gate = new();
    private readonly IReadOnlyList<DeviceId> _devices;

    public HidHideManager(IReadOnlyList<DeviceId> devices) => _devices = devices;

    public string? LastError { get; private set; }

    // True when the last failure was "driver handle busy" (another API consumer, e.g. the
    // HidHide Configuration Client) rather than a hard error - it clears when that app closes.
    public bool LastErrorWasBusy { get; private set; }

    private static T Call<T>(Func<IHidHideControlService, T> fn)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return fn(new HidHideControlService()); }
            catch (HidHideDriverAccessFailedException) when (attempt < OpenAttempts) { Thread.Sleep(OpenRetryMs); }
        }
    }

    public string DriverInfo
    {
        get
        {
            try { return Call(s => s.IsInstalled ? $"HidHide {s.LocalDriverVersion}" : "not installed"); }
            catch (Exception ex) { return "unknown (" + ex.GetType().Name + ")"; }
        }
    }

    public bool Available
    {
        get { try { return Call(s => s.IsOperational); } catch { return false; } }
    }

    // Read-only probe that actually opens the control device (via IsActive) and classifies the outcome.
    public HidHideAccess CheckAccess(out string detail)
    {
        try
        {
            if (!Call(s => s.IsInstalled))
            {
                detail = "HidHide driver is not installed.";
                return HidHideAccess.NotInstalled;
            }
            if (!Call(s => s.IsDriverNodePresent))
            {
                detail = "HidHide driver node not present - reboot after install.";
                return HidHideAccess.ServiceUnavailable;
            }

            bool active = Call(s => s.IsActive);
            Version ver = Call(s => s.LocalDriverVersion);
            detail = $"HidHide {ver} reachable; cloak {(active ? "on" : "off")}.";
            return HidHideAccess.Ok;
        }
        catch (Exception ex)
        {
            LastError = Describe("access probe", ex);
            LastErrorWasBusy = IsBusy(ex);
            if (LastErrorWasBusy)
            {
                detail = "The HidHide driver is in use by another program - close the HidHide Configuration " +
                         "Client if it is open. XBridge will keep retrying.";
                return HidHideAccess.AccessDenied;
            }
            if (IsAccessDenied(ex))
            {
                detail = "HidHide denied access to this process (Win32 5). Its allow-list/config is likely " +
                         "inconsistent - repair HidHide (run install.cmd in its folder as admin, or reinstall), " +
                         "or set RequireCloak=false.";
                return HidHideAccess.AccessDenied;
            }
            detail = "HidHide error: " + LastError;
            return HidHideAccess.Failed;
        }
    }

    // Driver handle held by another API consumer - transient, clears when that app releases it.
    private static bool IsBusy(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is HidHideDriverAccessFailedException) return true;
        return false;
    }

    private static bool IsAccessDenied(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is HidHideDriverAccessFailedException or UnauthorizedAccessException) return true;
            if (e is HidHideException he && he.NativeErrorCode == 5) return true;
            string m = e.Message.ToLowerInvariant();
            if (m.Contains("0x0005") || m.Contains("access is denied") || m.Contains("access denied")) return true;
        }
        return false;
    }

    private static string Describe(string what, Exception ex) =>
        $"{what}: {ex.GetType().Name}: {ex.Message}";

    private static readonly Regex VidRe = new(@"VID[_&]([0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PidRe = new(@"PID[_&]([0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Handles both "HID\VID_0079&PID_0006\..." and Bluetooth "HID\{guid}_VID&0001038F_PID&1200\...".
    public static bool TryExtractVidPid(string instanceId, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        var mv = VidRe.Match(instanceId);
        var mp = PidRe.Match(instanceId);
        if (!mv.Success || !mp.Success) return false;
        vid = (ushort)(Convert.ToUInt32(mv.Groups[1].Value, 16) & 0xFFFF);
        pid = (ushort)(Convert.ToUInt32(mp.Groups[1].Value, 16) & 0xFFFF);
        return true;
    }

    // Present HID PnP instance ids for the configured devices (WMI - sees cloaked devices too).
    public static IReadOnlyList<string> EnumerateInstancePaths(IReadOnlyList<DeviceId> devices)
    {
        var list = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2", "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'HID\\\\%'");
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                if (mo["DeviceID"] is string id && id.Length > 0
                    && TryExtractVidPid(id, out var v, out var p)
                    && devices.Any(d => d.Matches(v, p)))
                    list.Add(id);
                mo.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error("WMI enumeration of HidHide instance ids failed", ex);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // Blocks are deliberately never removed while the app runs or exits: the kernel decides at
    // device arrival whether RawInput apps (browsers) can see a device, and only an already-
    // blocked device is excluded. Only --cloak-off / the uninstaller clear them.
    public bool HideDevice(string instanceId) => Mutate("hide " + Short(instanceId), s =>
    {
        if (!s.BlockedInstanceIds.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            s.AddBlockedInstanceId(instanceId);
    });

    // Bring one device fully under HidHide: allow-list this exe, block the device, enable hiding.
    public bool CloakDevice(string instanceId, string exePath) => Mutate("cloak " + Short(instanceId), s =>
    {
        if (!s.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase))
            s.AddApplicationPath(exePath, false);
        if (!s.BlockedInstanceIds.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            s.AddBlockedInstanceId(instanceId);
        if (!s.IsActive) s.IsActive = true;
    });

    // Put this exe on HidHide's application allow-list.
    public bool RegisterApp(string exePath) => Mutate("register app", s =>
    {
        if (!s.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase))
            s.AddApplicationPath(exePath, false);
    });

    // --cloak-off / uninstall: unblock our devices, unregister this exe, and drop global
    // hiding only if nobody else is blocking anything.
    public void GlobalUncloak(string exePath) => Mutate("global uncloak", s =>
    {
        foreach (var id in s.BlockedInstanceIds.Where(IsOurs).ToList())
            s.RemoveBlockedInstanceId(id);

        if (s.BlockedInstanceIds.Count == 0 && s.IsActive)
            s.IsActive = false;

        if (s.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase))
            s.RemoveApplicationPath(exePath);
    });

    private bool IsOurs(string instanceId) =>
        TryExtractVidPid(instanceId, out var v, out var p) && _devices.Any(d => d.Matches(v, p));

    private bool Mutate(string what, Action<IHidHideControlService> act)
    {
        lock (_gate)
        {
            try
            {
                Call(s => { act(s); return true; });
                Log.Info($"HidHide: {what} ok");
                LastErrorWasBusy = false;
                return true;
            }
            catch (Exception ex)
            {
                LastError = Describe(what, ex);
                LastErrorWasBusy = IsBusy(ex);
                if (LastErrorWasBusy) Log.Warn("HidHide: " + LastError + " (driver busy - will retry)");
                else Log.Error("HidHide: " + LastError);
                return false;
            }
        }
    }

    private static string Short(string instanceId)
    {
        int slash = instanceId.LastIndexOf('\\');
        return slash > 0 && slash < instanceId.Length - 1 ? "..." + instanceId[slash..] : instanceId;
    }
}
