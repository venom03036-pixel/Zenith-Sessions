using System.Drawing;
using MacroSuite.Core.Coordinates;

namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Physical virtual-desktop geometry. Win32 details stay in Infrastructure (Rule 3).
/// </summary>
public interface IVirtualDesktopGeometry
{
    Rectangle VirtualDesktopBounds { get; }

    IReadOnlyList<MonitorDescriptor> Monitors { get; }
}
