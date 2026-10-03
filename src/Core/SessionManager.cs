using Microsoft.Win32;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using XBridge.Config;
using XBridge.Diagnostics;
using XBridge.Input;

namespace XBridge.Core;

internal enum ManagerStatus { Idle, Active, Error }

internal readonly record struct ManagerState(ManagerStatus Status, int ActiveCount, int FaultedCount, string? Message);

// Owns one ControllerSession per gamepad (max 4). Reacts to each hot-plug transition under _lock
// without disturbing running sessions; a started session runs on its own thread.
internal sealed class SessionManager
{
    private readonly object _lock = new();
    // Keyed by KeyOf() (the HID device path) not the DI InstanceGuid, which DirectInput
    // reshuffles among identical VID/PID units on every plug/unplug.
    private readonly Dictionary<string, ControllerSession> _sessions = new();
    private readonly Dictionary<string, DateTime> _cooldownUntil = new();
    private readonly Dictionary<string, int> _missingPasses = new();
    private readonly Dictionary<Guid, int> _unpairedPasses = new();   // pre-pairing retry counter - guid is all we have
    private readonly HashSet<string> _warnedTransient = new();
    private readonly HashSet<string> _warnedRawInput = new();
    private readonly Dictionary<(ushort vid, ushort pid), InputMapper> _mappers = new();
    private DateTime _lastCloakReassert = DateTime.MinValue;

    private readonly AppConfig _cfg;
    private readonly HidHideManager _hidHide;
    private readonly string _ownExe;

    private ViGEmClient? _client;
    private string? _clientError;

    private System.Threading.Timer? _periodicTimer;
    private System.Threading.Timer? _debounceTimer;
    private int _reconcilePending;
    private int _disposedFlag;
    private volatile bool _started;

    private bool _disposed => Volatile.Read(ref _disposedFlag) != 0;

    public event Action<ManagerState>? StateChanged;
    public Action<string, string, bool>? Notify { get; set; }   // title, body, isError -> tray balloon

    public SessionManager(AppConfig cfg, HidHideManager hidHide, string ownExe)
    {
        _cfg = cfg;
        _hidHide = hidHide;
        _ownExe = ownExe;
    }

    // One InputMapper (and mapping file) per VID/PID - different controller layouts differ.
    private InputMapper MapperFor(ushort vid, ushort pid)
    {
        if (!_mappers.TryGetValue((vid, pid), out var m))
            _mappers[(vid, pid)] = m = new InputMapper(MappingProfile.LoadOrDefault(vid, pid), _cfg);
        return m;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        try { SystemEvents.PowerModeChanged += OnPowerModeChanged; }
        catch (Exception ex) { Log.Warn("Could not subscribe to PowerModeChanged: " + ex.Message); }

        int periodMs = Math.Max(1000, _cfg.ReenumerateSeconds * 1000);
        _periodicTimer = new System.Threading.Timer(_ => SafeReconcile("periodic"), null, periodMs, periodMs);

        Log.Info($"SessionManager started (re-enumerate {_cfg.ReenumerateSeconds}s, debounce {_cfg.DeviceChangeDebounceMs}ms, cap {_cfg.EffectiveMaxControllers}).");
        SafeReconcile("startup");
    }

