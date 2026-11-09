using System.Windows.Threading;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public async Task UiFixtureCannotFinishBeforeItsDispatcherShutdownCompletes()
    {
        var shutdownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseShutdown = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var run = OnUi(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.ShutdownStarted += (_, _) =>
            {
                shutdownEntered.TrySetResult();
                if (!releaseShutdown.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic dispatcher shutdown was not released.");
            };
            return Task.CompletedTask;
        });
        try
        {
            await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
        }
        finally { releaseShutdown.Set(); }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(dispatcher!.HasShutdownFinished);
        Assert.True(dispatcher.Thread.Join(TimeSpan.FromSeconds(5)), "The fixture's STA thread must retire before the next fixture starts.");
    }
}
