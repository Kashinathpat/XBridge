using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using XBridge.Config;
using XBridge.Diagnostics;
using XBridge.Input;
using XBridge.Interop;
using Vortice.DirectInput;

namespace XBridge.Core;

internal enum SessionStatus { Starting, Running, Faulted, Disposing, Disposed }

internal sealed class SessionStartException : Exception
{
    public SessionStartException(string message, Exception? inner = null) : base(message, inner) { }

    // Set when the failure is expected to clear on its own (e.g. HidHide driver momentarily held by another app).
    public bool Transient { get; init; }
}

// One physical gamepad: own DI handle (input), raw HID handle (rumble), ViGEm pad, cloak entry, poll thread.
// FeedbackReceived only stages bytes for this session's HID handle, so rumble can't cross to another unit.
internal sealed class ControllerSession : IDisposable
{
    public Guid InstanceGuid => _unit.DiInstanceGuid;
    public string ProductName => _unit.ProductName;
    public SessionStatus Status => _status;
    public string? FaultReason { get; private set; }

    private readonly GamepadUnit _unit;
    private readonly IntPtr _coopHwnd;
    private readonly ViGEmClient _client;
    private readonly InputMapper _mapper;
    private readonly AppConfig _cfg;
    private readonly HidHideManager _hidHide;
    private readonly string _ownExe;
    private readonly Action<ControllerSession> _onFaulted;

    private volatile SessionStatus _status = SessionStatus.Starting;
    private string ShortId => _unit.Short;

    private IDirectInput8? _di;
    private IDirectInputDevice8? _dev;
    private CooperativeLevel _coop;
    private SafeFileHandle? _rumble;
    private IXbox360Controller? _pad;
    private bool _cloaked;
    private bool _keepCloak;   // rebuild for the same physical slot - leave the HidHide block in place

    private Thread? _thread;
    private volatile bool _stop;
    private readonly double _targetMs;
    private long _lastAttachCheck;
    private int _attachCheckCount;

    private int _wantRumble;   // packed (strong << 8) | weak, written by the ViGEm callback
    private int _rumbleDirty;
    private long _lastRumbleApplyMs;
    private long _lastRumbleErrMs;
    private readonly byte[] _txBuf;

    private JoystickState _state = new();
    private XboxState _xb;

    private long _lastLoopTicks;
    private long _jitterCount;
    private double _jitterSumMs;
    private double _jitterMaxMs;
    private long _lastJitterWarnMs;
    private long _lastJitterReportMs;

    private int _disposed;
    private int _faultReported;

    public ControllerSession(
        GamepadUnit unit, IntPtr coopHwnd, ViGEmClient client, InputMapper mapper,
        AppConfig cfg, HidHideManager hidHide, string ownExe, Action<ControllerSession> onFaulted)
    {
        _unit = unit;
        _coopHwnd = coopHwnd;
        _client = client;
        _mapper = mapper;
        _cfg = cfg;
        _hidHide = hidHide;
        _ownExe = ownExe;
        _onFaulted = onFaulted;
        _targetMs = Math.Max(1, cfg.PollIntervalMs);
        _txBuf = new byte[Math.Max(8, unit.HidMaxOutputLength)];
    }

