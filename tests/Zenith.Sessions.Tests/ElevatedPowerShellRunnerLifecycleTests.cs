using System.Diagnostics;
using Xunit;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Only a synthetic ShellExecute completion. No process is started and no UAC prompt is opened.
public sealed class ElevatedPowerShellRunnerLifecycleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ACancelledStartRemainsOwnedAndPublishesTheExactLateNoStartAnswer(int kind)
    {
        var starting = new TaskCompletionSource<(Process?, ElevatedRunKind)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ElevatedPowerShellRunner(startProcess: _ => { entered.SetResult(); return starting.Task; });
        var replies = new List<ElevatedRunOutcome>();
        using var stop = new CancellationTokenSource();
        var run = runner.RunAsync("synthetic", TimeSpan.FromSeconds(1), stop.Token, replies.Add);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var released = runner.ReleaseAsync(default);
        Assert.False(released.IsCompleted); Assert.Empty(replies);
        starting.SetResult((null, (ElevatedRunKind)kind));
        await released.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new ElevatedRunOutcome((ElevatedRunKind)kind), Assert.Single(replies));
    }

    [Fact]
    public async Task AnApprovedAbandonedStartIsRetainedUntilItsActualExit()
    {
        var starting = new TaskCompletionSource<(Process?, ElevatedRunKind)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ElevatedPowerShellRunner(startProcess: _ => starting.Task,
            waitForExit: (_, _) => { observing.TrySetResult(); return exited.Task; });
        using var stop = new CancellationTokenSource();
        var replies = new List<ElevatedRunOutcome>();
        var run = runner.RunAsync("synthetic", TimeSpan.FromSeconds(1), stop.Token, replies.Add);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        starting.SetResult((new Process(), ElevatedRunKind.Exited));
        await observing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var released = runner.ReleaseAsync(default); bool held = !released.IsCompleted;
        exited.SetResult(0); await released.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(held); Assert.Equal(new(ElevatedRunKind.Exited, 0), Assert.Single(replies));
    }

    [Fact]
    public async Task AReleaseFailureRemainsObservableByItsOwner()
    {
        var starting = new TaskCompletionSource<(Process?, ElevatedRunKind)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ElevatedPowerShellRunner(startProcess: _ => starting.Task);
        using var stop = new CancellationTokenSource();
        var run = runner.RunAsync("synthetic", TimeSpan.FromSeconds(1), stop.Token);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        starting.SetException(new InvalidOperationException("synthetic start failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ReleaseAsync(default));
    }
}
