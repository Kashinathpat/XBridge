using System.Text.Json;
using XBridge.Diagnostics;

namespace XBridge.Config;

// Physical-pad -> Xbox 360 layout, one profile shared by every session (identical hardware).
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

    public static string PathOnDisk => Path.Combine(AppConfig.ConfigDir, "mapping.json");

    public static MappingProfile LoadOrDefault()
    {
        try
        {
            if (File.Exists(PathOnDisk))
            {
                var p = JsonSerializer.Deserialize<MappingProfile>(File.ReadAllText(PathOnDisk), JsonOpts);
                if (p is not null)
                {
                    Log.Info($"Mapping profile loaded from {PathOnDisk}");
                    return p;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mapping load failed - using defaults", ex);
        }

        var def = new MappingProfile();
        def.Save();
        Log.Info($"Mapping profile created with defaults at {PathOnDisk} (run --calibrate to tune it)");
        return def;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDir);
            File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Error("Mapping save failed", ex);
        }
    }
}
