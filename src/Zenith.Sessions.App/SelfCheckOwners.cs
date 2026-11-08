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
    // The self-check's 15 s deadline plus this bound stays inside the 20 s that Check-ZenithSessionsManager.ps1
    // waits, so the report is still written in time. A stalled owner costs one bound: the owners around it are
    // not waited for as well (see below).
    internal static readonly TimeSpan ShutdownBound = TimeSpan.FromSeconds(2);
    private const string StalledOwnerKey = "Zenith.Sessions.SelfCheck.StalledOwner";

    public static Task UseAsync<TOwner>(TOwner owner, Func<TOwner, Task> work) where TOwner : IAsyncDisposable
        => UseAsync(owner, work, ShutdownBound);

    // Runs work with owner, then disposes owner and waits at most bound for it, whatever work did.
    internal static async Task UseAsync<TOwner>(TOwner owner, Func<TOwner, Task> work, TimeSpan bound) where TOwner : IAsyncDisposable
    {
        Exception? failure = null;
        try { await work(owner); }
        catch (Exception ex) { failure = ex; }
        // Once an owner inside this one has stalled, the stage has failed and this owner is only asked to stop:
        // nested owners do not each add a bound, and the first owner that stalled stays the stage's failure.
        bool innerStalled = failure is not null && StalledOwner(failure) is not null;
        var shutdown = owner.DisposeAsync().AsTask();
        try { await shutdown.WaitAsync(innerStalled ? TimeSpan.Zero : bound); }
        catch (TimeoutException) when (!shutdown.IsCompleted)
        {
            if (innerStalled) ExceptionDispatchInfo.Throw(failure!);
            var type = owner.GetType();
            var timeout = new TimeoutException($"The {type.Name} did not shut down in time.", failure);
            timeout.Data[StalledOwnerKey] = type.FullName ?? type.Name;
            throw timeout;
        }
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    // The type name of the owner whose shutdown this timeout reports, or null for any other failure. A static type
    // name only (Rule 10), so the report can locate the stalled shutdown at that owner's DisposeAsync.
    internal static string? StalledOwner(Exception ex)
        => ex is TimeoutException && ex.Data[StalledOwnerKey] is string owner ? owner : null;
}
