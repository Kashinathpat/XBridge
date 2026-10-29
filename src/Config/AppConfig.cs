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

    public DeviceId() { }
    public DeviceId(string vid, string pid) { Vid = vid; Pid = pid; }

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
    public List<DeviceId> Devices { get; set; } = new()
    {
        new DeviceId("0079", "0006"),
        new DeviceId("038F", "1200"),
    };

    // radial dead zones, fraction of full range
    public double LeftStickDeadzone { get; set; } = 0.12;
    public double RightStickDeadzone { get; set; } = 0.12;
    public double TriggerDeadzone { get; set; } = 0.06;

    // ignore a device this long after its session faulted
    public int FaultCooldownSeconds { get; set; } = 15;

    // safety-net re-enumeration interval, seconds (also covers sleep/resume)
    public int ReenumerateSeconds { get; set; } = 30;
    // debounce window for WM_DEVICECHANGE bursts, ms
    public int DeviceChangeDebounceMs { get; set; } = 400;
    // consecutive enumerations a running session may be missing before it's torn down (DI enum is racy during hot-plug)
    public int AbsenceGracePasses { get; set; } = 3;
    // reconciles to wait for a new unit's DI<->HID pairing to resolve before reporting it can't be paired
    public int PairRetryPasses { get; set; } = 6;
    // re-assert the HidHide block for running sessions this often, seconds (0 = off); guards against HidHide state drift
    public int CloakReassertSeconds { get; set; } = 120;

    // don't expose a virtual pad unless the physical one was cloaked
    public bool RequireCloak { get; set; } = true;

    // times the 0x51 / 0xFA-0xFE pair is sent per change (this pad needs a few to latch)
    public int RumbleRepeat { get; set; } = 3;
    // re-send the current rumble state at least this often while non-zero, ms
    public int RumbleKeepAliveMs { get; set; } = 1000;
    // DragonRise quirk: send 0x0B when the weak value would be 0x0A
    public bool RumbleWeak0x0AQuirk { get; set; } = true;

    public string LogLevel { get; set; } = "Info";
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
        if (!File.Exists(ConfigPath))
        {
            var def = new AppConfig();
            def.Save();
            Log.Info($"Config created with defaults at {ConfigPath}");
            return def;
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts)
                      ?? throw new JsonException("the file contains null");
            cfg.Clamp();
            cfg.Save();
            Log.Info($"Config loaded from {ConfigPath}");
            return cfg;
        }
        catch (Exception ex)
        {
            // Leave the file as-is so one typo doesn't wipe the user's settings.
            Log.Error($"Config load failed - running on defaults; {ConfigPath} left unchanged so it can be fixed", ex);
            return new AppConfig();
        }
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

    private void Clamp()
    {
        int listed = Devices?.Count ?? 0;
        Devices = (Devices ?? new()).Where(d => d is not null && d.IsValid).ToList();
        if (Devices.Count < listed)
            Log.Warn($"Config: ignored {listed - Devices.Count} invalid Devices entries (each needs a 4-hex-digit Vid and Pid)");
        if (Devices.Count == 0)
            Log.Warn("Config: the Devices list is empty - no controllers will be bridged");

        LeftStickDeadzone = Math.Clamp(LeftStickDeadzone, 0, 0.9);
        RightStickDeadzone = Math.Clamp(RightStickDeadzone, 0, 0.9);
        TriggerDeadzone = Math.Clamp(TriggerDeadzone, 0, 0.9);
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
        if (Enum.TryParse<LogLevel>(LogLevel, true, out var lvl))
            Log.MinimumLevel = lvl;
        else
            Log.Warn($"Config: invalid LogLevel '{LogLevel}' (expected Debug, Info, Warn, Error) - defaulting to Info");
    }
}
