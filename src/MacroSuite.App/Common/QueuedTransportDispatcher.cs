using System.Diagnostics;
using System.Windows.Threading;

namespace MacroSuite.App.Common;

/// <summary>Invalidates old queued starts when Stop/Panic overtakes the dispatcher queue.</summary>
public sealed class QueuedTransportDispatcher(Dispatcher dispatcher)
{
    private readonly object _sync = new();
    private readonly HashSet<Task> _pending = [];
    private long _generation;

    public int PendingCount { get { lock (_sync) return _pending.Count; } }
    public void Invalidate() => Interlocked.Increment(ref _generation);

    public Task Enqueue(Action action) => EnqueueAsync(_ => { action(); return Task.CompletedTask; });
    public Task EnqueueAsync(Func<Task> action) => EnqueueAsync(_ => action());

    /// <summary>The validity check also guards a delayed switch after an awaited stop.</summary>
    public Task EnqueueAsync(Func<Func<bool>, Task> action)
    {
        long generation = Volatile.Read(ref _generation);
        bool IsCurrent() => generation == Volatile.Read(ref _generation);
        Task task = dispatcher.InvokeAsync(() => IsCurrent() ? action(IsCurrent) : Task.CompletedTask,
            DispatcherPriority.Normal).Task.Unwrap();
        return Track(task);
    }

    public Task InterruptAsync(Action action) => InterruptAsync(() => { action(); return Task.CompletedTask; });

    public Task InterruptAsync(Func<Task> action)
    {
        // Invalidate at arrival and execution: no older request can start either
        // before this stop reaches the dispatcher or immediately behind it.
        Invalidate();
        Task task = dispatcher.InvokeAsync(() => { Invalidate(); return action(); }, DispatcherPriority.Send).Task.Unwrap();
        return Track(task);
    }

    private Task Track(Task task)
    {
        lock (_sync) _pending.Add(task);
        // All async callbacks stay tracked until completion, and faults are observed.
        // The dispatcher remains free to execute a priority Stop while a run is active.
        _ = task.ContinueWith(completed =>
        {
            lock (_sync) _pending.Remove(completed);
            if (completed.IsFaulted) Trace.TraceError($"Transport callback failed: {completed.Exception}");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
}
