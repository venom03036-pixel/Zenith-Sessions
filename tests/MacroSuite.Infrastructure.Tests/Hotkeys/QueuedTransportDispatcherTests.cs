using System.Windows.Threading;
using FluentAssertions;
using MacroSuite.App.Common;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Hotkeys;

public sealed class QueuedTransportDispatcherTests
{
    [Fact]
    public void PriorityStop_InvalidatesQueuedPlayRecordScriptAndAutoClickerStarts()
        => OnDispatcher(async dispatcher =>
        {
            var queue = new QueuedTransportDispatcher(dispatcher);
            var log = new List<string>();
            Task[] pending = [queue.Enqueue(() => log.Add("play")), queue.Enqueue(() => log.Add("record")),
                queue.Enqueue(() => log.Add("script")), queue.Enqueue(() => log.Add("auto-clicker")),
                queue.InterruptAsync(() => log.Add("stop"))];
            await Task.WhenAll(pending);
            log.Should().Equal("stop");
            queue.PendingCount.Should().Be(0);
        });

    [Fact]
    public void LocalPanic_InvalidatesQueuedStarts_AndFreshRequestAfterwardCanRun()
        => OnDispatcher(async dispatcher =>
        {
            var queue = new QueuedTransportDispatcher(dispatcher);
            var log = new List<string>();
            Task old = queue.Enqueue(() => log.Add("stale"));
            queue.Invalidate(); // MainWindow's local ESC runs synchronously.
            log.Add("panic");
            await old;
            await queue.Enqueue(() => log.Add("fresh"));
            log.Should().Equal("panic", "fresh");
        });

    [Fact]
    public void StopDuringAwaitedScriptSwitch_PreventsDelayedRestartAndJoinsCallback()
        => OnDispatcher(async dispatcher =>
        {
            var queue = new QueuedTransportDispatcher(dispatcher);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var log = new List<string>();
            Task delayed = queue.EnqueueAsync(async isCurrent =>
            {
                started.TrySetResult();
                await resume.Task;
                if (isCurrent()) log.Add("restart");
            });
            await started.Task;
            await queue.InterruptAsync(() => log.Add("stop"));
            resume.TrySetResult();
            await delayed;
            log.Should().Equal("stop");
            queue.PendingCount.Should().Be(0);
        });

    [Fact]
    public void QueuedAsyncFailure_IsObservedAndRemovedFromPendingCallbacks()
        => OnDispatcher(async dispatcher =>
        {
            var queue = new QueuedTransportDispatcher(dispatcher);
            Task failure = queue.EnqueueAsync(() => Task.FromException(new InvalidOperationException("test failure")));
            await FluentActions.Awaiting(() => failure).Should().ThrowAsync<InvalidOperationException>();
            queue.PendingCount.Should().Be(0);
        });

    private static void OnDispatcher(Func<Dispatcher, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                Task work = body(dispatcher);
                _ = work.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
                Dispatcher.Run();
                work.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("dispatcher test must finish without a deadlock");
        failure.Should().BeNull();
    }
}
