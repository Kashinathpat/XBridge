using HidSharp;
using XBridge.Config;
using XBridge.Core;
using XBridge.Diagnostics;
using XBridge.Input;
using XBridge.Interop;

namespace XBridge.Calibration;

// --calibrate: reads a pad's raw HID reports through the same parser the app uses and prints
// button/axis/POV indices as controls are pressed, so mapping-<vid>-<pid>.json can be filled in.
internal static class CalibrationRunner
{
    private static volatile bool _stop;
    public static void Stop() => _stop = true;

    public static void Run(string[] args, AppConfig cfg)
    {
        Log.EnableConsoleMirror();
        Console.WriteLine("=== XBridge : calibration ===");
        Console.WriteLine();

        int wantIndex = ArgInt(args, "--index", -1);
        Guid wantGuid = ArgGuid(args, "--guid");

        var units = DevicePairer.Enumerate(cfg.Devices).ToList();
        if (units.Count == 0)
        {
            Console.WriteLine("None of the config.json Devices are attached.");
            return;
        }

        for (int i = 0; i < units.Count; i++)
            Console.WriteLine($"  [{i}] {units[i].ProductName.Trim()}  VID {units[i].Vid:X4}/PID {units[i].Pid:X4}  " +
                              $"InstanceGuid={units[i].InstanceGuid}");
        Console.WriteLine();

        int byGuid = wantGuid != Guid.Empty ? units.FindIndex(u => u.InstanceGuid == wantGuid) : -1;
        int pick = byGuid >= 0 ? byGuid : wantIndex >= 0 && wantIndex < units.Count ? wantIndex : 0;
        var unit = units[pick];
        Console.WriteLine($"Using [{pick}] {unit.ProductName.Trim()}  {unit.InstanceGuid}");
        Console.WriteLine("(pass --index N or --guid <instance-guid> to pick another)");
        Console.WriteLine();

        try
        {
            var dev = DeviceList.Local.GetHidDevices().FirstOrDefault(
                d => string.Equals(d.DevicePath, unit.HidDevicePath, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("the pad's HID device disappeared");
            using var parser = new HidInputParser(dev.DevicePath);
            using var stream = dev.Open();
            stream.ReadTimeout = 250;   // so Ctrl+C is noticed on a pad that only sends on change
            var buf = new byte[Math.Max(parser.ReportLength, dev.GetMaxInputReportLength())];

            Console.WriteLine("Press controls one at a time and note the indices printed.");
            Console.WriteLine("Suggested order: A B X Y, LB RB, LT RT, Back Start, L3 R3, D-pad, then sweep both sticks.");
            Console.WriteLine("Press Ctrl+C (or close this window) when done.");
            Console.WriteLine(new string('-', 64));

            var state = new JoystickState();
            bool[] prevButtons = new bool[128];
            int prevPov = int.MinValue;
            int[] prevAxes = { int.MinValue, 0, 0, 0, 0, 0 };
            DateTime lastAxis = DateTime.MinValue;

            while (!_stop)
            {
                try { stream.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { continue; }
                parser.Parse(buf, state);

                for (int i = 0; i < state.Buttons.Length && i < prevButtons.Length; i++)
                {
                    if (state.Buttons[i] && !prevButtons[i])
                        Console.WriteLine($"  BUTTON  index={i,-2}  DOWN");
                    else if (!state.Buttons[i] && prevButtons[i])
                        Console.WriteLine($"  BUTTON  index={i,-2}  up");
                    prevButtons[i] = state.Buttons[i];
                }

                int pov = state.PointOfViewControllers[0];
                if (pov != prevPov)
                {
                    Console.WriteLine($"  POV[0]  value={pov}   ({DescribePov(pov)})");
                    prevPov = pov;
                }

                int[] axes = { state.X, state.Y, state.Z, state.RotationX, state.RotationY, state.RotationZ };
                bool moved = false;
                for (int i = 0; i < 6; i++)
                    if (prevAxes[i] == int.MinValue || Math.Abs(axes[i] - prevAxes[i]) > 1200) moved = true;
                if (moved && (DateTime.Now - lastAxis).TotalMilliseconds > 120)
                {
                    Console.WriteLine($"  AXES  X={axes[0],6} Y={axes[1],6} Z={axes[2],6} " +
                                      $"Rx={axes[3],6} Ry={axes[4],6} Rz={axes[5],6}");
                    Array.Copy(axes, prevAxes, 6);
                    lastAxis = DateTime.Now;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Calibration error: " + ex.Message);
            Log.Error("calibration error", ex);
            return;
        }

        Console.WriteLine(new string('-', 64));
        MappingProfile.LoadOrDefault(unit.Vid, unit.Pid);   // ensure the file exists
        Console.WriteLine($"Edit your mapping file: {MappingProfile.PathFor(unit.Vid, unit.Pid)}");
        Console.WriteLine("Axis index convention: 0=X 1=Y 2=Z 3=RotationX 4=RotationY 5=RotationZ");
        Console.WriteLine("Set button numbers under \"Buttons\", stick axes under LeftStick/RightStick,");
        Console.WriteLine("and TriggerMode (\"Buttons\" or \"ZAxisSplit\"). Restart the app to apply.");
    }

    private static string DescribePov(int pov)
    {
        if (pov < 0 || pov > 36000) return "centered";
        return pov switch
        {
            <= 4500 or >= 31500 => "up-ish",
            <= 13500 => "right-ish",
            <= 22500 => "down-ish",
            _ => "left-ish",
        };
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var v))
                return v;
        return fallback;
    }

    private static Guid ArgGuid(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(args[i + 1], out var g))
                return g;
        return Guid.Empty;
    }
}
