using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using XBridge.Config;
using XBridge.Core;
using XBridge.Diagnostics;
using XBridge.Interop;

namespace XBridge.Tray;

// Tray icon (grey idle / green N-active / red error) + menu, plus a hidden message window that is
// both the DirectInput cooperative HWND and the WM_DEVICECHANGE sink for hot-plug detection.
internal sealed class TrayAppContext : ApplicationContext
{
    private readonly MessageWindow _msg;
    private readonly IntPtr _msgHwnd;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;

    private readonly HidHideManager _hidHide;
    private readonly SessionManager _manager;

    private readonly Dictionary<ManagerStatus, (Icon icon, IntPtr handle)> _icons = new();
    private int _cleaned;

    public TrayAppContext(AppConfig cfg, string ownExe)
    {
        _msg = new MessageWindow(OnDeviceChange);
        _msgHwnd = _msg.Handle; // force creation on the UI thread; value is stable

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
            Icon = _icons[ManagerStatus.Idle].icon,
            Text = "XBridge (starting)",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _hidHide = new HidHideManager(cfg.Devices);
        _manager = new SessionManager(cfg, _hidHide, ownExe, () => _msgHwnd)
        {
            Notify = (title, body, isError) => ShowBalloon(title, body, isError ? ToolTipIcon.Error : ToolTipIcon.Info),
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
        _tray.Icon = _icons[key].icon;

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
                "Controller emulation needs the ViGEmBus driver. Reinstall ViGEmBusSetup_x64.msi. Emulation is paused.",
                ToolTipIcon.Error);
        else if (!vigem.Running)
            ShowBalloon("ViGEmBus not running",
                "The ViGEmBus service is installed but stopped. Run 'sc start ViGEmBus' or reboot.",
                ToolTipIcon.Warning);

        switch (access)
        {
            case HidHideAccess.Ok:
                break;
            case HidHideAccess.NotInstalled:
                ShowBalloon("HidHide not installed",
                    "The HidHide driver was not found. Install HidHideMSI_x64.msi. " +
                    "Sessions will not start while RequireCloak=true (config.json).", ToolTipIcon.Warning);
                break;
            case HidHideAccess.ServiceUnavailable:
                ShowBalloon("HidHide service problem", accessDetail, ToolTipIcon.Warning);
                break;
            case HidHideAccess.AccessDenied:
                ShowBalloon("HidHide unavailable", accessDetail, ToolTipIcon.Warning);
                break;
            default:
                ShowBalloon("HidHide check failed", accessDetail, ToolTipIcon.Warning);
                break;
        }
    }

    private void ShowBalloon(string title, string body, ToolTipIcon icon)
    {
        void Do()
        {
            try { _tray.ShowBalloonTip(4000, title, body, icon); }
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
        _icons[ManagerStatus.Idle] = MakeIcon(Color.FromArgb(150, 150, 150));
        _icons[ManagerStatus.Active] = MakeIcon(Color.FromArgb(40, 170, 70));
        _icons[ManagerStatus.Error] = MakeIcon(Color.FromArgb(200, 60, 50));
    }

    private static (Icon, IntPtr) MakeIcon(Color tint)
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(tint);
            g.FillEllipse(body, 1, 9, 15, 15);
            g.FillEllipse(body, 16, 9, 15, 15);
            g.FillRectangle(body, 8, 10, 16, 13);
            using var hole = new SolidBrush(Color.FromArgb(235, 250, 250, 250));
            g.FillEllipse(hole, 6, 14, 5, 5);
            g.FillEllipse(hole, 21, 14, 5, 5);
        }
        IntPtr h = bmp.GetHicon();
        return ((Icon)Icon.FromHandle(h), h);
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

        try { NativeMethods.timeEndPeriod(1); } catch { }

        try
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch { }

        foreach (var (icon, handle) in _icons.Values)
        {
            try { icon.Dispose(); } catch { }
            try { NativeMethods.DestroyIcon(handle); } catch { }
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

    // Invisible window: DirectInput cooperative HWND + WM_DEVICECHANGE sink.
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
