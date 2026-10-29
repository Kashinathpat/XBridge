using XBridge.Config;
using XBridge.Core;
using XBridge.Diagnostics;
using XBridge.Interop;

namespace XBridge.Tray;

// Tray icon (grey idle / green N-active / red error) + menu, plus a hidden message window that
// receives WM_DEVICECHANGE for hot-plug detection.
internal sealed class TrayAppContext : ApplicationContext
{
    private readonly MessageWindow _msg;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;

    private readonly HidHideManager _hidHide;
    private readonly SessionManager _manager;

    private readonly Dictionary<ManagerStatus, Icon> _icons = new();
    private int _cleaned;

    public TrayAppContext(AppConfig cfg, string ownExe)
    {
        _msg = new MessageWindow(OnDeviceChange);
        _ = _msg.Handle; // force handle creation on the UI thread (device-change messages + BeginInvoke)

        BuildIcons();

        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("XBridge") { Enabled = false });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open config / logs folder", null, (_, _) => OpenFolder(AppConfig.ConfigDir));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Icon = _icons[ManagerStatus.Idle],
            Text = "XBridge (starting)",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _hidHide = new HidHideManager(cfg.Devices);
        _manager = new SessionManager(cfg, _hidHide, ownExe)
        {
            Notify = (title, body) => ShowBalloon(title, body),
        };
        _manager.StateChanged += OnManagerStateChanged;

        // Cleanup hook must be armed before anything is started.
        LifetimeCleanup.SetHandler(Cleanup);

        PreflightDriverChecks();

        try { _hidHide.RegisterApp(ownExe); } catch (Exception ex) { Log.Warn("RegisterApp: " + ex.Message); }

