using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using XBridge.Diagnostics;

namespace XBridge.Config;

// A controller to bridge, by USB VID/PID as 4-hex-digit strings ("0079", "038f").
internal sealed class DeviceId
{
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";
    // "dinput" (default) or "rawhid" - the latter reads HID input reports directly, for pads
    // that only send on change (Bluetooth phone gamepads) and choke DirectInput polling.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Input { get; set; }

    public DeviceId() { }
    public DeviceId(string vid, string pid) { Vid = vid; Pid = pid; }

    [JsonIgnore] public bool RawHid => string.Equals(Input?.Trim(), "rawhid", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsValid => TryHex(Vid, out _) && TryHex(Pid, out _);
    [JsonIgnore] public ushort VidValue => TryHex(Vid, out var v) ? v : (ushort)0;
    [JsonIgnore] public ushort PidValue => TryHex(Pid, out var v) ? v : (ushort)0;

    public bool Matches(ushort vid, ushort pid) => IsValid && VidValue == vid && PidValue == pid;

    private static bool TryHex(string? s, out ushort value) =>
        ushort.TryParse((s ?? "").Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
}

// Runtime knobs, persisted to %LocalAppData%\XBridge\config.json and
// rewritten with defaults on load so new keys appear in the user's file.
internal sealed class AppConfig
{
    // controllers to bridge, by USB VID/PID (4 hex digits). Add an entry per new pad.
    public List<DeviceId> Devices { get; set; } = new() { new DeviceId("0079", "0006"), new DeviceId("038F", "1200") };

    // per-session poll target, ms (4-8 typical)
    public int PollIntervalMs { get; set; } = 5;
    // busy-spin the last ~1ms for lower jitter (burns a core per controller)
    public bool HighPrecisionTiming { get; set; } = false;
    // warn when an achieved poll interval exceeds this (ms)
    public double JitterWarnMs { get; set; } = 6.0;
    // seconds between poll-timing summary lines (0 = never)
    public int JitterReportSeconds { get; set; } = 60;

    // radial dead zones, fraction of full range
    public double LeftStickDeadzone { get; set; } = 0.12;
    public double RightStickDeadzone { get; set; } = 0.12;
    public double TriggerDeadzone { get; set; } = 0.06;

    // acquire attempts before a session faults
    public int AcquireAttempts { get; set; } = 5;
    // consecutive poll errors before the session checks IsDeviceAttached (transient DI loss is tolerated)
    public int PollErrorsBeforeAttachCheck { get; set; } = 20;
    // back-off between attempts, ms (last value repeats); kept short so one contested pad doesn't delay others
    public int[] AcquireBackoffMs { get; set; } = { 150, 350, 700, 1200, 2000 };
    // ignore a device this long after its session faulted
    public int FaultCooldownSeconds { get; set; } = 15;

    // safety-net re-enumeration interval, seconds (also covers sleep/resume)
    public int ReenumerateSeconds { get; set; } = 3;
    // debounce window for WM_DEVICECHANGE bursts, ms
    public int DeviceChangeDebounceMs { get; set; } = 400;
    // consecutive enumerations a running session may be missing before it's torn down (DI enum is racy during hot-plug)
    public int AbsenceGracePasses { get; set; } = 3;
    // reconciles to wait for a new unit's DI<->HID pairing to resolve before reporting it can't be paired
    public int PairRetryPasses { get; set; } = 6;
    // re-assert the HidHide block for running sessions this often, seconds (0 = off); guards against HidHide state drift
    public int CloakReassertSeconds { get; set; } = 30;

    // don't expose a virtual pad unless the physical one was cloaked
    public bool RequireCloak { get; set; } = true;
    // don't start a session unless its DI unit was paired to a raw HID device for rumble
    public bool RequireHidPairing { get; set; } = true;
    // DI cooperative level; NonExclusive by default - Exclusive can block the raw-HID rumble write.
    public bool DirectInputExclusive { get; set; } = false;

    // times the 0x51 / 0xFA-0xFE pair is sent per change (this pad needs a few to latch)
    public int RumbleRepeat { get; set; } = 3;
    // re-send the current rumble state at least this often while non-zero, ms
    public int RumbleKeepAliveMs { get; set; } = 1000;
    // DragonRise quirk: send 0x0B when the weak value would be 0x0A
    public bool RumbleWeak0x0AQuirk { get; set; } = true;

    public long LogMaxBytes { get; set; } = 1_048_576;
    public int LogMaxFiles { get; set; } = 5;

    [JsonIgnore] public int EffectiveMaxControllers => 4;   // ViGEm bus hard limit

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XBridge");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig LoadOrCreate()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg is not null)
                {
                    cfg.Clamp();
                    cfg.Save();
                    Log.Info($"Config loaded from {ConfigPath}");
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Config load failed - using defaults", ex);
        }

        var def = new AppConfig();
        def.Save();
        Log.Info($"Config created with defaults at {ConfigPath}");
        return def;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Error("Config save failed", ex);
        }
    }

    public int BackoffFor(int attemptIndex) =>
        AcquireBackoffMs.Length == 0 ? 500 : AcquireBackoffMs[Math.Min(attemptIndex, AcquireBackoffMs.Length - 1)];

    private void Clamp()
    {
        Devices = (Devices ?? new()).Where(d => d.IsValid).ToList();
        if (Devices.Count == 0) { Devices.Add(new DeviceId("0079", "0006")); Devices.Add(new DeviceId("038F", "1200")); }

        PollIntervalMs = Math.Clamp(PollIntervalMs, 1, 33);
        JitterWarnMs = Math.Clamp(JitterWarnMs, 1.0, 1000.0);
        LeftStickDeadzone = Math.Clamp(LeftStickDeadzone, 0, 0.9);
        RightStickDeadzone = Math.Clamp(RightStickDeadzone, 0, 0.9);
        TriggerDeadzone = Math.Clamp(TriggerDeadzone, 0, 0.9);
        AcquireAttempts = Math.Clamp(AcquireAttempts, 1, 50);
        PollErrorsBeforeAttachCheck = Math.Clamp(PollErrorsBeforeAttachCheck, 3, 500);
        if (AcquireBackoffMs is null || AcquireBackoffMs.Length == 0)
            AcquireBackoffMs = new[] { 150, 350, 700, 1200, 2000 };
        FaultCooldownSeconds = Math.Clamp(FaultCooldownSeconds, 1, 3600);
        ReenumerateSeconds = Math.Clamp(ReenumerateSeconds, 1, 120);
        DeviceChangeDebounceMs = Math.Clamp(DeviceChangeDebounceMs, 50, 5000);
        AbsenceGracePasses = Math.Clamp(AbsenceGracePasses, 1, 20);
        PairRetryPasses = Math.Clamp(PairRetryPasses, 1, 60);
        CloakReassertSeconds = Math.Clamp(CloakReassertSeconds, 0, 600);
        RumbleRepeat = Math.Clamp(RumbleRepeat, 1, 20);
        RumbleKeepAliveMs = Math.Clamp(RumbleKeepAliveMs, 100, 10000);
        LogMaxBytes = Math.Max(64 * 1024, LogMaxBytes);
        LogMaxFiles = Math.Clamp(LogMaxFiles, 1, 50);
    }
}
