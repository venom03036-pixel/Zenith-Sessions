using MacroSuite.App.Common;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Hotkeys;

public sealed class RestoreFocusRetryTests
{
    [Fact]
    public async Task ReadinessEvent_WakesRetry_WhenInitialNativeWindowWasNotReady()
    {
        var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool ready = false; int attempts = 0;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var restore = RestoreFocusRetry.RunAsync(() => true, () => { attempts++; return ready; }, ct => pulse.Task.WaitAsync(ct), lifetime.Token);
        Assert.Equal(1, attempts); Assert.False(restore.IsCompleted);
        ready = true; pulse.SetResult();
        Assert.True(await restore); Assert.Equal(2, attempts);
    }
    [Fact]
    public async Task ForegroundLostWhenMiniHides_RequiresFreshObservedAcceptance()
    {
        bool foreground = false; int attempts = 0;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.True(await RestoreFocusRetry.RunAsync(() => true, () => { attempts++; return foreground; }, _ => { foreground = true; return Task.CompletedTask; }, lifetime.Token));
        Assert.Equal(2, attempts);
    }
    [Fact]
    public async Task UserMinimizeOrClose_CancelsReadinessWaitWithoutAnotherFocusAttempt()
    {
        using var cancellation = new CancellationTokenSource(); int attempts = 0;
        var restore = RestoreFocusRetry.RunAsync(() => true, () => { attempts++; return false; }, ct => Task.Delay(Timeout.Infinite, ct), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        Assert.Equal(1, attempts);
    }
    [Fact]
    public async Task HiddenOrSupersededWindow_DoesNotRequestForeground()
    {
        int attempts = 0;
        Assert.False(await RestoreFocusRetry.RunAsync(() => false, () => { attempts++; return true; }, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(0, attempts);
    }
}
