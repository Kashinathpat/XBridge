using System.Text.Json;
using XBridge.Diagnostics;

namespace XBridge.Config;

// Physical-pad -> Xbox 360 layout, one file per VID/PID (mapping-<vid>-<pid>.json).
// Axis index: 0=X 1=Y 2=Z 3=RotationX 4=RotationY 5=RotationZ. Button index: raw DInput number.
internal sealed class MappingProfile
{
    public int AxisMin { get; set; } = 0;
    public int AxisMax { get; set; } = 65535;

    // This pad's HID descriptor exposes only X(0), Y(1), Z(2), RotationZ(5); RotationX(3)
    // and RotationY(4) are phantom axes stuck at an extreme - so the right stick is Z + Rz.
    public StickMap LeftStick { get; set; } = new() { XAxis = 0, YAxis = 1 };
    public StickMap RightStick { get; set; } = new() { XAxis = 2, YAxis = 5 };

    public string TriggerMode { get; set; } = "Buttons";   // "Buttons" (digital L2/R2) or "ZAxisSplit"
    public int LeftTriggerButton { get; set; } = 6;
    public int RightTriggerButton { get; set; } = 7;
    public int ZSplitAxis { get; set; } = 2;

    public bool DpadFromPov { get; set; } = true;
    public int PovIndex { get; set; } = 0;
    public int DpadUpButton { get; set; } = -1;
    public int DpadDownButton { get; set; } = -1;
    public int DpadLeftButton { get; set; } = -1;
    public int DpadRightButton { get; set; } = -1;

    public ButtonMap Buttons { get; set; } = new();

    public sealed class StickMap
    {
        public int XAxis { get; set; }
        public int YAxis { get; set; }
        public bool InvertX { get; set; }
        public bool InvertY { get; set; }   // cancels the default DInput->Xbox Y flip
    }

    public sealed class ButtonMap
    {
        // Verified on a real device: physical order is 0=north 1=east 2=south 3=west.
        public int A { get; set; } = 2;
        public int B { get; set; } = 1;
        public int X { get; set; } = 3;
        public int Y { get; set; } = 0;
        public int LeftShoulder { get; set; } = 4;
        public int RightShoulder { get; set; } = 5;
        public int Back { get; set; } = 8;
        public int Start { get; set; } = 9;
        public int LeftThumb { get; set; } = 10;
        public int RightThumb { get; set; } = 11;
        public int Guide { get; set; } = -1;   // this pad has none
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string PathFor(ushort vid, ushort pid) =>
        Path.Combine(AppConfig.ConfigDir, $"mapping-{vid:x4}-{pid:x4}.json");

    public static MappingProfile LoadOrDefault(ushort vid, ushort pid)
    {
        string path = PathFor(vid, pid);

        // one-time migration of the old single mapping.json to the DragonRise profile
        string legacy = Path.Combine(AppConfig.ConfigDir, "mapping.json");
        if (!File.Exists(path) && File.Exists(legacy) && vid == 0x0079 && pid == 0x0006)
            try { File.Copy(legacy, path); Log.Info($"Migrated mapping.json -> {Path.GetFileName(path)}"); } catch { }

        if (!File.Exists(path))
        {
            var def = Default(vid, pid);
            def.Save(vid, pid);
            Log.Info($"Mapping created with defaults at {Path.GetFileName(path)} (run --calibrate to tune it)");
            return def;
        }

        try
        {
            var p = JsonSerializer.Deserialize<MappingProfile>(File.ReadAllText(path), JsonOpts)
                    ?? throw new JsonException("the file contains null");
            p.Validate(vid, pid, Path.GetFileName(path));
            Log.Info($"Mapping loaded from {Path.GetFileName(path)}");
            return p;
        }
        catch (Exception ex)
        {
            // Leave the file as-is so one typo doesn't wipe the user's calibration.
            Log.Error($"Mapping load failed for {Path.GetFileName(path)} - using defaults; file left unchanged so it can be fixed", ex);
            return Default(vid, pid);
        }
    }

