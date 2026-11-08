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
    public async Task AnApprovedAbandonedStartIsReleasedWithoutClaimingNothingRan()
    {
        var starting = new TaskCompletionSource<(Process?, ElevatedRunKind)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ElevatedPowerShellRunner(startProcess: _ => starting.Task);
        using var stop = new CancellationTokenSource();
        bool reconciled = false;
        var run = runner.RunAsync("synthetic", TimeSpan.FromSeconds(1), stop.Token, _ => reconciled = true);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        // Unstarted managed Process is just a disposable stand-in; no native helper exists.
        starting.SetResult((new Process(), ElevatedRunKind.Exited));
        await runner.ReleaseAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(reconciled);
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
