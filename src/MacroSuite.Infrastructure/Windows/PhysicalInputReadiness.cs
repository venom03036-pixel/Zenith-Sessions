using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>Bounded observation only. A resume request waits for its initiating click/key release;
/// it never synthesizes releases for keys the user is physically holding.</summary>
public static class PhysicalInputReadiness
{
    public static Task<bool> WaitForNeutralAsync(TimeSpan timeout, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            long started = Stopwatch.GetTimestamp();
            do
            {
                ct.ThrowIfCancellationRequested();
                bool neutral = true;
                for (int key = 1; key < 255; key++)
                    if ((GetAsyncKeyState(key) & 0x8000) != 0) { neutral = false; break; }
                if (neutral) return true;
                await Task.Delay(25, ct).ConfigureAwait(false);
            } while (Stopwatch.GetElapsedTime(started) < timeout);
            return false;
        }, ct);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
}
