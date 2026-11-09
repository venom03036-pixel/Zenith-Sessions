using Zenith.Sessions.App;
using Xunit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Zenith.Sessions.Tests;

// The seam under the packaged composition stage. It never constructs a WPF Application:
// the test host may hold only one, and the shipped check owns it.
public sealed class PackagedLayoutCheckTests
{
    [Fact]
    public async Task StaStageReturnsOnTheDeadlineWhenItsBodyNeverCompletes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var never = new TaskCompletionSource();
        var run = PackagedLayoutCheck.RunOnStaAsync(() => new object(), _ => never.Task, _ => { }, deadline.Token);
        try
        {
            Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        finally { never.TrySetResult(); } // let the abandoned STA dispatcher retire instead of idling in the test host
    }

    [Fact]
    public async Task StaStageRunsCleanupOnItsOwnThreadAfterTheBodyCompletes()
    {
        var order = new List<string>(); int bodyThread = 0, cleanupThread = 0;
        await PackagedLayoutCheck.RunOnStaAsync(() => new object(),
            async _ =>
            {
                bodyThread = Environment.CurrentManagedThreadId;
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                await Task.Yield();
                order.Add("body");
            },
            _ => { cleanupThread = Environment.CurrentManagedThreadId; order.Add("cleanup"); },
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["body", "cleanup"], order);
        Assert.Equal(bodyThread, cleanupThread);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("body")]
    [InlineData("cleanup")]
    public async Task StaStageRetainsDispatcherWaitProtectionThroughEveryWpfPhase(string phase)
    {
        var contexts = new Dictionary<string, SynchronizationContext?>();
        Dispatcher? dispatcher = null;
        await PackagedLayoutCheck.RunOnStaAsync(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                contexts["create"] = SynchronizationContext.Current;
                var view = new Border { Child = new TextBlock { Text = "Synthetic layout" } };
                view.Measure(new Size(200, 40));
                return view;
            },
            async view =>
            {
                await Task.Yield();
                contexts["body"] = SynchronizationContext.Current;
                view.Arrange(new Rect(0, 0, 200, 40));
            },
            view =>
            {
                contexts["cleanup"] = SynchronizationContext.Current;
                view.Child = null;
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsType<DispatcherSynchronizationContext>(contexts[phase]);
        Assert.True(dispatcher!.HasShutdownFinished);
        Assert.True(dispatcher.Thread.Join(TimeSpan.FromSeconds(5)), "The owned STA thread must retire before this fixture finishes.");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("body")]
    [InlineData("cleanup")]
    public async Task StaStagePropagatesTheOriginalPhaseFailureAfterRetiringItsDispatcher(string phase)
    {
        var expected = new InvalidOperationException("Synthetic STA phase failure");
        Dispatcher? dispatcher = null;
        bool cleanupRan = false;
        var run = PackagedLayoutCheck.RunOnStaAsync(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                if (phase == "create") throw expected;
                return new object();
            },
            async _ =>
            {
                await Task.Yield();
                if (phase == "body") throw expected;
            },
            _ =>
            {
                cleanupRan = true;
                if (phase == "cleanup") throw expected;
            }, CancellationToken.None);

        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.Equal(phase != "create", cleanupRan);
        Assert.True(dispatcher!.HasShutdownFinished);
        Assert.True(dispatcher.Thread.Join(TimeSpan.FromSeconds(5)), "A failing phase must still retire its owned STA thread.");
    }

    [Fact]
    public async Task StaStageCannotReportCompletionWhileItsCleanupIsStillRunning()
    {
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var run = PackagedLayoutCheck.RunOnStaAsync(() => new object(),
            _ => { dispatcher = Dispatcher.CurrentDispatcher; return Task.CompletedTask; },
            _ =>
            {
                cleanupEntered.TrySetResult();
                if (!releaseCleanup.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic cleanup was not released.");
            }, CancellationToken.None);
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
        }
        finally { releaseCleanup.Set(); }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(dispatcher!.HasShutdownFinished);
        Assert.True(dispatcher.Thread.Join(TimeSpan.FromSeconds(5)));
    }
}
