namespace MacroSuite.Core.Models;

/// <summary>
/// Identifies the underlying hardware/API source that acquired a <see cref="CaptureFrame"/>.
/// </summary>
public enum CaptureSource : byte
{
    /// <summary>Captured via Windows Graphics Capture or DXGI Desktop Duplication.</summary>
    Dxgi = 0,

    /// <summary>Captured via Win32 GDI BitBlt fallback.</summary>
    Gdi = 1
}