    // Debounced - call from WM_DEVICECHANGE.
    public void RequestReconcile()
    {
        if (_disposed) return;
        int due = Math.Clamp(_cfg.DeviceChangeDebounceMs, 50, 5000);
        try
        {
            _debounceTimer ??= new System.Threading.Timer(_ => SafeReconcile("device-change"), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(due, Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }
    }

    public ManagerState CurrentState()
    {
        lock (_lock) return ComputeState();
    }

    private void SafeReconcile(string trigger)
    {
        try { Reconcile(trigger); }
        catch (Exception ex) { Log.Error($"Reconcile ({trigger}) threw", ex); }
    }

    private void Reconcile(string trigger)
    {
        if (_disposed) return;

        if (!Monitor.TryEnter(_lock))
        {
            Volatile.Write(ref _reconcilePending, 1);   // fold into the in-progress pass
            return;
        }
        try
        {
            do
            {
                Volatile.Write(ref _reconcilePending, 0);
                ReconcileOnce(trigger);
            }
            while (Volatile.Read(ref _reconcilePending) == 1 && !_disposed);
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    // Stable per-slot identity: the HID device path (USB-port derived). Falls back to the DI
    // guid only for a not-yet-paired unit, which has no HID path.
    private static string KeyOf(GamepadUnit u) =>
        u.HasHidPath ? u.HidDevicePath!.Trim().ToLowerInvariant() : "di:" + u.DiInstanceGuid;

    private void ReconcileOnce(string trigger)
    {
        var present = DevicePairer.Enumerate(_cfg.Devices);
        var presentByKey = new Dictionary<string, GamepadUnit>();
        foreach (var u in present) presentByKey[KeyOf(u)] = u;

        // A totally-empty enumeration while sessions are running is almost always an
        // enumeration hiccup - a faulted session still gets cleaned, absent ones do not.
        bool enumSuspect = present.Count == 0 && _sessions.Count > 0;
        if (enumSuspect)
            Log.Warn($"Reconcile({trigger}): enumeration returned 0 devices but {_sessions.Count} session(s) live - treating as a hiccup");

        foreach (var (key, session) in _sessions.ToArray())
        {
            string sid = Short(session.InstanceGuid);
            presentByKey.TryGetValue(key, out var unit);
            bool listedAbsent = unit is null;

            if (session.Status is SessionStatus.Faulted)
            {
                bool rebuild = session.RebuildRequested;
                Log.Info($"Reconcile({trigger}): tearing down {sid} - faulted: {session.FaultReason}");
                RemoveSession(key, session);
                if (!rebuild && !listedAbsent)
                    _cooldownUntil[key] = DateTime.UtcNow.AddSeconds(_cfg.FaultCooldownSeconds);
                continue;
            }

            if (enumSuspect)
            {
                _missingPasses.Remove(key);
                continue;
            }

            if (!listedAbsent)
            {
                _missingPasses.Remove(key);
                continue;
            }

            // Absent from enumeration - a few consecutive misses before tearing down (enum can be racy).
            int n = _missingPasses[key] = _missingPasses.GetValueOrDefault(key) + 1;
            if (n < _cfg.AbsenceGracePasses)
            {
                Log.Info($"Reconcile({trigger}): {sid} missing from enumeration ({n}/{_cfg.AbsenceGracePasses}) - keeping");
                continue;
            }
            Log.Info($"Reconcile({trigger}): tearing down {sid} - device removed ({n} passes)");
            RemoveSession(key, session);
        }

        if (presentByKey.Count > _sessions.Count && !EnsureClient())
        {
            PublishState();
            return;
        }

        ReassertCloakIfDue();

        foreach (var unit in present)
        {
            string key = KeyOf(unit);
            Guid guid = unit.DiInstanceGuid;
            if (_sessions.ContainsKey(key)) continue;

            if (_cooldownUntil.TryGetValue(key, out var until))
            {
                if (DateTime.UtcNow < until) continue;
                _cooldownUntil.Remove(key);
            }

            if (_sessions.Count >= _cfg.EffectiveMaxControllers)
            {
                Log.Warn($"Reconcile({trigger}): {Short(guid)} present but at the {_cfg.EffectiveMaxControllers}-pad ViGEm limit");
                continue;
            }

            // A fresh unit's DI<->HID pairing can take a few reconciles to resolve after a
            // hot-plug - don't hard-fail it (balloon + 15s cooldown) until it's been "none" a while.
            if (unit.MatchKind == "none")
            {
                int u = _unpairedPasses[guid] = _unpairedPasses.GetValueOrDefault(guid) + 1;
                if (u < _cfg.PairRetryPasses)
                {
                    Log.Info($"Reconcile({trigger}): {Short(guid)} not yet paired to a raw HID device ({u}/{_cfg.PairRetryPasses}) - retrying");
                    continue;
                }
                Log.Warn($"Reconcile({trigger}): {Short(guid)} still unpaired after {u} passes.");
            }
            else
            {
                _unpairedPasses.Remove(guid);
            }

            var session = new ControllerSession(unit, _client!, MapperFor(unit.Vid, unit.Pid),
                _cfg, _hidHide, _ownExe, OnSessionFaulted);
            try
            {
                session.Start();
                _sessions[key] = session;
                _unpairedPasses.Remove(guid);
                _warnedTransient.Remove(key);
                if (session.RawInputVisible && _warnedRawInput.Add(key))
                    Notify?.Invoke("Reconnect controller once",
                        $"\"{unit.ProductName.Trim()}\" was connected before XBridge hid it, so browsers can still see it. " +
                        "Disconnect and reconnect it once; after that it stays hidden.", false);
            }
            catch (SessionStartException ex)
            {
                string firstLine = ex.Message.Split('\n')[0].TrimEnd();
                Log.Warn($"Reconcile({trigger}): {Short(guid)} did not start - {firstLine}");
                SafeDisposeSession(session);

                if (ex.Transient)
                {
                    // e.g. HidHide driver momentarily held by another app - retry soon, don't alarm.
                    _cooldownUntil[key] = DateTime.UtcNow.AddSeconds(3);
                    if (_warnedTransient.Add(key)) Notify?.Invoke("Waiting on HidHide", firstLine, false);
                }
                else
                {
                    _cooldownUntil[key] = DateTime.UtcNow.AddSeconds(_cfg.FaultCooldownSeconds);
                    Notify?.Invoke("Controller not started", firstLine, true);
                }

                if (IsViGEmBusGone(ex))
                {
                    Log.Error("Reconcile: ViGEmBus appears gone - dropping client, will re-create.");
                    DropClient("ViGEmBus stopped responding");
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Reconcile({trigger}): {Short(guid)} unexpected start error", ex);
                SafeDisposeSession(session);
                _cooldownUntil[key] = DateTime.UtcNow.AddSeconds(_cfg.FaultCooldownSeconds);
            }
        }

        PublishState();
    }

    private void OnSessionFaulted(ControllerSession s)
    {
        Log.Warn($"SessionManager: {Short(s.InstanceGuid)} signalled a fault ({s.FaultReason}) - scheduling reconcile");
        RequestReconcile();
    }

    private void RemoveSession(string key, ControllerSession session)
    {
        _sessions.Remove(key);
        _missingPasses.Remove(key);
        _warnedTransient.Remove(key);
        _unpairedPasses.Remove(session.InstanceGuid);
        try { session.Dispose(); }
        catch (Exception ex) { Log.Error($"Reconcile: dispose of {Short(session.InstanceGuid)} threw", ex); }
    }

    private void ReassertCloakIfDue()
    {
        if (_cfg.CloakReassertSeconds <= 0) return;
        if ((DateTime.UtcNow - _lastCloakReassert).TotalSeconds < _cfg.CloakReassertSeconds) return;
        _lastCloakReassert = DateTime.UtcNow;
        foreach (var s in _sessions.Values)
            if (s.Status == SessionStatus.Running)
                try { s.ReassertCloak(); } catch (Exception ex) { Log.Warn($"ReassertCloak {Short(s.InstanceGuid)}: {ex.Message}"); }
    }

    private bool EnsureClient()
    {
        if (_client is not null) return true;
        try
        {
            _client = new ViGEmClient();
            _clientError = null;
            Log.Info("ViGEmClient connected to ViGEmBus.");
            return true;
        }
        catch (Exception ex)
        {
            var d = Drivers.Query(Drivers.ViGEmService);
            string svc = !d.Installed ? "NOT INSTALLED" : d.Running ? "running" : "INSTALLED BUT STOPPED";
            _clientError = $"ViGEmBus not usable (service: {svc}). {ex.GetType().Name}: {ex.Message}";
            Log.Error(_clientError, ex);
            Notify?.Invoke("ViGEmBus unavailable",
                d.Installed && !d.Running
                    ? "The ViGEmBus service is installed but stopped - 'sc start ViGEmBus' or reboot, then reconnect the pad."
                    : "The ViGEmBus driver is not installed - reinstall ViGEmBusSetup_x64.msi, then reconnect the pad.",
                true);
            return false;
        }
    }

    private void DropClient(string why)
    {
        _clientError = why;
        try { _client?.Dispose(); } catch (Exception ex) { Log.Warn("Disposing dead ViGEmClient: " + ex.Message); }
        _client = null;
    }

    private static bool IsViGEmBusGone(Exception ex) =>
        ex is VigemBusNotFoundException || ex.InnerException is VigemBusNotFoundException;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Log.Info("System resumed - forcing reconcile (USB may have re-enumerated).");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000).ConfigureAwait(false);
                SafeReconcile("resume");
                await Task.Delay(3000).ConfigureAwait(false);   // second pass - some hubs settle slowly
                SafeReconcile("resume-2");
            }
            catch (Exception ex) { Log.Error("resume reconcile task threw", ex); }
        });
    }

