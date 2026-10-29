using HidSharp;
using Microsoft.Win32.SafeHandles;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using XBridge.Config;
using XBridge.Diagnostics;
using XBridge.Input;
using XBridge.Interop;

namespace XBridge.Core;

internal enum SessionStatus { Starting, Running, Faulted, Disposing, Disposed }

internal sealed class SessionStartException : Exception
{
    public SessionStartException(string message, Exception? inner = null) : base(message, inner) { }

    // Set when the failure is expected to clear on its own (e.g. HidHide driver momentarily held by another app).
    public bool Transient { get; init; }
}

// One physical gamepad: raw HID read handle (input), raw HID write handle (rumble), ViGEm pad, cloak entry.
// FeedbackReceived only stages bytes for this session's HID handle, so rumble can't cross to another unit.
internal sealed class ControllerSession : IDisposable
{
    public Guid InstanceGuid => _unit.InstanceGuid;
    public string ProductName => _unit.ProductName;
    public SessionStatus Status => _status;
    public string? FaultReason { get; private set; }
    public bool RawInputVisible { get; private set; }

    private readonly GamepadUnit _unit;
    private readonly ViGEmClient _client;
    private readonly InputMapper _mapper;
    private readonly AppConfig _cfg;
    private readonly HidHideManager _hidHide;
    private readonly string _ownExe;
    private readonly Action<ControllerSession> _onFaulted;

    private volatile SessionStatus _status = SessionStatus.Starting;
    private string ShortId => _unit.Short;

    private SafeFileHandle? _rumble;
    private HidDevice? _hidDevice;
    private HidStream? _hidReader;
    private HidInputParser? _parser;
    private byte[] _rxInput = Array.Empty<byte>();
    private IXbox360Controller? _pad;
    private bool _cloaked;

    private Thread? _thread;
    private volatile bool _stop;

    private int _wantRumble;   // packed (strong << 8) | weak, written by the ViGEm callback
    private int _rumbleDirty;
    private long _lastRumbleApplyMs;
    private long _lastRumbleErrMs;
    private readonly byte[] _txBuf;

    private JoystickState _state = new();
    private XboxState _xb;
    private XboxState _lastSubmittedXb;
    private bool _hasSubmitted;
    private long _lastSubmitMs;
    private long _lastReportRateLogMs;
    private int _reportsSubmittedSinceLog;

    private int _disposed;
    private int _faultReported;

    public ControllerSession(
        GamepadUnit unit, ViGEmClient client, InputMapper mapper,
        AppConfig cfg, HidHideManager hidHide, string ownExe, Action<ControllerSession> onFaulted)
    {
        _unit = unit;
        _client = client;
        _mapper = mapper;
        _cfg = cfg;
        _hidHide = hidHide;
        _ownExe = ownExe;
        _onFaulted = onFaulted;
        _txBuf = new byte[Math.Max(8, unit.HidMaxOutputLength)];
    }

