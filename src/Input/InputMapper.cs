using XBridge.Config;

namespace XBridge.Input;

internal struct XboxState : IEquatable<XboxState>
{
    public short LX, LY, RX, RY;
    public byte LT, RT;
    public bool A, B, X, Y, LB, RB, Back, Start, LS, RS, Guide;
    public bool DUp, DDown, DLeft, DRight;

    public readonly bool Equals(XboxState o) =>
        LX == o.LX && LY == o.LY && RX == o.RX && RY == o.RY &&
        LT == o.LT && RT == o.RT &&
        A == o.A && B == o.B && X == o.X && Y == o.Y &&
        LB == o.LB && RB == o.RB && Back == o.Back && Start == o.Start &&
        LS == o.LS && RS == o.RS && Guide == o.Guide &&
        DUp == o.DUp && DDown == o.DDown && DLeft == o.DLeft && DRight == o.DRight;
}

// JoystickState -> XboxState. Allocation-free on the hot path. One instance shared by every session.
internal sealed class InputMapper
{
    private readonly MappingProfile _m;
    private readonly double _axisMid;
    private readonly double _axisHalf;
    private readonly double _lDead;
    private readonly double _rDead;
    private readonly double _tDead;
    private readonly bool _zSplit;

    public InputMapper(MappingProfile map, AppConfig cfg)
    {
        _m = map;
        _axisMid = (map.AxisMin + map.AxisMax) / 2.0;
        _axisHalf = Math.Max(1.0, (map.AxisMax - map.AxisMin) / 2.0);
        _lDead = Math.Clamp(cfg.LeftStickDeadzone, 0, 0.9);
        _rDead = Math.Clamp(cfg.RightStickDeadzone, 0, 0.9);
        _tDead = Math.Clamp(cfg.TriggerDeadzone, 0, 0.9);
        _zSplit = string.Equals(map.TriggerMode, "ZAxisSplit", StringComparison.OrdinalIgnoreCase);
    }

    public void Map(JoystickState s, ref XboxState o)
    {
        bool[] btn = s.Buttons; // reused array owned by the JoystickState instance

        o.A = Get(btn, _m.Buttons.A);
        o.B = Get(btn, _m.Buttons.B);
        o.X = Get(btn, _m.Buttons.X);
        o.Y = Get(btn, _m.Buttons.Y);
        o.LB = Get(btn, _m.Buttons.LeftShoulder);
        o.RB = Get(btn, _m.Buttons.RightShoulder);
        o.Back = Get(btn, _m.Buttons.Back);
        o.Start = Get(btn, _m.Buttons.Start);
        o.LS = Get(btn, _m.Buttons.LeftThumb);
        o.RS = Get(btn, _m.Buttons.RightThumb);
        o.Guide = Get(btn, _m.Buttons.Guide);

        MapStick(s, _m.LeftStick, _lDead, out o.LX, out o.LY);
        MapStick(s, _m.RightStick, _rDead, out o.RX, out o.RY);

        if (_zSplit)
        {
            double v = Norm(AxisRaw(s, _m.ZSplitAxis)); // -1..1
            if (v < -_tDead) { o.LT = Scale(-v); o.RT = 0; }
            else if (v > _tDead) { o.RT = Scale(v); o.LT = 0; }
            else { o.LT = 0; o.RT = 0; }
        }
        else
        {
            o.LT = Get(btn, _m.LeftTriggerButton) ? (byte)255 : (byte)0;
            o.RT = Get(btn, _m.RightTriggerButton) ? (byte)255 : (byte)0;
        }

        if (_m.DpadFromPov)
        {
            int pov = -1;
            int idx = _m.PovIndex;
            int[] povs = s.PointOfViewControllers;
            if (povs is not null && idx >= 0 && idx < povs.Length) pov = povs[idx];
            SetDpad(ref o, pov);
        }
        else
        {
            o.DUp = Get(btn, _m.DpadUpButton);
            o.DDown = Get(btn, _m.DpadDownButton);
            o.DLeft = Get(btn, _m.DpadLeftButton);
            o.DRight = Get(btn, _m.DpadRightButton);
        }
    }

    private static bool Get(bool[] b, int i) => i >= 0 && i < b.Length && b[i];

    private int AxisRaw(JoystickState s, int idx) => idx switch
    {
        0 => s.X,
        1 => s.Y,
        2 => s.Z,
        3 => s.RotationX,
        4 => s.RotationY,
        5 => s.RotationZ,
        _ => (int)_axisMid,
    };

    private double Norm(int raw) => Math.Clamp((raw - _axisMid) / _axisHalf, -1.0, 1.0);

    private static byte Scale(double v01) => (byte)Math.Clamp(Math.Round(v01 * 255.0), 0, 255);

    private void MapStick(JoystickState s, MappingProfile.StickMap map, double dead, out short ox, out short oy)
    {
        double nx = Norm(AxisRaw(s, map.XAxis));
        double ny = Norm(AxisRaw(s, map.YAxis));

        if (map.InvertX) nx = -nx;
        if (map.InvertY) ny = -ny;
        ny = -ny; // DirectInput down-positive -> Xbox up-positive

        double mag = Math.Sqrt(nx * nx + ny * ny);
        if (mag <= dead || mag <= 1e-6)
        {
            ox = 0;
            oy = 0;
            return;
        }

        double scaled = Math.Min(1.0, (mag - dead) / (1.0 - dead));
        double f = scaled / mag;
        ox = ToShort(nx * f);
        oy = ToShort(ny * f);
    }

    private static short ToShort(double v) =>
        (short)Math.Clamp(Math.Round(v * 32767.0), -32768, 32767);

    private static void SetDpad(ref XboxState o, int pov)
    {
        o.DUp = o.DDown = o.DLeft = o.DRight = false;
        if (pov < 0 || pov > 36000) return; // centered / invalid
        o.DUp = pov >= 31500 || pov <= 4500;
        o.DRight = pov is >= 4500 and <= 13500;
        o.DDown = pov is >= 13500 and <= 22500;
        o.DLeft = pov is >= 22500 and <= 31500;
    }
}
