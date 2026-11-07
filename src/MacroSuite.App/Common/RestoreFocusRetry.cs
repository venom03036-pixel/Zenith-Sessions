namespace MacroSuite.App.Common;

/// <summary>Bounded by the caller's cancellation budget; readiness notifications wake attempts
/// immediately, with a short fallback for native transitions that publish no WPF event.</summary>
internal static class RestoreFocusRetry
{
    internal static async Task<bool> RunAsync(Func<bool> canContinue, Func<bool> tryForeground,
        Func<CancellationToken, Task> waitForReadiness, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!canContinue()) return false;
            if (tryForeground()) return true;
            await waitForReadiness(ct);
        }
    }
}
