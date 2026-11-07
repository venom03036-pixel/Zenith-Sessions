namespace MacroSuite.Core.Models;

/// <summary>
/// Specifies the pixel memory layout of a <see cref="CaptureFrame"/>.
/// </summary>
public enum FramePixelFormat : byte
{
    /// <summary>32 bits per pixel: Blue, Green, Red, Alpha (8 bits each). Standard DXGI/WGC format.</summary>
    Bgra32 = 0,

    /// <summary>24 bits per pixel: Blue, Green, Red (8 bits each). Standard GDI DIB format.</summary>
    Bgr24 = 1,

    /// <summary>32 bits per pixel: Red, Green, Blue, Alpha (8 bits each).</summary>
    Rgba32 = 2
}
