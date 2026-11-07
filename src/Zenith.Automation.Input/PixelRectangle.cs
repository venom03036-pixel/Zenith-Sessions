namespace Zenith.Sessions.Core;

/// <summary>Physical virtual-desktop pixels, including negative monitor origins. Width/height are outer-window bounds.</summary>
public readonly record struct PixelRectangle(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0 && (long)X + Width <= int.MaxValue && (long)Y + Height <= int.MaxValue;
    public bool Contains(PixelRectangle other) => IsValid && other.IsValid && other.X >= X && other.Y >= Y
        && (long)other.X + other.Width <= (long)X + Width && (long)other.Y + other.Height <= (long)Y + Height;
}
