using System.Management;
using Nefarius.Drivers.HidHide;
using Nefarius.Drivers.HidHide.Exceptions;
using XBridge.Diagnostics;

namespace XBridge.Core;

internal enum HidHideAccess { Ok, NotInstalled, AccessDenied, ServiceUnavailable, Failed }

// Every call opens its own short-lived \\.\HidHide handle, so calls are serialized here and
// retried if the driver is momentarily held by another API consumer (e.g. its config GUI).
internal sealed class HidHideManager
{
    public const ushort Vid = 0x0079;
    public const ushort Pid = 0x0006;

    private const int OpenAttempts = 6;
    private const int OpenRetryMs = 200;

    private readonly object _gate = new();
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
                detail = "HidHide driver is not installed (HidHideMSI_x64.msi).";
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

    // All present "HID\VID_0079&PID_0006\..." PnP instance ids (WMI - sees cloaked devices too).
    public static IReadOnlyList<string> EnumerateInstancePaths()
    {
        var list = new List<string>();
        string like = $"HID\\\\VID_{Vid:X4}&PID_{Pid:X4}%";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2", $"SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '{like}'");
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                if (mo["DeviceID"] is string id && id.Length > 0) list.Add(id);
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

    public bool HideDevice(string instanceId) => Mutate("hide " + Short(instanceId), s =>
    {
        if (!s.BlockedInstanceIds.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            s.AddBlockedInstanceId(instanceId);
    });

    public bool UnhideDevice(string instanceId) => Mutate("unhide " + Short(instanceId), s =>
    {
        if (s.BlockedInstanceIds.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            s.RemoveBlockedInstanceId(instanceId);
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

    // Put this exe on HidHide's application allow-list (installer one-shot, runs elevated).
    public bool RegisterApp(string exePath) => Mutate("register app", s =>
    {
        if (!s.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase))
            s.AddApplicationPath(exePath, false);
    });

    // Startup: allow-list this exe and drop any stale blocks for our device family
    // (a crashed run can leave them) - sessions re-block as they start.
    public void StartupReset(string exePath) => Mutate("startup reset", s =>
    {
        if (!s.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase))
            s.AddApplicationPath(exePath, false);
        foreach (var id in s.BlockedInstanceIds.Where(IsOurs).ToList())
            s.RemoveBlockedInstanceId(id);
    });

    // Unblock our devices; drop global hiding only if nobody else is blocking anything.
    // unregisterApp is uninstall-only - a normal exit keeps this exe on the allow-list.
    public void GlobalUncloak(string exePath, IEnumerable<string> knownInstanceIds, bool unregisterApp) =>
        Mutate(unregisterApp ? "global uncloak (+unreg)" : "shutdown uncloak", s =>
        {
            var mine = knownInstanceIds
                .Concat(s.BlockedInstanceIds.Where(IsOurs))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var id in mine)
                if (s.BlockedInstanceIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                    s.RemoveBlockedInstanceId(id);

            if (s.BlockedInstanceIds.Count == 0 && s.IsActive)
                s.IsActive = false;

            if (unregisterApp && s.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase))
                s.RemoveApplicationPath(exePath);
        });

    private static bool IsOurs(string instanceId) =>
        instanceId.Contains($"VID_{Vid:X4}&PID_{Pid:X4}", StringComparison.OrdinalIgnoreCase);

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