        _manager.Start();
        UpdateTrayFromState(_manager.CurrentState());
    }

    private void OnDeviceChange() => _manager?.RequestReconcile();

    private void OnManagerStateChanged(ManagerState st)
    {
        try
        {
            if (_msg.IsHandleCreated)
                _msg.BeginInvoke(() => UpdateTrayFromState(st));
        }
        catch (Exception ex) { Log.Warn("state marshal failed: " + ex.Message); }
    }

    private void UpdateTrayFromState(ManagerState st)
    {
        var key = _icons.ContainsKey(st.Status) ? st.Status : ManagerStatus.Idle;
        _tray.Icon = _icons[key];

        string text = st.Status switch
        {
            ManagerStatus.Idle => "No controllers connected",
            ManagerStatus.Active => $"{st.ActiveCount} controller(s) active",
            ManagerStatus.Error => st.Message ?? $"{st.FaultedCount} controller(s) in error",
            _ => "Unknown",
        };
        if (st.Status == ManagerStatus.Error && st.ActiveCount > 0)
            text = $"{st.ActiveCount} active, {text}";

        _statusItem.Text = text;
        // NotifyIcon.Text is capped at 63 chars.
        _tray.Text = Truncate("XBridge: " + text, 63);
    }

    private void PreflightDriverChecks()
    {
        var vigem = Drivers.Query(Drivers.ViGEmService);
        var hidhide = Drivers.Query(Drivers.HidHideService);

        Log.Info("=== preflight ===");
        Log.Info($"  running as        : {(Program.IsElevated() ? "elevated" : "standard user (asInvoker)")}");
        Log.Info($"  ViGEmBus service  : {vigem.Describe()}");
        Log.Info($"  HidHide service   : {hidhide.Describe()}");
        Log.Info($"  HidHide driver    : {_hidHide.DriverInfo}");

        HidHideAccess access = _hidHide.CheckAccess(out string accessDetail);
        Log.Info($"  HidHide access    : {access} - {accessDetail}");

        if (!vigem.Installed)
            ShowBalloon("ViGEmBus not installed",
                "Controller emulation needs the ViGEmBus driver. Reinstall ViGEmBusSetup_x64.msi. Emulation is paused.");
        else if (!vigem.Running)
            ShowBalloon("ViGEmBus not running",
                "The ViGEmBus service is installed but stopped. Run 'sc start ViGEmBus' or reboot.");

        switch (access)
        {
            case HidHideAccess.Ok:
                break;
            case HidHideAccess.NotInstalled:
                ShowBalloon("HidHide not installed",
                    "The HidHide driver was not found. Install HidHideMSI_x64.msi. " +
                    "Sessions will not start while RequireCloak=true (config.json).");
                break;
            case HidHideAccess.ServiceUnavailable:
                ShowBalloon("HidHide service problem", accessDetail);
                break;
            case HidHideAccess.AccessDenied:
                ShowBalloon("HidHide unavailable", accessDetail);
                break;
            default:
                ShowBalloon("HidHide check failed", accessDetail);
                break;
        }
    }

    private void ShowBalloon(string title, string body)
    {
        void Do()
        {
            // WinForms only exposes Windows' stock severity glyphs here; omit them so notifications
            // are branded by the app icon instead of showing a generic orange X or warning symbol.
            try { _tray.ShowBalloonTip(4000, title, body, ToolTipIcon.None); }
            catch (Exception ex) { Log.Warn("balloon failed: " + ex.Message); }
        }
        try
        {
            if (_msg.IsHandleCreated && _msg.InvokeRequired) _msg.BeginInvoke(Do);
            else Do();
        }
        catch { }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
            { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("open folder failed: " + ex.Message); }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private void BuildIcons()
    {
        _icons[ManagerStatus.Idle] = LoadIcon("tray-idle.ico");
        _icons[ManagerStatus.Active] = LoadIcon("tray-active.ico");
        _icons[ManagerStatus.Error] = LoadIcon("tray-error.ico");
    }

    // Picks the embedded size closest to the tray's current icon size, so it stays sharp at any DPI.
    private static Icon LoadIcon(string resource)
    {
        using var stream = typeof(TrayAppContext).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing embedded icon " + resource);
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    private void ExitApp()
    {
        LifetimeCleanup.RunOnce();
        ExitThread();
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0) return;
        Log.Info("TrayAppContext cleanup starting.");

        try { _manager.DisposeAll(); }
        catch (Exception ex) { Log.Error("cleanup: SessionManager.DisposeAll", ex); }

        try
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch { }

        foreach (var icon in _icons.Values)
        {
            try { icon.Dispose(); } catch { }
        }
        _icons.Clear();

        try { _msg.Dispose(); } catch { }

        Log.Info("TrayAppContext cleanup done.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Cleanup();
        base.Dispose(disposing);
    }

    // Invisible window that receives WM_DEVICECHANGE.
    private sealed class MessageWindow : Form
    {
        private readonly Action _onDeviceChange;
        private IntPtr _hidNotify;
        private long _lastFireTicks;

        public MessageWindow(Action onDeviceChange)
        {
            _onDeviceChange = onDeviceChange;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
            Opacity = 0d;
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { _hidNotify = NativeMethods.RegisterHidNotification(Handle); }
            catch (Exception ex) { Log.Warn("RegisterDeviceNotification failed: " + ex.Message); }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try { NativeMethods.UnregisterHidNotification(_hidNotify); } catch { }
            _hidNotify = IntPtr.Zero;
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_DEVICECHANGE)
            {
                int evt = m.WParam.ToInt32();
                if (evt is NativeMethods.DBT_DEVICEARRIVAL
                    or NativeMethods.DBT_DEVICEREMOVECOMPLETE
                    or NativeMethods.DBT_DEVNODES_CHANGED)
                {
                    // Coarse coalescing on top of the manager's own debounce.
                    long now = Environment.TickCount64;
                    if (now - _lastFireTicks >= 100)
                    {
                        _lastFireTicks = now;
                        try { _onDeviceChange(); } catch (Exception ex) { Log.Warn("device-change handler: " + ex.Message); }
                    }
                }
            }
            base.WndProc(ref m);
        }
    }
}
