using System.Diagnostics;

namespace MacroSuite.Infrastructure.Tests.AI;

/// <summary>
/// Bounded wait for a live-test window to hold the foreground. Windows' foreground lock can
/// refuse SetForegroundWindow (for example while the user works in another app), so a live
/// input test retries the request until a deadline and then reports that it could not run,
/// instead of sending input that the expected-window safety rule would correctly reject.
/// </summary>
internal static class LiveForegroundWait
{
    /// <summary>
    /// Returns true as soon as <paramref name="isForeground"/> reports the window has the
    /// foreground; otherwise calls <paramref name="requestForeground"/> and checks again every
    /// <paramref name="retryInterval"/> until <paramref name="timeout"/> elapses, then returns false.
    /// </summary>
    public static async Task<bool> TryAcquireAsync(
        Func<bool> isForeground,
        Action requestForeground,
        TimeSpan timeout,
        TimeSpan retryInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isForeground);
        ArgumentNullException.ThrowIfNull(requestForeground);

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isForeground())
                return true;
            if (elapsed.Elapsed >= timeout)
                return false;

            requestForeground();
            await Task.Delay(retryInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
