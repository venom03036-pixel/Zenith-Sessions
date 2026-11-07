using System.Drawing;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Service contract for acquiring screen frames.
/// Consumers depend only on this abstraction; DXGI vs GDI details are isolated behind it (Rule 3, Rule 5).
/// </summary>
public interface IScreenCaptureService : IDisposable
{
    /// <summary>The primary source currently active (Dxgi or Gdi).</summary>
    CaptureSource CurrentSource { get; }

    /// <summary>Whether this capture service is supported on the current system.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Captures a specified rectangular region of the desktop in virtual screen coordinates.
    /// Caller owns the returned <see cref="CaptureFrame"/> and is responsible for disposing it (Rule 13).
    /// </summary>
    Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default);

    /// <summary>
    /// Captures the entire virtual desktop spanning all active monitors.
    /// Caller owns the returned <see cref="CaptureFrame"/> and is responsible for disposing it (Rule 13).
    /// </summary>
    Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default);
}
