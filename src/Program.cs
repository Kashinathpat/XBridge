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
    // Held by the tray instance for its lifetime: a second tray would bridge the same pads twice, and
    // --cloak-off must not run under a live instance whose periodic re-hide would undo it.
    private const string TrayMutexName = @"Local\XBridge-Tray-3F2A9C41";

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
        ApplicationConfiguration.Initialize();
        using var trayMutex = new Mutex(true, TrayMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            Log.Warn("Another XBridge tray instance is already running - exiting.");
            MessageBox.Show("XBridge is already running - look for its icon in the notification area.", "XBridge",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
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
        NativeMethods.EnsureConsole();
        Log.EnableConsoleMirror();
        Log.Info("Running one-shot HidHide cleanup (--cloak-off / --hidhide-cleanup).");

        if (Mutex.TryOpenExisting(TrayMutexName, out var running))
        {
            running.Dispose();
            Console.WriteLine("XBridge is running - exit it from its tray icon first, then run this again.");
            return 1;
        }

        string ownExe = Environment.ProcessPath ?? "XBridge.exe";
        var hh = new HidHideManager(cfg.Devices);
        if (!hh.GlobalUncloak(ownExe))
        {
            Console.WriteLine("HidHide cleanup failed - devices may still be hidden: " + (hh.LastError ?? "see the log"));
            return 1;
        }
        Console.WriteLine("HidHide: devices un-hidden, this app un-registered, cloak disabled.");
        return 0;
    }

    private static int HidHideRegister(AppConfig cfg)
    {
        NativeMethods.EnsureConsole();
        Log.EnableConsoleMirror();
        Log.Info("Running one-shot HidHide app registration (--hidhide-register).");

        string ownExe = Environment.ProcessPath ?? "XBridge.exe";
        bool ok = new HidHideManager(cfg.Devices).RegisterApp(ownExe);
        Console.WriteLine(ok ? "HidHide: this app is on the allow-list." : "HidHide: registration failed (see log).");
        return ok ? 0 : 1;
    }

    private static int RunCalibrate(string[] args, AppConfig cfg)
    {
        bool allocated = NativeMethods.EnsureConsole();
        try
        {
            CalibrationRunner.Run(args, cfg);
            if (allocated)
            {
                Console.WriteLine();
                Console.WriteLine("Press Enter to close.");
                try { Console.ReadLine(); } catch { }
            }
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
        bool allocated = NativeMethods.EnsureConsole();
        Console.WriteLine(
            """
            XBridge - emulates Xbox 360 controllers (with rumble) from up to four
            HID gamepads and hides the physical devices via HidHide. Which
            controllers to bridge is set by the Devices list in config.json.

            Usage:
              XBridge.exe                 Run in the system tray (normal mode).
              XBridge.exe --calibrate [--index N | --guid <g>]
                                                  Print raw button/axis indices while
                                                  you press controls; writes the
                                                  mapping file for that pad.
              XBridge.exe --cloak-off     One-shot: un-hide devices, un-register
              XBridge.exe --hidhide-cleanup   this exe, disable cloak, then exit
                                                  (used by the uninstaller; quit the tray app first).
              XBridge.exe --help          This text.

            Files (all under %LOCALAPPDATA%\XBridge):
              config.json              Devices list, dead zones, RequireCloak, rumble tuning
              mapping-<vid>-<pid>.json  button/axis calibration, one file per pad layout
              hidden-devices.txt       pads XBridge has hidden, so --cloak-off can un-hide them
              logs\app.log             rolling, size-capped diagnostic log
            """);
        if (allocated)
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to close.");
            try { Console.ReadLine(); } catch { }
        }
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
