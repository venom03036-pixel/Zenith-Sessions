using System.Drawing;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Represents a consumer of screen capture frames (e.g. Color Watcher, OpenCV Matcher, AI Vision).
/// Watchers register interest with <see cref="Capture.CaptureScheduler"/> and receive frames;
/// they do not own capture infrastructure or run independent capture loops (Rule 5, Rule 6).
/// </summary>
public interface ICaptureSubscriber
{
    /// <summary>Unique subscriber identifier.</summary>
    Guid Id { get; }

    /// <summary>Desktop region of interest in virtual screen coordinates.</summary>
    Rectangle Region { get; }

    /// <summary>Requested capture interval for this subscriber.</summary>
    TimeSpan PollingInterval { get; }

    /// <summary>
    /// Invoked when a new frame is captured covering <see cref="Region"/>.
    /// <paramref name="frame"/> is owned by the caller/scheduler and is only valid for the duration of this callback.
    /// Consumers must not dispose or hold references to <paramref name="frame"/> past callback return (Rule 13).
    /// <paramref name="subRegion"/> indicates the relative coordinates of this subscriber's region within <paramref name="frame"/>.
    /// </summary>
    void OnFrameCaptured(CaptureFrame frame, Rectangle subRegion);
}
