using MacroSuite.Core.Abstractions;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>
/// Requests a 1 ms Windows multimedia timer period so Sleep(1) is actually ~1 ms
/// during high-rate camera playback (Rule 3).
/// </summary>
public sealed class Win32TimerResolution : ITimerResolution
{
    public IDisposable Request(int periodMs = 1)
        => new PeriodScope((uint)Math.Clamp(periodMs, 1, 15));

    private sealed class PeriodScope : IDisposable
    {
        private readonly uint _period;
        private bool _disposed;

        public PeriodScope(uint period)
        {
            _period = period;
            NativeMethods.TimeBeginPeriod(_period);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            NativeMethods.TimeEndPeriod(_period);
        }
    }
}