    public void Save(ushort vid, ushort pid)
    {
        try
        {
            AtomicFile.WriteAllText(PathFor(vid, pid), JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Error("Mapping save failed", ex);
        }
    }

    private void Validate(ushort vid, ushort pid, string fileName)
    {
        var d = Default(vid, pid);
        LeftStick ??= d.LeftStick;
        RightStick ??= d.RightStick;
        Buttons ??= d.Buttons;

        LeftStick.XAxis = ValidateAxis(fileName, "LeftStick.XAxis", LeftStick.XAxis, d.LeftStick.XAxis);
        LeftStick.YAxis = ValidateAxis(fileName, "LeftStick.YAxis", LeftStick.YAxis, d.LeftStick.YAxis);
        RightStick.XAxis = ValidateAxis(fileName, "RightStick.XAxis", RightStick.XAxis, d.RightStick.XAxis);
        RightStick.YAxis = ValidateAxis(fileName, "RightStick.YAxis", RightStick.YAxis, d.RightStick.YAxis);

        if (!string.Equals(TriggerMode, "Buttons", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(TriggerMode, "ZAxisSplit", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Mapping {fileName}: TriggerMode '{TriggerMode}' is unrecognized - falling back to '{d.TriggerMode}'");
            TriggerMode = d.TriggerMode;
        }

        if (string.Equals(TriggerMode, "ZAxisSplit", StringComparison.OrdinalIgnoreCase))
            ZSplitAxis = ValidateAxis(fileName, "ZSplitAxis", ZSplitAxis, d.ZSplitAxis);

        Buttons.A = ValidateButton(fileName, "Buttons.A", Buttons.A, d.Buttons.A);
        Buttons.B = ValidateButton(fileName, "Buttons.B", Buttons.B, d.Buttons.B);
        Buttons.X = ValidateButton(fileName, "Buttons.X", Buttons.X, d.Buttons.X);
        Buttons.Y = ValidateButton(fileName, "Buttons.Y", Buttons.Y, d.Buttons.Y);
        Buttons.LeftShoulder = ValidateButton(fileName, "Buttons.LeftShoulder", Buttons.LeftShoulder, d.Buttons.LeftShoulder);
        Buttons.RightShoulder = ValidateButton(fileName, "Buttons.RightShoulder", Buttons.RightShoulder, d.Buttons.RightShoulder);
        Buttons.Back = ValidateButton(fileName, "Buttons.Back", Buttons.Back, d.Buttons.Back);
        Buttons.Start = ValidateButton(fileName, "Buttons.Start", Buttons.Start, d.Buttons.Start);
        Buttons.LeftThumb = ValidateButton(fileName, "Buttons.LeftThumb", Buttons.LeftThumb, d.Buttons.LeftThumb);
        Buttons.RightThumb = ValidateButton(fileName, "Buttons.RightThumb", Buttons.RightThumb, d.Buttons.RightThumb);
        Buttons.Guide = ValidateButton(fileName, "Buttons.Guide", Buttons.Guide, d.Buttons.Guide);

        LeftTriggerButton = ValidateButton(fileName, "LeftTriggerButton", LeftTriggerButton, d.LeftTriggerButton);
        RightTriggerButton = ValidateButton(fileName, "RightTriggerButton", RightTriggerButton, d.RightTriggerButton);
        DpadUpButton = ValidateButton(fileName, "DpadUpButton", DpadUpButton, d.DpadUpButton);
        DpadDownButton = ValidateButton(fileName, "DpadDownButton", DpadDownButton, d.DpadDownButton);
        DpadLeftButton = ValidateButton(fileName, "DpadLeftButton", DpadLeftButton, d.DpadLeftButton);
        DpadRightButton = ValidateButton(fileName, "DpadRightButton", DpadRightButton, d.DpadRightButton);

        if (PovIndex is < 0 or > 3)
        {
            Log.Warn($"Mapping {fileName}: PovIndex ({PovIndex}) is out of range 0..3 - using default {d.PovIndex}");
            PovIndex = d.PovIndex;
        }
    }

    private static int ValidateAxis(string file, string name, int val, int fallback)
    {
        if (val is < 0 or > 5)
        {
            Log.Warn($"Mapping {file}: {name} ({val}) is out of range 0..5 - using default {fallback}");
            return fallback;
        }
        return val;
    }

    private static int ValidateButton(string file, string name, int val, int fallback)
    {
        if (val is < -1 or >= 128)
        {
            Log.Warn($"Mapping {file}: {name} ({val}) is out of range -1..127 - using default {fallback}");
            return fallback;
        }
        return val;
    }

    // Starting point per known layout - always verify with --calibrate.
    private static MappingProfile Default(ushort vid, ushort pid)
    {
        // "Bluetooth Gamepad" (VID 038F / PID 1200): X/Y/Z/Rx, 16 buttons, no hat, no rumble.
        if (vid == 0x038F && pid == 0x1200)
            return new MappingProfile
            {
                LeftStick = new() { XAxis = 0, YAxis = 1 },
                RightStick = new() { XAxis = 2, YAxis = 3 },
                TriggerMode = "Buttons",
                LeftTriggerButton = 6,
                RightTriggerButton = 7,
                DpadFromPov = false,
                DpadUpButton = 12,
                DpadDownButton = 13,
                DpadLeftButton = 14,
                DpadRightButton = 15,
                Buttons = new()
                {
                    A = 0, B = 1, X = 2, Y = 3,
                    LeftShoulder = 4, RightShoulder = 5,
                    Back = 8, Start = 9, LeftThumb = 10, RightThumb = 11, Guide = -1,
                },
            };

        return new MappingProfile();   // DragonRise defaults (the field initializers)
    }
}
