using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Input;

public enum HostInputOperationKind { Playback, Recording, AutoClicker, Picker, CaptureTest, Companion }

public sealed record HostInputOperationSnapshot(Guid? OperationId, HostInputOperationKind? Kind,
    long StopGeneration, bool IsBusy, bool IsQuarantined, bool IsClosed, bool IsRecovering);

public sealed class HostInputOperationCleanupException : InvalidOperationException
{
    public HostInputOperationCleanupException()
        : base("Input cleanup is incomplete. New activities are blocked until the owning activity is recovered.") { }
}

/// <summary>Additive backend capability; cleanup always carries the captured owner.</summary>
public interface IHostInputOperationAwarePlayer
{
    IDisposable AttachHostOperation(HostInputOperationLease lease);
    void ReleaseHostOperationInputs(HostInputOperationLease lease);
}

/// <summary>An interactive child receives its parent explicitly, never from ambient current state.</summary>
public interface IHostInputOperationBoundPicker : ILocationPicker
{
    Task<ScreenPoint?> ArmAndWaitAsync(HostInputOperationLease parent, CancellationToken ct = default);
    void Cancel(HostInputOperationLease parent);
}

/// <summary>One nonqueued desktop activity. This is ownership, not target delivery or license authority.</summary>
public sealed class HostInputOperationArbiter
{
    private readonly object _gate = new();
    private HostInputOperationLease? _current;
    private long _generation;
    private int _closed, _quarantined;
    private Task<bool>? _recovery;
    private HostInputOperationLease? _recoveryOwner;

    public long StopGeneration => Volatile.Read(ref _generation);
    public HostInputOperationSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new(_current?.OperationId, _current?.Kind, StopGeneration, _current is not null,
                    _quarantined != 0, _closed != 0, _recovery is { IsCompleted: false });
        }
    }

    public bool Owns(HostInputOperationLease lease) => ReferenceEquals(Volatile.Read(ref _current), lease);
    internal bool Allows(HostInputOperationLease lease) => Owns(lease)
        && Volatile.Read(ref _closed) == 0 && Volatile.Read(ref _quarantined) == 0 && StopGeneration == lease.Generation;

    public bool TryAcquire(HostInputOperationKind kind, out HostInputOperationLease? lease, out string reason)
        => TryAcquire(kind, StopGeneration, out lease, out reason);

    public bool TryAcquire(HostInputOperationKind kind, long expectedGeneration,
        out HostInputOperationLease? lease, out string reason)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        lock (_gate)
        {
            lease = null;
            if (_closed != 0) { reason = "Input activity is closing. New activities cannot start."; return false; }
            if (_quarantined != 0) { reason = "Previous input cleanup is incomplete. Recover it before starting another activity."; return false; }
            if (_current is not null) { reason = "Another input activity is starting, running, paused or finishing. Stop it first."; return false; }
            if (expectedGeneration != StopGeneration) { reason = "This input request was stopped. Start it again deliberately."; return false; }
            lease = new(this, kind, expectedGeneration);
            Volatile.Write(ref _current, lease);
            reason = string.Empty;
        }
        // A hook may fence the generation between the in-lock check and publication.
        // The reservation remains owned, but cannot continue; its caller must finalize it.
        if (lease.Generation != StopGeneration || Volatile.Read(ref _closed) != 0) lease.SignalStop();
        return true;
    }

    /// <summary>Hook-safe revocation: no user callbacks, native release, UI work or waits here.</summary>
    public void SignalStop()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Read(ref _current)?.SignalStop();
    }

    public void Close()
    {
        Volatile.Write(ref _closed, 1);
        SignalStop();
    }

    /// <summary>Waits only the observed incarnation; a later operation never extends this wait.</summary>
    public Task WaitForRetirementAsync(Guid operationId, CancellationToken ct = default)
    {
        Task completion;
        lock (_gate) completion = _current?.OperationId == operationId ? _current.Completion : Task.CompletedTask;
        return completion.WaitAsync(ct);
    }

    internal void Retire(HostInputOperationLease lease, bool clean)
    {
        lock (_gate)
        {
            if (!Owns(lease)) return;
            if (clean) Volatile.Write(ref _current, null);
            else Volatile.Write(ref _quarantined, 1);
        }
    }

    /// <summary>
    /// Explicit recovery of a retired quarantined owner. The callback must verify all that owner's
    /// resources, not merely clear shared input. Cancellation never abandons the actual callback.
    /// </summary>
    public Task<bool> TryRecoverAsync(Func<CancellationToken, Task> cleanup, CancellationToken ct = default)
        => TryRecoverCoreAsync(null, cleanup, ct);

    /// <summary>Recover only the captured owner, even if another operation has since been quarantined.</summary>
    public Task<bool> TryRecoverAsync(HostInputOperationLease owner, Func<CancellationToken, Task> cleanup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return TryRecoverCoreAsync(owner, cleanup, ct);
    }

    private Task<bool> TryRecoverCoreAsync(HostInputOperationLease? owner, Func<CancellationToken, Task> cleanup, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (ct.IsCancellationRequested) return Task.FromCanceled<bool>(ct);
        TaskCompletionSource? start = null;
        Task<bool> result;
        lock (_gate)
        {
            if (owner is not null && !Owns(owner)) return Task.FromResult(false);
            if (_recovery is { IsCompleted: false })
                return owner is null || ReferenceEquals(owner, _recoveryOwner) ? _recovery.WaitAsync(ct) : Task.FromResult(false);
            if (_current is not { IsRetired: true } lease || !lease.Completion.IsCompletedSuccessfully || _quarantined == 0)
                return Task.FromResult(false);
            long generation = StopGeneration;
            start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _recoveryOwner = lease;
            var ready = start.Task;
            result = _recovery = Task.Run(async () =>
            {
                await ready.ConfigureAwait(false);
                try { ct.ThrowIfCancellationRequested(); await cleanup(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
                // Other errors propagate, with quarantine retained. A cancelled caller must
                // not turn an ignored-token late success into permission for another activity.
                lock (_gate)
                {
                    if (ct.IsCancellationRequested || generation != StopGeneration || !Owns(lease)) return false;
                    Volatile.Write(ref _quarantined, 0);
                    Volatile.Write(ref _current, null);
                    return true; // Commit point: no later cancellation check can contradict it.
                }
            });
        }
        start!.TrySetResult(); // Actual task was published before callback execution became possible.
        return result;
    }

    /// <summary>Close admission before shutdown drain. A remaining quarantine is an error, not idle.</summary>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        while (true)
        {
            HostInputOperationLease? lease; Task<bool>? recovery;
            lock (_gate) { lease = _current; recovery = _recovery is { IsCompleted: false } ? _recovery : null; }
            if (recovery is not null) { await recovery.WaitAsync(ct).ConfigureAwait(false); continue; }
            if (lease is null) return;
            await lease.Completion.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
                if (Owns(lease) && _quarantined != 0 && _recovery is not { IsCompleted: false })
                    throw new HostInputOperationCleanupException();
        }
    }
}