    public void Start()
    {
        Log.Info($"[{ShortId}] session CREATED guid={InstanceGuid} name=\"{ProductName}\" " +
                 $"pairing={_unit.MatchKind} hid={_unit.HidDevicePath ?? "<none>"} cloak={_unit.HidHideInstancePath ?? "<none>"}");
        try
        {
            OpenRumbleHandleStep();
            CloakStep();
            OpenDeviceStep();
            AcquireStep();
            VerifyPairingStep();
            SendIdle();
            VirtualPadStep();   // wires FeedbackReceived only after pairing is verified

            _status = SessionStatus.Running;
            _thread = new Thread(PollThread) { Name = $"Poll-{ShortId}", IsBackground = true, Priority = ThreadPriority.AboveNormal };
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
        if (string.IsNullOrEmpty(_unit.HidDevicePath))
        {
            if (_cfg.RequireHidPairing)
                throw new SessionStartException(
                    "No raw HID device paired to this unit - rumble can't be routed. " +
                    "Set RequireHidPairing=false in config.json for input-only.");
            Log.Warn($"[{ShortId}] no paired HID path - input-only, rumble disabled");
            return;
        }

        _rumble = NativeHid.OpenForWrite(_unit.HidDevicePath!, out string? err);
        if (_rumble is not null && !_rumble.IsInvalid)
        {
            Log.Info($"[{ShortId}] rumble HID handle open");
            return;
        }

        _rumble?.Dispose();
        _rumble = null;
        string msg = $"Could not open the paired raw HID handle for rumble: {err ?? "unknown error"}";
        if (_cfg.RequireHidPairing) throw new SessionStartException(msg);
        Log.Warn($"[{ShortId}] {msg} - input-only");
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
        if (_cloaked) return;

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
        if (!svc.Installed) return "HidHide is not installed (HidHideMSI_x64.msi); reboot after installing.";
        if (!svc.Running) return "HidHide service is stopped ('sc start HidHide' or reboot).";
        string blob = (err ?? "").ToLowerInvariant();
        if (blob.Contains("no other process") || blob.Contains("driveraccessfailed"))
            return "Another program is using the HidHide driver - close the HidHide Configuration Client if it is open; XBridge keeps retrying.";
        if (blob.Contains("0x0005") || blob.Contains("access is denied") || blob.Contains("access denied"))
            return "HidHide denied driver access - its config is inconsistent; repair/reinstall HidHide, or set RequireCloak=false.";
        return "See the HidHide error in the log.";
    }

    private void OpenDeviceStep()
    {
        try
        {
            _di = DInput.DirectInput8Create();
            _dev = _di.CreateDevice(InstanceGuid);
            _dev.SetDataFormat<RawJoystickState>().CheckError();
            _coop = (_cfg.DirectInputExclusive ? CooperativeLevel.Exclusive : CooperativeLevel.NonExclusive)
                    | CooperativeLevel.Background;
            _dev.SetCooperativeLevel(_coopHwnd, _coop).CheckError();
        }
        catch (Exception ex)
        {
            throw new SessionStartException("Failed to open the DirectInput device: " + ex.Message, ex);
        }
    }

    private void AcquireStep()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _dev!.Acquire().CheckError();
                if (attempt > 0) Log.Info($"[{ShortId}] acquired on attempt {attempt + 1}");
                return;
            }
            catch (Exception ex)
            {
                if (attempt + 1 >= _cfg.AcquireAttempts)
                    throw new SessionStartException(
                        $"Acquire failed after {_cfg.AcquireAttempts} attempts - the device is likely held by " +
                        $"another app (Steam Input, a game launcher, or a leftover instance). Last error: {ex.Message}", ex);
                int backoff = _cfg.BackoffFor(attempt);
                Log.Warn($"[{ShortId}] acquire {attempt + 1}/{_cfg.AcquireAttempts} failed ({ex.Message}); retry in {backoff} ms");
                Thread.Sleep(backoff);
                if (_stop) throw new SessionStartException("Cancelled during acquire retries.", ex);
            }
        }
    }

    private void VerifyPairingStep()
    {
        string live;
        try { live = _dev!.Properties.InterfacePath ?? ""; }
        catch (Exception ex) { live = "<read failed: " + ex.Message + ">"; }

        bool consistent = string.Equals(live.Trim(), _unit.DiInterfacePath.Trim(), StringComparison.OrdinalIgnoreCase);
        Log.Info($"[{ShortId}] PAIRED  DI={InstanceGuid} path={live}  <->  HID={_unit.HidDevicePath ?? "<none>"}  " +
                 $"cloak={_unit.HidHideInstancePath ?? "<none>"}  match={_unit.MatchKind}  consistent={consistent}");

        // Positional pairing deliberately ignores the (ambiguous) DI path for identical units.
        if (!consistent && _unit.MatchKind != "positional")
            Log.Warn($"[{ShortId}] DI InterfacePath differs from the profiled one ('{_unit.DiInterfacePath}') - " +
                     "check rumble lands on the right pad.");
        if (_rumble is null)
            Log.Warn($"[{ShortId}] no rumble handle - input-only");
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

    // Called before Dispose when rebuilding for the same physical slot, so the device does
    // not flash visible between teardown and the rebuilt session re-cloaking.
    public void KeepCloakOnDispose() => _keepCloak = true;

    // Poll thread only - single-threads all raw HID access.
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

    private void PollThread()
    {
        var sw = Stopwatch.StartNew();
        int errStreak = 0;

        while (!_stop)
        {
            long loopStart = sw.ElapsedTicks;
            long nowMs = loopStart * 1000 / Stopwatch.Frequency;
            MeasureJitter(loopStart, nowMs);

            try
            {
                _dev!.Poll().CheckError();
                _dev.GetCurrentJoystickState(ref _state);
                errStreak = 0;
                _attachCheckCount = 0;

                _mapper.Map(_state, ref _xb);
                PushToPad();
                _pad!.SubmitReport();
                ServiceRumble(nowMs);
            }
            catch (Exception ex)
            {
                errStreak++;
                if (errStreak is 1 or 10 || errStreak % 60 == 0)
                    Log.Warn($"[{ShortId}] poll error #{errStreak}: {ex.Message}");

                TryReacquire();

                // Re-acquire and keep going for a transient loss; if it can't recover within ~2
                // attach checks, fault so the manager rebuilds the session.
                if (errStreak >= _cfg.PollErrorsBeforeAttachCheck &&
                    Environment.TickCount64 - _lastAttachCheck > 2000)
                {
                    _lastAttachCheck = Environment.TickCount64;
                    if (!DevicePairer.IsAttached(InstanceGuid))
                    {
                        Fault("Physical device is no longer attached.");
                        return;
                    }
                    if (++_attachCheckCount >= 2)
                    {
                        Fault("Device is attached but this session can't read it (identity changed?) - rebuilding.", rebuild: true);
                        return;
                    }
                    Log.Warn($"[{ShortId}] poll errors persist ({errStreak}), device still attached - one more retry window");
                }
                Thread.Sleep(50);
                continue;
            }

            SleepToTarget(sw, loopStart);
        }
    }

    // Recovers a device that blinked out and came back under the same InstanceGuid.
    private void TryReacquire()
    {
        try
        {
            _dev?.Unacquire();
            _dev?.SetCooperativeLevel(_coopHwnd, _coop);
            _dev?.Acquire();
        }
        catch { }
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

    private void MeasureJitter(long loopStartTicks, long nowMs)
    {
        if (_lastLoopTicks != 0)
        {
            double dtMs = (loopStartTicks - _lastLoopTicks) * 1000.0 / Stopwatch.Frequency;
            _jitterCount++;
            _jitterSumMs += dtMs;
            if (dtMs > _jitterMaxMs) _jitterMaxMs = dtMs;

            if (dtMs > _cfg.JitterWarnMs && nowMs - _lastJitterWarnMs > 2000)
            {
                Log.Warn($"[{ShortId}] poll interval {dtMs:F1} ms > {_cfg.JitterWarnMs:F1} ms (CPU load / GC pause)");
                _lastJitterWarnMs = nowMs;
            }
            if (_cfg.JitterReportSeconds > 0 && nowMs - _lastJitterReportMs > _cfg.JitterReportSeconds * 1000L)
            {
                double avg = _jitterSumMs / Math.Max(1, _jitterCount);
                Log.Info($"[{ShortId}] poll timing: avg {avg:F2} ms / max {_jitterMaxMs:F1} ms over {_jitterCount} iters (target {_targetMs:F0} ms)");
                _jitterCount = 0; _jitterSumMs = 0; _jitterMaxMs = 0;
                _lastJitterReportMs = nowMs;
            }
        }
        _lastLoopTicks = loopStartTicks;
    }

    private void SleepToTarget(Stopwatch sw, long loopStartTicks)
    {
        double Elapsed() => (sw.ElapsedTicks - loopStartTicks) * 1000.0 / Stopwatch.Frequency;
        double remaining = _targetMs - Elapsed();
        if (remaining > 1.5)
            Thread.Sleep((int)(remaining - (_cfg.HighPrecisionTiming ? 1.0 : 0.0)));
        if (_cfg.HighPrecisionTiming)
            while (Elapsed() < _targetMs && !_stop) Thread.SpinWait(40);
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

        try { _thread?.Join(3000); }
        catch (Exception ex) { Log.Warn($"[{ShortId}] poll thread join: {ex.Message}"); }

        DisposeInternal();
        _status = SessionStatus.Disposed;
        Log.Info($"[{ShortId}] session DESTROYED guid={InstanceGuid}");
    }

    // Every step guarded so nothing is skipped on the unhappy paths.
    private void DisposeInternal()
    {
        Safe("rumble stop", () => { if (_rumble is { IsInvalid: false }) SendPayload(0xF3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00); });
        Safe("rumble handle", () => { _rumble?.Dispose(); _rumble = null; });

        Safe("pad unsubscribe", () => { if (_pad is not null) _pad.FeedbackReceived -= OnFeedbackReceived; });
        Safe("pad reset", () => { if (_pad is not null) { _pad.ResetReport(); _pad.SubmitReport(); } });
        Safe("pad disconnect", () => _pad?.Disconnect());
        Safe("pad dispose", () => { (_pad as IDisposable)?.Dispose(); _pad = null; });

        Safe("device unacquire", () => _dev?.Unacquire());
        Safe("device dispose", () => { _dev?.Dispose(); _dev = null; });
        Safe("dinput dispose", () => { _di?.Dispose(); _di = null; });

        if (_cloaked && !_keepCloak && _unit.HidHideInstancePath is not null)
            Safe("hidhide unhide", () => { _hidHide.UnhideDevice(_unit.HidHideInstancePath); _cloaked = false; });
        else if (_cloaked && _keepCloak)
            Log.Info($"[{ShortId}] HidHide cloak kept across rebuild");
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
