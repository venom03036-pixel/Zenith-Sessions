namespace Zenith.Sessions.Windows;

/// <summary>
/// The kind of saved-session operation <see cref="RobloxSessionLinkService"/> owns, as a profile removal reports the one it
/// stopped. Different callers start different kinds (a share-link check or a launch's native preparation, for example, is
/// not started by whoever signs accounts in), so a caller cancels its own command only for a kind it starts.
/// </summary>
public enum SavedSessionOperationKind
{
    None,
    AddAccount,
    SignIn,
    SavedSignInCheck,
    HandoffInspection,
    ShareLinkCheck,
    NativeLaunchPreparation,
}

public sealed partial class RobloxSessionLinkService
{
    // Profiles a caller is removing, with how many removals hold each one. Guarded by _state.
    private readonly Dictionary<Guid, int> _removing = [];

    /// <summary>
    /// For a caller that is about to remove these profiles. Stops the owned operation if it is working on one of them,
    /// including an Add that moved onto one to refresh its saved sign-in, and waits for it to actually finish. Until the
    /// returned lease is disposed, no saved-session operation starts on one of these profiles (it is refused with
    /// <see cref="Zenith.Sessions.Core.AccountLinkException.ProfileRemoved"/>, before anything is read or sent; a share-link check, handoff
    /// inspection or native preparation reports that refusal as its ProfileRemoved outcome, not as Busy), and an Add refuses to move onto
    /// one. Only the caller can know which rows it removes and only this service knows which row an Add moved onto, so the
    /// check and the move share the state gate. Hold the lease until the profiles and their saved sign-ins are removed.
    /// The lease says which kind of operation was stopped, so the caller cancels its own command only when it owned that operation.
    /// </summary>
    public async Task<ProfileRemovalLease> BeginProfileRemovalAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profileIds); ct.ThrowIfCancellationRequested();
        Guid[] ids = profileIds.Where(id => id != Guid.Empty).Distinct().ToArray(); Operation? active;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var id in ids) _removing[id] = _removing.GetValueOrDefault(id) + 1;
            active = _active is { } current && ids.Contains(current.ProfileId) ? current : null;
            if (active is not null) active.Invalidated = true;
        }
        var lease = new ProfileRemovalLease(this, ids, active?.Kind ?? SavedSessionOperationKind.None);
        try
        {
            if (active is null) return lease;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            active.Cancel();
            // An uncooperative browser, network or storage owner is awaited to actual completion, as Disconnect does.
            await active.Completion.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private void EndProfileRemoval(Guid[] ids)
    {
        lock (_state)
            foreach (var id in ids)
                if (_removing.TryGetValue(id, out int holders)) { if (holders <= 1) _removing.Remove(id); else _removing[id] = holders - 1; }
    }

    /// <summary>
    /// Held by a profile removal; disposing it lets operations start on these profiles, and an Add refresh them, again.
    /// <see cref="StoppedOperationKind"/> names the operation working on one of them that was stopped and has finished,
    /// or is <see cref="SavedSessionOperationKind.None"/> when none was.
    /// </summary>
    public sealed class ProfileRemovalLease : IDisposable
    {
        private RobloxSessionLinkService? _owner;
        private readonly Guid[] _ids;
        internal ProfileRemovalLease(RobloxSessionLinkService owner, Guid[] ids, SavedSessionOperationKind stopped)
        { _owner = owner; _ids = ids; StoppedOperationKind = stopped; }
        public SavedSessionOperationKind StoppedOperationKind { get; }
        public bool StoppedOperation => StoppedOperationKind != SavedSessionOperationKind.None;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndProfileRemoval(_ids);
    }
}