/// <summary>
/// Opaque operation capability. Complete only after the actual producer and cleanup have ended.
/// StopToken callbacks must only signal old owners; never wait for this lease's Completion.
/// </summary>
public sealed class HostInputOperationLease
{
    private const int Stopped = 1, Completing = 2, Retired = 4;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private readonly TaskCompletionSource<Task> _cancellationPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _finishGate = new();
    private Task? _finish;
    private int _flags;
    public HostInputOperationArbiter Arbiter { get; }
    public HostInputOperationKind Kind { get; }
    public Guid OperationId { get; } = Guid.NewGuid();
    public long Generation { get; }
    public bool IsCurrent => Arbiter.Owns(this);
    public bool OwnsCleanup => Arbiter.Owns(this);
    public bool IsRetired => (Volatile.Read(ref _flags) & Retired) != 0;
    public bool CanContinue => Volatile.Read(ref _flags) == 0 && Arbiter.Allows(this);
    public CancellationToken StopToken => _token;
    public Task Completion => _completed.Task;

    internal HostInputOperationLease(HostInputOperationArbiter arbiter, HostInputOperationKind kind, long generation)
    { Arbiter = arbiter; Kind = kind; Generation = generation; _token = _stop.Token; }

    /// <summary>Stops this captured incarnation only, never a replacement operation.</summary>
    public void SignalStop()
    {
        int flags;
        do
        {
            flags = Volatile.Read(ref _flags);
            if ((flags & (Stopped | Completing)) != 0) return;
        } while (Interlocked.CompareExchange(ref _flags, flags | Stopped, flags) != flags);
        try { _cancellationPublished.TrySetResult(_stop.CancelAsync()); }
        catch (Exception error) { _cancellationPublished.TrySetException(error); }
    }

    public Task CompleteAsync(bool cleanupSucceeded)
    {
        lock (_finishGate)
        {
            if (_finish is not null) return _finish;
            int previous = Interlocked.Or(ref _flags, Completing);
            return _finish = FinishAsync(cleanupSucceeded, (previous & Stopped) != 0);
        }
    }

    private async Task FinishAsync(bool clean, bool joinStop)
    {
        await Task.Yield(); // Publish the actual finalization task before any continuation can reenter.
        Exception? stopError = null;
        if (joinStop)
            try { await (await _cancellationPublished.Task.ConfigureAwait(false)).ConfigureAwait(false); }
            catch (Exception error) { clean = false; stopError = error; }
        Interlocked.Or(ref _flags, Retired);
        Arbiter.Retire(this, clean);
        _stop.Dispose();
        _completed.TrySetResult();
        if (stopError is not null) throw new InvalidOperationException("Input stop callbacks failed. Cleanup remains quarantined.", stopError);
    }
}
