using System.ServiceProcess;
using Microsoft.Win32;
using XBridge.Diagnostics;

namespace XBridge.Core;

internal readonly record struct DriverState(bool Installed, bool Running)
{
    public static readonly DriverState Missing = new(false, false);
    public string Describe() => !Installed ? "not installed" : Running ? "running" : "installed but not running";
}

// Presence/health checks for the ViGEmBus and HidHide kernel services.
internal static class Drivers
{
    public const string ViGEmService = "ViGEmBus";
    public const string HidHideService = "HidHide";

    public static DriverState Query(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            var status = sc.Status;   // throws InvalidOperationException if the service does not exist
            bool running = status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
            return new DriverState(true, running);
        }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            Log.Warn($"ServiceController query for '{serviceName}' failed: {ex.Message}");
        }

        try   // registry fallback tells us "installed", not "running"
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key is not null)
                return new DriverState(true, false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry check for service '{serviceName}' failed: {ex.Message}");
        }

        return DriverState.Missing;
    }
}