    public void Start()
    {
        Log.Info($"[{ShortId}] session CREATED guid={InstanceGuid} name=\"{ProductName}\" " +
                 $"hid={_unit.HidDevicePath} cloak={_unit.HidHideInstancePath ?? "<none>"}");
        try
        {
            OpenRumbleHandleStep();
            CloakStep();

            OpenRawHidReaderStep();
            SendIdle();
            VirtualPadStep();

            _status = SessionStatus.Running;
            _thread = new Thread(ReadThread) { Name = $"Input-{ShortId}", IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            Log.Info($"[{ShortId}] session RUNNING");
        }
        catch (Exception ex)
        {
            _status = SessionStatus.Faulted;
            FaultReason = ex.Message;
            if (ex is SessionStartException)
                Log.Warn($"[{ShortId}] session start aborted: {FirstLine(ex.Message)}");
            else
                Log.Error($"[{ShortId}] session start FAILED", ex);
            DisposeInternal();
            throw ex as SessionStartException ?? new SessionStartException(ex.Message, ex);
        }
    }

    private void OpenRumbleHandleStep()
    {
        if (_unit.HidMaxOutputLength <= 0)
        {
            Log.Info($"[{ShortId}] HID device has no output report - input-only (this device can't rumble)");
            return;
        }

        _rumble = NativeHid.OpenForWrite(_unit.HidDevicePath, out string? err);
        if (_rumble is not null && !_rumble.IsInvalid)
        {
            Log.Info($"[{ShortId}] rumble HID handle open");
            return;
        }

        _rumble?.Dispose();
        _rumble = null;
        Log.Warn($"[{ShortId}] could not open the rumble HID handle ({err ?? "unknown error"}) - input-only");
    }

    private void CloakStep()
    {
        if (_unit.HidHideInstancePath is null)
        {
            if (_cfg.RequireCloak)
                throw new SessionStartException(
                    "Could not resolve this unit's PnP instance path, so HidHide can't hide it. " +
                    "Set RequireCloak=false in config.json to override.");
            Log.Warn($"[{ShortId}] no HidHide instance path - UNCLOAKED (RequireCloak=false)");
            return;
        }

        if (!_hidHide.Available)
        {
            if (_cfg.RequireCloak)
                throw new SessionStartException("HidHide driver not installed/operational. Install HidHide or set RequireCloak=false.");
            Log.Warn($"[{ShortId}] HidHide unavailable - UNCLOAKED (RequireCloak=false)");
            return;
        }

        _cloaked = _hidHide.CloakDevice(_unit.HidHideInstancePath, _ownExe);
        if (_cloaked)
        {
            if (RawInputDevices.IsListed(_unit.HidDevicePath))
            {
                RawInputVisible = true;
                Log.Warn($"[{ShortId}] device was already present before it was hidden - RawInput apps " +
                         "(browsers) can still see it until it is reconnected once");
            }
            return;
        }

        var svc = Drivers.Query(Drivers.HidHideService);
        string hint = HidHideHint(_hidHide.LastError, svc);
        Log.Error($"[{ShortId}] HidHide cloak failed for {_unit.HidHideInstancePath} " +
                  $"(service {svc.Describe()}, {(Program.IsElevated() ? "elevated" : "standard user")}): " +
                  $"{_hidHide.LastError ?? "no error captured"} | {hint}");

        if (_cfg.RequireCloak)
            throw new SessionStartException($"HidHide cloak failed - not exposing a virtual pad. {hint} " +
                                            "Or set RequireCloak=false in config.json.")
            { Transient = _hidHide.LastErrorWasBusy };
        Log.Warn($"[{ShortId}] continuing UNCLOAKED (RequireCloak=false)");
    }

    private static string HidHideHint(string? err, DriverState svc)
    {
        if (!svc.Installed) return "HidHide is not installed; reboot after installing.";
        if (!svc.Running) return "HidHide service is stopped ('sc start HidHide' or reboot).";
        string blob = (err ?? "").ToLowerInvariant();
        if (blob.Contains("no other process") || blob.Contains("driveraccessfailed"))
            return "Another program is using the HidHide driver - close the HidHide Configuration Client if it is open; XBridge keeps retrying.";
        if (blob.Contains("0x0005") || blob.Contains("access is denied") || blob.Contains("access denied"))
            return "HidHide denied driver access - its config is inconsistent; repair/reinstall HidHide, or set RequireCloak=false.";
        return "See the HidHide error in the log.";
    }

    private void OpenRawHidReaderStep()
    {
        _hidDevice = FindHidDevice()
            ?? throw new SessionStartException("Raw-HID input device not found for this unit.");
        try { _parser = new HidInputParser(_hidDevice.DevicePath); }
        catch (Exception ex) { throw new SessionStartException("Could not read the HID report descriptor: " + ex.Message, ex); }
        _rxInput = new byte[Math.Max(_parser.ReportLength, _hidDevice.GetMaxInputReportLength())];
        if (!OpenHidReader())
            throw new SessionStartException("Could not open the raw-HID input device (in use, or blocked by HidHide).");

        Log.Info($"[{ShortId}] raw HID input stream open ({_rxInput.Length}-byte reports)");
    }

    private HidDevice? FindHidDevice() => DeviceList.Local.GetHidDevices().FirstOrDefault(
        d => string.Equals(d.DevicePath, _unit.HidDevicePath, StringComparison.OrdinalIgnoreCase));

    private bool OpenHidReader()
    {
        if (_hidDevice is null || !_hidDevice.TryOpen(out _hidReader)) return false;
        _hidReader.ReadTimeout = 1000;   // so the read loop notices _stop on a pad that only sends on change
        return true;
    }

    // Blocks until the pad sends a report, so input is forwarded the moment it arrives and the
    // thread costs nothing while waiting - no poll interval, no spinning.
    private void ReadThread()
    {
        try { ReadLoop(); }
        catch (Exception ex) when (!_stop) { Fault($"Input thread failed: {ex.Message}"); }
        catch (Exception ex) { Log.Debug($"[{ShortId}] ReadThread shutdown: {ex.Message}"); }
    }

    private void ReadLoop()
    {
        int errStreak = 0;
        while (!_stop)
        {
            int n;
            try { n = _hidReader!.Read(_rxInput, 0, _rxInput.Length); }
            catch (TimeoutException) { ServiceRumble(Environment.TickCount64); continue; }
            catch (Exception ex)
            {
                if (_stop) return;
                errStreak++;
                if (errStreak is 1 or 10) Log.Warn($"[{ShortId}] raw HID read error #{errStreak}: {ex.GetType().Name}: {ex.Message}");
                if (errStreak >= 40) { Fault("Raw-HID input device stopped responding.", rebuild: true); return; }
                if (!ReopenRawHid()) Thread.Sleep(500);
                continue;
            }

            errStreak = 0;
            if (n > 0)
            {
                _parser!.Parse(_rxInput, _state);
                _mapper.Map(_state, ref _xb);

                long now = Environment.TickCount64;
                bool stateChanged = !_hasSubmitted || !_xb.Equals(_lastSubmittedXb);
                bool keepAliveDue = (now - _lastSubmitMs) >= 1500;

                if (stateChanged || keepAliveDue)
                {
                    PushToPad();
                    try
                    {
                        _pad!.SubmitReport();
                        _lastSubmittedXb = _xb;
                        _lastSubmitMs = now;
                        _hasSubmitted = true;
                        _reportsSubmittedSinceLog++;
                    }
                    catch (Exception ex) { Log.Warn($"[{ShortId}] SubmitReport: {ex.Message}"); }
                }

                if (now - _lastReportRateLogMs >= 60000)
                {
                    if (_lastReportRateLogMs > 0)
                        Log.Debug($"[{ShortId}] IOCTL rate: {_reportsSubmittedSinceLog} reports in last minute ({_reportsSubmittedSinceLog / 60.0:F1}/s)");
                    _reportsSubmittedSinceLog = 0;
                    _lastReportRateLogMs = now;
                }
            }
            ServiceRumble(Environment.TickCount64);
        }
    }

    private bool ReopenRawHid()
    {
        try { _hidReader?.Dispose(); } catch { }
        _hidReader = null;
        try
        {
            _hidDevice = FindHidDevice();
            if (_hidDevice is null) { Log.Info($"[{ShortId}] raw HID reopen: device not present"); return false; }
            if (!OpenHidReader()) { Log.Warn($"[{ShortId}] raw HID reopen: open failed"); return false; }
            Log.Info($"[{ShortId}] raw HID input stream reopened");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[{ShortId}] raw HID reopen: {ex.Message}");
            return false;
        }
    }

    private void VirtualPadStep()
    {
        try
        {
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;
            _pad.Connect();
            _pad.FeedbackReceived += OnFeedbackReceived;
            Log.Info($"[{ShortId}] virtual Xbox 360 pad connected");
        }
        catch (Exception ex)
        {
            throw new SessionStartException("Failed to create/connect the virtual Xbox 360 pad: " + ex.Message, ex);
        }
    }

    private void OnFeedbackReceived(object? sender, Xbox360FeedbackReceivedEventArgs e)
    {
        byte strong = e.LargeMotor;   // Xbox: LargeMotor = low-freq, SmallMotor = high-freq
        byte weak = e.SmallMotor;
        if (_cfg.RumbleWeak0x0AQuirk && weak == 0x0A) weak = 0x0B;   // DragonRise quirk

        int packed = (strong << 8) | weak;
        if (Interlocked.Exchange(ref _wantRumble, packed) != packed)
            Interlocked.Exchange(ref _rumbleDirty, 1);
    }

    // Re-assert the HidHide block for this unit (idempotent) in case HidHide's state drifted.
    public void ReassertCloak()
    {
        if (_cloaked && _unit.HidHideInstancePath is { } p)
            _hidHide.HideDevice(p);
    }

    // Input thread only - single-threads all raw HID rumble writes.
    private void ServiceRumble(long nowMs)
    {
        if (_rumble is null) return;

        bool dirty = Interlocked.Exchange(ref _rumbleDirty, 0) == 1;
        int packed = Volatile.Read(ref _wantRumble);
        byte strong = (byte)(packed >> 8);
        byte weak = (byte)(packed & 0xFF);

        if (dirty)
        {
            if (packed == 0) SendPayload(0xF3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
            else SendRumblePair(weak, strong, _cfg.RumbleRepeat);
            _lastRumbleApplyMs = nowMs;
        }
        else if (packed != 0 && nowMs - _lastRumbleApplyMs > _cfg.RumbleKeepAliveMs)
        {
            SendRumblePair(weak, strong, 1);
            _lastRumbleApplyMs = nowMs;
        }
    }

    private void SendRumblePair(byte weak, byte strong, int repeat)
    {
        for (int i = 0; i < repeat; i++)
        {
            SendPayload(0x51, 0x00, weak, 0x00, strong, 0x00, 0x00);
            SendPayload(0xFA, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x00);
        }
    }

    private void SendIdle() => SendPayload(0xF3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);

    // Report-ID-zero framing: 0x00 + the 7 payload bytes, zero-padded to maxOut, via HidD_SetOutputReport.
    private void SendPayload(byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6)
    {
        if (_rumble is null) return;

        Array.Clear(_txBuf);
        _txBuf[1] = b0; _txBuf[2] = b1; _txBuf[3] = b2; _txBuf[4] = b3;
        _txBuf[5] = b4; _txBuf[6] = b5; _txBuf[7] = b6;

        if (NativeHid.SetOutputReport(_rumble, _txBuf, out string err)) return;

        long now = Environment.TickCount64;
        if (now - _lastRumbleErrMs > 5000)
        {
            Log.Warn($"[{ShortId}] rumble SET_REPORT failed: {err}");
            _lastRumbleErrMs = now;
        }
    }

    private void PushToPad()
    {
        var p = _pad!;
        p.SetButtonState(Xbox360Button.A, _xb.A);
        p.SetButtonState(Xbox360Button.B, _xb.B);
        p.SetButtonState(Xbox360Button.X, _xb.X);
        p.SetButtonState(Xbox360Button.Y, _xb.Y);
        p.SetButtonState(Xbox360Button.LeftShoulder, _xb.LB);
        p.SetButtonState(Xbox360Button.RightShoulder, _xb.RB);
        p.SetButtonState(Xbox360Button.Back, _xb.Back);
        p.SetButtonState(Xbox360Button.Start, _xb.Start);
        p.SetButtonState(Xbox360Button.LeftThumb, _xb.LS);
        p.SetButtonState(Xbox360Button.RightThumb, _xb.RS);
        p.SetButtonState(Xbox360Button.Guide, _xb.Guide);
        p.SetButtonState(Xbox360Button.Up, _xb.DUp);
        p.SetButtonState(Xbox360Button.Down, _xb.DDown);
        p.SetButtonState(Xbox360Button.Left, _xb.DLeft);
        p.SetButtonState(Xbox360Button.Right, _xb.DRight);
        p.SetAxisValue(Xbox360Axis.LeftThumbX, _xb.LX);
        p.SetAxisValue(Xbox360Axis.LeftThumbY, _xb.LY);
        p.SetAxisValue(Xbox360Axis.RightThumbX, _xb.RX);
        p.SetAxisValue(Xbox360Axis.RightThumbY, _xb.RY);
        p.SetSliderValue(Xbox360Slider.LeftTrigger, _xb.LT);
        p.SetSliderValue(Xbox360Slider.RightTrigger, _xb.RT);
    }

    public bool RebuildRequested { get; private set; }

    private void Fault(string reason, bool rebuild = false)
    {
        _status = SessionStatus.Faulted;
        FaultReason = reason;
        RebuildRequested = rebuild;
        Log.Error($"[{ShortId}] session FAULTED: {reason}");
        if (!_stop && Interlocked.Exchange(ref _faultReported, 1) == 0)
        {
            try { _onFaulted(this); }
            catch (Exception ex) { Log.Error($"[{ShortId}] onFaulted handler threw", ex); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _status = SessionStatus.Disposing;
        Log.Info($"[{ShortId}] session DESTROYING guid={InstanceGuid}");
        _stop = true;

        DisposeInternal(WaitForInputThread());
        _status = SessionStatus.Disposed;
        Log.Info($"[{ShortId}] session DESTROYED guid={InstanceGuid}");
    }

    // The read thread wakes at least once a second (ReadTimeout), so 3 s is ample. Past that it is stuck
    // in a native call: closing the stream may free it, and nothing else it uses is disposed under it.
    private bool WaitForInputThread()
    {
        if (_thread is null) return true;
        try
        {
            if (_thread.Join(3000)) return true;
            Safe("hid reader", () => _hidReader?.Dispose());
            if (_thread.Join(2000)) return true;
        }
        catch (ThreadStateException) { return true; }   // never started
        Log.Error($"[{ShortId}] input thread did not exit - leaving its virtual pad and HID handles allocated");
        return false;
    }

    // Every step guarded so nothing is skipped on the unhappy paths.
    private void DisposeInternal(bool threadExited = true)
    {
        Safe("pad unsubscribe", () => { if (_pad is not null) _pad.FeedbackReceived -= OnFeedbackReceived; });
        if (!threadExited) return;

        Safe("rumble stop", () => { if (_rumble is { IsInvalid: false }) SendPayload(0xF3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00); });
        Safe("rumble handle", () => { _rumble?.Dispose(); _rumble = null; });
        Safe("hid reader", () => { _hidReader?.Dispose(); _hidReader = null; });
        Safe("hid parser", () => { _parser?.Dispose(); _parser = null; });

        Safe("pad reset", () => { if (_pad is not null) { _pad.ResetReport(); _pad.SubmitReport(); } });
        Safe("pad disconnect", () => _pad?.Disconnect());
        Safe("pad dispose", () => { (_pad as IDisposable)?.Dispose(); _pad = null; });
    }

    private void Safe(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Warn($"[{ShortId}] cleanup '{what}' failed: {ex.Message}"); }
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOf('\n');
        return nl < 0 ? s : s[..nl].TrimEnd();
    }
}
