using System.Diagnostics;
using Xunit;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Process is a disposable managed stand-in; waits and start results are controlled, not native.
public sealed class ElevatedHelperExitOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrTimedOutApprovedHelperRetainsOwnershipUntilActualExit(bool timeout)
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = new List<ElevatedRunOutcome>();
        var runner = new ElevatedPowerShellRunner(startProcess: _ => Task.FromResult<(Process?, ElevatedRunKind)>((new Process(), ElevatedRunKind.Exited)),
            waitForExit: async (_, ct) => { entered.TrySetResult(); return await exit.Task.WaitAsync(ct); });
        using var stop = new CancellationTokenSource();
        var run = runner.RunAsync("synthetic", timeout ? TimeSpan.Zero : TimeSpan.FromSeconds(30), stop.Token, replies.Add);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (timeout) Assert.Equal(ElevatedRunKind.TimedOut, (await run).Kind);
        else { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        var released = runner.ReleaseAsync(default);
        bool held = !released.IsCompleted;
        exit.SetResult(0); await released.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(held, "A cancelled wait must retain the helper's actual lifetime.");
        Assert.Equal(new ElevatedRunOutcome(ElevatedRunKind.Exited, 0), Assert.Single(replies));
    }

    [Fact]
    public async Task AFailedWaitStillOwnsTheHelperEvenWithoutACompletionSubscriber()
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int waits = 0;
        var runner = new ElevatedPowerShellRunner(startProcess: _ => Task.FromResult<(Process?, ElevatedRunKind)>((new Process(), ElevatedRunKind.Exited)),
            waitForExit: (_, _) => ++waits == 1 ? Task.FromException<int>(new InvalidOperationException("synthetic wait failure")) : exit.Task);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("synthetic", TimeSpan.FromSeconds(30), default));
        var released = runner.ReleaseAsync(default); bool held = !released.IsCompleted;
        exit.SetResult(1); await released.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(held); Assert.Equal(2, waits);
    }

    [Fact]
    public async Task AnApprovedAbandonedPromptRemainsOwnedUntilItsHelperExits()
    {
        var starting = new TaskCompletionSource<(Process?, ElevatedRunKind)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = new List<ElevatedRunOutcome>();
        var runner = new ElevatedPowerShellRunner(startProcess: _ => starting.Task,
            waitForExit: async (_, ct) => { entered.TrySetResult(); return await exit.Task.WaitAsync(ct); });
        using var stop = new CancellationTokenSource();
        var run = runner.RunAsync("synthetic", TimeSpan.FromSeconds(30), stop.Token, replies.Add);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        starting.SetResult((new Process(), ElevatedRunKind.Exited));
        // The old implementation returns before asking for the process's exit.
        var released = runner.ReleaseAsync(default);
        await Task.WhenAny(entered.Task, released).WaitAsync(TimeSpan.FromSeconds(5));
        bool held = !released.IsCompleted;
        exit.SetResult(0); await released.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(held);
        Assert.Equal(new ElevatedRunOutcome(ElevatedRunKind.Exited, 0), Assert.Single(replies));
    }
}
