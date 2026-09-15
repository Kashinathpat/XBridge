using System.Security.Principal;
using XBridge.Calibration;
using XBridge.Config;
using XBridge.Core;
using XBridge.Diagnostics;
using XBridge.Interop;
using XBridge.Tray;

namespace XBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Log.Init();

        var cfg = AppConfig.LoadOrCreate();
        Log.Configure(cfg.LogMaxBytes, cfg.LogMaxFiles);

        Log.Info($"XBridge v{typeof(Program).Assembly.GetName().Version} starting. " +
                 $"args=[{string.Join(' ', args)}] elevated={IsElevated()} pid={Environment.ProcessId}");

        // Global safety nets: full cleanup before the process dies.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("FATAL: AppDomain.UnhandledException (terminating=" + e.IsTerminating + ")", e.ExceptionObject as Exception);
            LifetimeCleanup.RunOnce();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Info("ProcessExit - running cleanup.");
            LifetimeCleanup.RunOnce();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("UnobservedTaskException (observed & swallowed)", e.Exception);
            e.SetObserved();
        };
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            CalibrationRunner.Stop();
            LifetimeCleanup.RunOnce();
        };

        try
        {
            if (HasFlag(args, "--help", "-h", "/?")) return ShowHelp();
            if (HasFlag(args, "--cloak-off", "--hidhide-cleanup")) return HidHideCleanup(cfg);
            if (HasFlag(args, "--hidhide-register")) return HidHideRegister(cfg);
            if (HasFlag(args, "--calibrate")) return RunCalibrate(args, cfg);

            return RunTray(cfg);
        }
        finally
        {
            LifetimeCleanup.RunOnce();
        }
    }

    private static int RunTray(AppConfig cfg)
    {
        NativeMethods.timeBeginPeriod(1); // 1 ms scheduler tick for low-jitter polling

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("FATAL: WinForms ThreadException", e.Exception);
            LifetimeCleanup.RunOnce();
            Environment.Exit(1);
        };

        string ownExe = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "XBridge.exe");

        using var ctx = new TrayAppContext(cfg, ownExe);
        Application.Run(ctx);
        return 0;
    }

    private static int HidHideCleanup(AppConfig cfg)
    {
        NativeMethods.AllocConsole();
        Log.EnableConsoleMirror();
        Log.Info("Running one-shot HidHide cleanup (--cloak-off / --hidhide-cleanup).");

        string ownExe = Environment.ProcessPath ?? "XBridge.exe";
        try
        {
            var hh = new HidHideManager(cfg.Devices);
            hh.GlobalUncloak(ownExe);
            Console.WriteLine("HidHide: devices un-hidden, this app un-registered, cloak disabled.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("HidHide cleanup failed", ex);
            Console.WriteLine("HidHide cleanup failed: " + ex.Message);
            return 1;
        }
    }

    private static int HidHideRegister(AppConfig cfg)
    {
        NativeMethods.AllocConsole();
        Log.EnableConsoleMirror();
        Log.Info("Running one-shot HidHide app registration (--hidhide-register).");

        string ownExe = Environment.ProcessPath ?? "XBridge.exe";
        bool ok = new HidHideManager(cfg.Devices).RegisterApp(ownExe);
        Console.WriteLine(ok ? "HidHide: this app is on the allow-list." : "HidHide: registration failed (see log).");
        return ok ? 0 : 1;
    }

    private static int RunCalibrate(string[] args, AppConfig cfg)
    {
        NativeMethods.AllocConsole();
        try
        {
            CalibrationRunner.Run(args, cfg);
            Console.WriteLine();
            Console.WriteLine("Press Enter to close.");
            try { Console.ReadLine(); } catch { }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("calibrate mode failed", ex);
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static int ShowHelp()
    {
        NativeMethods.AllocConsole();
        Console.WriteLine(
            """
            XBridge - emulates Xbox 360 controllers (with rumble) from up to four
            DirectInput gamepads and hides the physical devices via HidHide. Which
            controllers to bridge is set by the Devices list in config.json.

            Usage:
              XBridge.exe                 Run in the system tray (normal mode).
              XBridge.exe --calibrate [--index N | --guid <g>]
                                                  Print raw button/axis indices while
                                                  you press controls; writes the
                                                  mapping file for that pad.
              XBridge.exe --cloak-off     One-shot: un-hide devices, un-register
              XBridge.exe --hidhide-cleanup   this exe, disable cloak, then exit
                                                  (used by the installer/uninstaller).
              XBridge.exe --help          This text.

            Files (all under %LOCALAPPDATA%\XBridge):
              config.json              Devices list, poll interval, dead zones, RequireCloak
              mapping-<vid>-<pid>.json  button/axis calibration, one file per pad layout
              logs\app.log             rolling, size-capped diagnostic log
            """);
        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        try { Console.ReadLine(); } catch { }
        return 0;
    }

    private static bool HasFlag(string[] args, params string[] names) =>
        args.Any(a => names.Any(n => a.Equals(n, StringComparison.OrdinalIgnoreCase)));

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
