using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Zenith.Sessions.App;

// Every packaged self-check stage shuts down the owners it creates through UseAsync instead of `await using`.
// An owner's DisposeAsync takes no token and joins the workers it tracks, so a worker that outlived the stage
// deadline would hold the stage, and the report, as well. A stalled owner fails the stage with a TimeoutException
// that keeps the stage's own failure (such as the deadline cancellation) as its cause, and is abandoned: its
// workers end with the process.
internal static class SelfCheckOwners
{
    // This bounds each asynchronous shutdown, not process startup, synchronous native work or report I/O.
    // Healthy nested owners can each consume a bound; after the first stalled owner, outer owners are only
    // asked to stop. The launcher's process deadline is a separate final containment boundary.
    internal static readonly TimeSpan ShutdownBound = TimeSpan.FromSeconds(2);
    private const string StalledOwnerKey = "Zenith.Sessions.SelfCheck.StalledOwner";

    public static Task UseAsync<TOwner>(TOwner owner, Func<TOwner, Task> work) where TOwner : IAsyncDisposable
        => UseAsync(owner, work, ShutdownBound);

    // Runs work with owner, then disposes owner and waits at most bound for it, whatever work did.
    internal static async Task UseAsync<TOwner>(TOwner owner, Func<TOwner, Task> work, TimeSpan bound, TimeProvider? clock = null) where TOwner : IAsyncDisposable
    {
        Exception? failure = null;
        try { await work(owner); }
        catch (Exception ex) { failure = ex; }
        // Once an owner inside this one has stalled, the stage has failed and this owner is only asked to stop:
        // nested owners do not each add a bound, and the first owner that stalled stays the stage's failure.
        bool innerStalled = failure is not null && StalledOwner(failure) is not null;
        Task shutdown;
        try { shutdown = owner.DisposeAsync().AsTask(); }
        catch when (innerStalled) { ExceptionDispatchInfo.Throw(failure!); throw; }
        if (innerStalled)
        {
            ObserveAbandoned(shutdown);
            ExceptionDispatchInfo.Throw(failure!);
        }
        using var timerStop = new System.Threading.CancellationTokenSource();
        var timer = Task.Delay(bound, clock ?? TimeProvider.System, timerStop.Token);
        // Make the decision once. Checking shutdown.IsCompleted in a WaitAsync exception filter loses the
        // stage's cause if shutdown completes between the timeout and the filter running.
        var completed = await Task.WhenAny(shutdown, timer);
        if (completed != shutdown)
        {
            ObserveAbandoned(shutdown);
            var type = owner.GetType();
            var timeout = new TimeoutException($"The {type.Name} did not shut down in time.", failure);
            timeout.Data[StalledOwnerKey] = type.FullName ?? type.Name;
            throw timeout;
        }
        timerStop.Cancel();
        await shutdown;
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    // Disposal has already requested cancellation. This self-check process cannot join a native call that
    // ignores it; observe a late fault without blocking the report or leaving an unobserved exception.
    private static void ObserveAbandoned(Task shutdown)
        => _ = shutdown.ContinueWith(static task => { _ = task.Exception; },
            System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // The type name of the owner whose shutdown this timeout reports, or null for any other failure. A static type
    // name only (Rule 10), so the report can locate the stalled shutdown at that owner's DisposeAsync.
    internal static string? StalledOwner(Exception ex)
        => ex is TimeoutException && ex.Data[StalledOwnerKey] is string owner ? owner : null;
}
