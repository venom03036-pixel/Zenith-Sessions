using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task CoalescedQueuePresentationCannotReplayAnOlderProducerSnapshot() => OnUi(async () =>
    {
        await using var shell = await NativeTrialShellAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var row = shell.SelectedAccount!;
        var intent = new LaunchIntent(Guid.NewGuid(), row.Profile.Id, row.Profile.Revision,
            row.KnownUserId!.Value, 42, 0, DateTimeOffset.UtcNow, row.AuthorizationGeneration!.Value);
        var queuedOperation = new LaunchOperation(intent, LaunchState.Queued, LaunchProblem.None, DateTimeOffset.UtcNow);
        using var pendingRows = new PausedQueueObservation(queuedOperation, Environment.CurrentManagedThreadId);
        var queued = new LaunchQueueSnapshot(true, pendingRows);
        var preparing = new LaunchQueueSnapshot(true, [queuedOperation with { State = LaunchState.Preparing }]);
        var firstPresentationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task producer = Task.CompletedTask;
        bool armed = true;
        void Publish(LaunchQueueSnapshot value) => typeof(ShellViewModel)
            .GetMethod("QueueChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [value]);
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (!armed || args.PropertyName != nameof(ShellViewModel.QueuePauseLabel)) return;
            armed = false;
            // A resume notification arrives while the first presentation is running. The worker
            // then observes Preparing, but is paused before it can replace the pending UI value.
            Publish(queued);
            producer = Task.Run(() => Publish(preparing));
            Assert.True(pendingRows.Entered.Wait(TimeSpan.FromSeconds(5)), "The producer must reach its observation boundary.");
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => firstPresentationFinished.TrySetResult()));
        }
        shell.PropertyChanged += OnPropertyChanged;
        try
        {
            Publish(new(true, [queuedOperation]));
            await firstPresentationFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A UI scheduling tail must never feed its older pending snapshot back into the
            // producer history. Otherwise the next Failed event misses Preparing -> Failed.
            var latest = Assert.IsType<LaunchQueueSnapshot>(MetadataField(shell, "_latestQueue"));
            Assert.Equal(LaunchState.Preparing, Assert.Single(latest.Operations).State);
        }
        finally
        {
            shell.PropertyChanged -= OnPropertyChanged;
            pendingRows.Release.Set();
            await producer.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Assert.Same(preparing, MetadataField(shell, "_latestQueue"));
        Assert.Equal(LaunchState.Preparing, Assert.Single(shell.Launches).Operation.State);
        Assert.Null(shell.Launches[0].Operation.ProcessReceipt);
    });

    // Only the background producer's first read is held. The UI can read the same immutable
    // queued operation normally; the event barriers replace timing-dependent race attempts.
    private sealed class PausedQueueObservation(LaunchOperation operation, int uiThread) : IReadOnlyList<LaunchOperation>, IDisposable
    {
        private int _held;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int Count => 1;
        public LaunchOperation this[int index] => index == 0 ? operation : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<LaunchOperation> GetEnumerator()
        {
            if (Environment.CurrentManagedThreadId != uiThread && Interlocked.Exchange(ref _held, 1) == 0)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The observation barrier was not released.");
            }
            yield return operation;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }
}