    private ManagerState ComputeState()
    {
        int active = _sessions.Values.Count(s => s.Status == SessionStatus.Running);
        int faulted = _sessions.Values.Count(s => s.Status == SessionStatus.Faulted);

        if (_clientError is not null) return new ManagerState(ManagerStatus.Error, active, faulted, _clientError);
        if (faulted > 0) return new ManagerState(ManagerStatus.Error, active, faulted, $"{faulted} controller(s) in an error state");
        if (active == 0) return new ManagerState(ManagerStatus.Idle, 0, 0, null);
        return new ManagerState(ManagerStatus.Active, active, 0, null);
    }

    private void PublishState()
    {
        ManagerState st = ComputeState();
        try { StateChanged?.Invoke(st); }
        catch (Exception ex) { Log.Error("StateChanged handler threw", ex); }
    }

    private static string Short(Guid g) => g.ToString()[..8];

    private void SafeDisposeSession(ControllerSession s)
    {
        try { s.Dispose(); } catch (Exception ex) { Log.Warn("SafeDisposeSession: " + ex.Message); }
    }

    public void DisposeAll()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { _periodicTimer?.Dispose(); } catch { }
        try { _debounceTimer?.Dispose(); } catch { }

        lock (_lock)
        {
            Log.Info($"SessionManager shutting down - {_sessions.Count} session(s).");
            foreach (var s in _sessions.Values)
            {
                try { s.Dispose(); }
                catch (Exception ex) { Log.Error($"Shutdown dispose of {Short(s.InstanceGuid)} threw", ex); }
            }
            _sessions.Clear();

            try { _client?.Dispose(); }
            catch (Exception ex) { Log.Error("ViGEmClient dispose threw", ex); }
            _client = null;
        }

        Log.Info("SessionManager shutdown complete.");
    }
}
