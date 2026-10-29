namespace XBridge.Input;

// Decoded controller state before Xbox 360 mapping.
internal sealed class JoystickState
{
    public int X { get; set; } = 32767;
    public int Y { get; set; } = 32767;
    public int Z { get; set; } = 32767;
    public int RotationX { get; set; } = 32767;
    public int RotationY { get; set; } = 32767;
    public int RotationZ { get; set; } = 32767;
    public int[] PointOfViewControllers { get; } = new int[4] { -1, -1, -1, -1 };
    public bool[] Buttons { get; } = new bool[128];
}
