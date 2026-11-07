using Zenith.Sessions.App;
using Xunit;

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
}
