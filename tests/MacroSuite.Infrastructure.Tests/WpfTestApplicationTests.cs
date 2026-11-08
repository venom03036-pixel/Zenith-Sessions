using FluentAssertions;
using Xunit;

namespace MacroSuite.Infrastructure.Tests;

/// <summary>
/// The shared test dispatcher runs every WPF test body. A body that overruns its
/// time limit must not keep running beside the tests that follow it.
/// </summary>
public sealed class WpfTestApplicationTests
{
    [Fact]
    public async Task OverrunningBody_HoldsItsTestUntilItStops_SoNoLaterTestRunsBesideIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool bodyStopped = false;
        Task run = WpfTestApplication.RunAsync(async () =>
        {
            try { entered.TrySetResult(); await release.Task; }
            finally { bodyStopped = true; }
        }, TimeSpan.FromMilliseconds(50), WpfTestApplication.LivenessBound);
        try
        {
            await entered.Task.WaitAsync(WpfTestApplication.LivenessBound);
            await Task.Delay(500); // Ten times the 50 ms limit given to this body.
            run.IsCompleted.Should().BeFalse("the overrunning body still occupies the shared dispatcher");
        }
        finally { release.TrySetResult(); }

        var error = await Assert.ThrowsAsync<TimeoutException>(() => run);
        bodyStopped.Should().BeTrue("the timeout is reported only after the body has stopped");
        error.Message.Should().Contain(nameof(OverrunningBody_HoldsItsTestUntilItStops_SoNoLaterTestRunsBesideIt))
            .And.Contain("no later test ran beside it");
    }

    [Fact]
    public async Task BodyStillRunningAfterItsDrain_IsNamed_AndLaterBodiesAreRefusedUntilItEnds()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool laterRan = false;
        try
        {
            var stuck = await Assert.ThrowsAsync<TimeoutException>(() => WpfTestApplication.RunAsync(
                () => release.Task, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), "StuckFixtureBody"));
            stuck.Message.Should().Contain("StuckFixtureBody").And.Contain("still running");

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => WpfTestApplication.RunAsync(() =>
            {
                laterRan = true;
                return Task.CompletedTask;
            }));
            refused.Message.Should().Contain("StuckFixtureBody");
            laterRan.Should().BeFalse("a later body must not start beside a body that is still running");
        }
        finally
        {
            release.TrySetResult();
            await WpfTestApplication.WhenAbandonedBodyEnds().WaitAsync(WpfTestApplication.LivenessBound);
        }

        await WpfTestApplication.RunAsync(() =>
        {
            laterRan = true;
            return Task.CompletedTask;
        });
        laterRan.Should().BeTrue("the shared dispatcher is usable again once the stuck body has ended");
    }

    /// <summary>
    /// Rule 12: the hang guard is a timer, so a body that finishes must cancel it. This assembly runs
    /// its tests one at a time, so the process-wide active-timer count moves only with these bodies
    /// (and with earlier timers expiring, which lowers it). Each body is counted once.
    /// </summary>
    [Fact]
    public async Task FinishedBody_LeavesNoHangGuardTimerPending()
    {
        const int bodies = 50;
        await WpfTestApplication.RunAsync(() => Task.CompletedTask); // Starts the shared dispatcher if this test is first.
        long before = Timer.ActiveCount;
        for (int i = 0; i < bodies; i++)
            await WpfTestApplication.RunAsync(() => Task.CompletedTask, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        long pending = Timer.ActiveCount - before;
        pending.Should().BeLessThan(bodies / 2,
            $"a finished body must cancel its hang-guard timer, but {pending} of {bodies} bodies left one pending");
    }

    [Fact]
    public async Task BodyThatStopsDuringItsDrain_LeavesNoDrainTimerPending()
    {
        const int bodies = 10;
        await WpfTestApplication.RunAsync(() => Task.CompletedTask);
        long before = Timer.ActiveCount;
        for (int i = 0; i < bodies; i++)
        {
            var overrun = await Assert.ThrowsAsync<TimeoutException>(() => WpfTestApplication.RunAsync(
                () => Task.Delay(150), TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(2))); // Fifteen times the limit.
            overrun.Message.Should().Contain("no later test ran beside it");
        }
        long pending = Timer.ActiveCount - before;
        pending.Should().BeLessThan(bodies / 2,
            $"a body that stops during its drain must cancel the drain timer, but {pending} of {bodies} bodies left one pending");
    }
}
