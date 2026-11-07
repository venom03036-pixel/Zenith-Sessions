using System.Runtime.InteropServices;
using MacroSuite.Core.Abstractions;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>
/// Queries Windows display settings to determine current and maximum supported
/// monitor refresh rates (Rule 3).
/// </summary>
public sealed class WindowsDisplayInfoService : IDisplayInfoService
{
    public const int DefaultFallbackRate = 60;

    public int GetPrimaryMonitorRefreshRate()
    {
        try
        {
            var dm = new NativeMethods.DEVMODEW { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODEW>() };
            if (NativeMethods.EnumDisplaySettingsW(null, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 0)
            {
                return (int)dm.dmDisplayFrequency;
            }
        }
        catch
        {
            // Graceful fallback on non-Windows / restricted environments
        }

        return DefaultFallbackRate;
    }

    public int GetMaxMonitorRefreshRate()
    {
        int maxRate = DefaultFallbackRate;

        try
        {
            // 1. Check primary monitor current settings
            int primaryCurrent = GetPrimaryMonitorRefreshRate();
            if (primaryCurrent > maxRate) maxRate = primaryCurrent;

            // 2. Check primary monitor supported display modes
            var dm = new NativeMethods.DEVMODEW { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODEW>() };
            for (int mode = 0; NativeMethods.EnumDisplaySettingsW(null, mode, ref dm); mode++)
            {
                if (dm.dmDisplayFrequency > maxRate)
                {
                    maxRate = (int)dm.dmDisplayFrequency;
                }
            }

            // 3. Enumerate all attached display devices (for multi-monitor setups)
            var displayDevice = new NativeMethods.DISPLAY_DEVICEW { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICEW>() };
            for (uint i = 0; NativeMethods.EnumDisplayDevicesW(null, i, ref displayDevice, 0); i++)
            {
                if ((displayDevice.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                {
                    var monMode = new NativeMethods.DEVMODEW { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODEW>() };
                    if (NativeMethods.EnumDisplaySettingsW(displayDevice.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref monMode))
                    {
                        if (monMode.dmDisplayFrequency > maxRate)
                        {
                            maxRate = (int)monMode.dmDisplayFrequency;
                        }
                    }

                    for (int m = 0; NativeMethods.EnumDisplaySettingsW(displayDevice.DeviceName, m, ref monMode); m++)
                    {
                        if (monMode.dmDisplayFrequency > maxRate)
                        {
                            maxRate = (int)monMode.dmDisplayFrequency;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback gracefully
        }

        // Clamp to valid range (60 - 500 Hz)
        return Math.Clamp(maxRate, 60, 500);
    }
}
