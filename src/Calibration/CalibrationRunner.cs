using XBridge.Config;
using XBridge.Diagnostics;
using XBridge.Interop;
using Vortice.DirectInput;

namespace XBridge.Calibration;

// --calibrate: opens a pad non-exclusively and prints raw button/axis/POV indices as
// controls are pressed, so mapping-<vid>-<pid>.json can be filled in.
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

        IDirectInput8? di = null;
        IDirectInputDevice8? dev = null;
        ushort cvid = 0, cpid = 0;
        try
        {
            di = DInput.DirectInput8Create();

            var matches = new List<(DeviceInstance inst, ushort vid, ushort pid)>();
            foreach (var d in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
            {
                var (vid, pid) = DecodeVidPid(d.ProductGuid);
                if (cfg.Devices.Any(x => x.Matches(vid, pid)))
                    matches.Add((d, vid, pid));
            }

            if (matches.Count == 0)
            {
                Console.WriteLine("None of the config.json Devices are attached.");
                return;
            }

            for (int i = 0; i < matches.Count; i++)
                Console.WriteLine($"  [{i}] {matches[i].inst.ProductName}  VID {matches[i].vid:X4}/PID {matches[i].pid:X4}  " +
                                  $"InstanceGuid={matches[i].inst.InstanceGuid}");
            Console.WriteLine();

            int byGuid = wantGuid != Guid.Empty ? matches.FindIndex(m => m.inst.InstanceGuid == wantGuid) : -1;
            var pick = byGuid >= 0 ? matches[byGuid]
                     : wantIndex >= 0 && wantIndex < matches.Count ? matches[wantIndex]
                     : matches[0];
            var chosen = pick.inst;
            cvid = pick.vid;
            cpid = pick.pid;

            Console.WriteLine($"Using [{matches.FindIndex(m => m.inst.InstanceGuid == chosen.InstanceGuid)}] {chosen.ProductName}  {chosen.InstanceGuid}");
            Console.WriteLine("(pass --index N or --guid <instance-guid> to pick another)");
            Console.WriteLine();

            dev = di.CreateDevice(chosen.InstanceGuid);
            dev.SetDataFormat<RawJoystickState>();
            // Non-exclusive: you can keep another tool open to cross-check.
            dev.SetCooperativeLevel(ConsoleHwnd(), CooperativeLevel.NonExclusive | CooperativeLevel.Background);
            dev.Acquire();

            var caps = dev.Capabilities;
            Console.WriteLine($"Axes={caps.AxeCount}  Buttons={caps.ButtonCount}  POVs={caps.PovCount}  " +
                              $"ForceFeedback={(caps.Flags & DeviceFlags.ForceFeedback) != 0}");
            Console.WriteLine();
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
                try
                {
                    dev.Poll();
                    dev.GetCurrentJoystickState(ref state);
                }
                catch
                {
                    try { dev.Acquire(); } catch { }
                    Thread.Sleep(120);
                    continue;
                }

                for (int i = 0; i < state.Buttons.Length && i < prevButtons.Length; i++)
                {
                    if (state.Buttons[i] && !prevButtons[i])
                        Console.WriteLine($"  BUTTON  index={i,-2}  DOWN");
                    else if (!state.Buttons[i] && prevButtons[i])
                        Console.WriteLine($"  BUTTON  index={i,-2}  up");
                    prevButtons[i] = state.Buttons[i];
                }

                int pov = state.PointOfViewControllers.Length > 0 ? state.PointOfViewControllers[0] : -1;
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

                Thread.Sleep(8);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Calibration error: " + ex);
            Log.Error("calibration error", ex);
            return;
        }
        finally
        {
            try { dev?.Unacquire(); } catch { }
            try { dev?.Dispose(); } catch { }
            try { di?.Dispose(); } catch { }
        }

        Console.WriteLine(new string('-', 64));
        MappingProfile.LoadOrDefault(cvid, cpid);   // ensure the file exists
        Console.WriteLine($"Edit your mapping file: {MappingProfile.PathFor(cvid, cpid)}");
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

    private static (ushort vid, ushort pid) DecodeVidPid(Guid productGuid)
    {
        Span<byte> b = stackalloc byte[16];
        productGuid.TryWriteBytes(b);
        uint dword = BitConverter.ToUInt32(b);
        return ((ushort)(dword & 0xFFFF), (ushort)(dword >> 16));
    }

    private static IntPtr ConsoleHwnd()
    {
        var h = NativeMethods.GetConsoleWindow();
        return h != IntPtr.Zero ? h : NativeMethods.GetForegroundWindow();
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
