using MacroSuite.App.Overlays;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Input;

public sealed class CoordinatePickOverlayOwnershipTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SetupFailureCancelsAndJoinsTheExactAlreadyArmedChild()
    {
        var release = new TaskCompletionSource<ScreenPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var task = CoordinatePickOverlay.RunPromptAsync(token =>
        {
            registration = token.Register(() => cancelled.TrySetResult()); return release.Task;
        }, () => throw new InvalidOperationException("Synthetic visual setup failure."), default);
        try
        {
            await cancelled.Task.WaitAsync(Deadline); Assert.False(task.IsCompleted);
        }
        finally { release.TrySetResult(null); }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(Deadline));
        Assert.Equal("Synthetic visual setup failure.", error.Message); registration.Dispose();
    }

    [Fact]
    public async Task CancellationDoesNotAbandonAnUncooperativeChildOrReturnItsLatePoint()
    {
        var release = new TaskCompletionSource<ScreenPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(); var visual = new Visual();
        var task = CoordinatePickOverlay.RunPromptAsync(_ => release.Task, () => visual, stop.Token);
        stop.Cancel();
        try { Assert.False(task.IsCompleted); Assert.Equal(0, visual.Disposals); }
        finally { release.SetResult(new(100, 200)); }
        Assert.Null(await task.WaitAsync(Deadline)); Assert.Equal(1, visual.Disposals);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackStillJoinsChildAndDisposesVisual()
    {
        var release = new TaskCompletionSource<ScreenPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visual = new Visual(); CancellationTokenRegistration registration = default;
        var task = CoordinatePickOverlay.RunPromptAsync(token =>
        {
            registration = token.Register(() => { callback.TrySetResult(); throw new InvalidOperationException("Synthetic cancellation failure."); });
            return release.Task;
        }, () => throw new InvalidOperationException("Synthetic setup failure."), default);
        try { await callback.Task.WaitAsync(Deadline); Assert.False(task.IsCompleted); }
        finally { release.SetResult(null); }
        await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => task.WaitAsync(Deadline)); registration.Dispose();
        Assert.Equal(0, visual.Disposals); // Show never returned a visual to transfer ownership.
    }

    [Fact]
    public async Task OrdinaryPointIsReturnedOnlyAfterTheVisualHasBeenDisposed()
    {
        var visual = new Visual();
        var result = await CoordinatePickOverlay.RunPromptAsync(_ => Task.FromResult<ScreenPoint?>(new(30, 40)), () => visual, default);
        Assert.Equal(new ScreenPoint(30, 40), result); Assert.Equal(1, visual.Disposals);
    }

    [Fact]
    public async Task VisualCleanupFailureIsAnExplicitCleanupFailure()
    {
        var visual = new Visual { Fail = true };
        await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => CoordinatePickOverlay.RunPromptAsync(
            _ => Task.FromResult<ScreenPoint?>(new(30, 40)), () => visual, default));
        Assert.Equal(1, visual.Disposals);
    }

    [Fact]
    public async Task AlreadyCancelledPromptDoesNotArmOrShowAnything()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); int armed = 0, shown = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CoordinatePickOverlay.RunPromptAsync(
            _ => { armed++; return Task.FromResult<ScreenPoint?>(null); }, () => { shown++; return new Visual(); }, stop.Token));
        Assert.Equal(0, armed); Assert.Equal(0, shown);
    }

    private sealed class Visual : IDisposable
    {
        public int Disposals; public bool Fail;
        public void Dispose() { Disposals++; if (Fail) throw new InvalidOperationException("Synthetic visual cleanup failure."); }
    }
}
