namespace Zenith.Sessions.Windows;

public sealed partial class RobloxSessionLinkService
{
    // Profiles a caller is removing, with how many removals hold each one. Guarded by _state.
    private readonly Dictionary<Guid, int> _removing = [];

    /// <summary>
    /// For a caller that is about to remove these profiles. Stops the owned operation if it is working on one of them,
    /// including an Add that moved onto one to refresh its saved sign-in, and waits for it to actually finish. Until the
    /// returned lease is disposed, no saved-session operation starts on one of these profiles (it is refused with
    /// <see cref="Zenith.Sessions.Core.AccountLinkException.ProfileRemoved"/>, before anything is read or sent), and an Add refuses to move onto
    /// one. Only the caller can know which rows it removes and only this service knows which row an Add moved onto, so the
    /// check and the move share the state gate. Hold the lease until the profiles and their saved sign-ins are removed.
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
        var lease = new ProfileRemovalLease(this, ids, active is not null);
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
    /// Held by a profile removal; disposing it lets operations start on these profiles, and an Add refresh them, again. <see cref="StoppedOperation"/>
    /// is true when an operation working on one of them was stopped and has finished.
    /// </summary>
    public sealed class ProfileRemovalLease : IDisposable
    {
        private RobloxSessionLinkService? _owner;
        private readonly Guid[] _ids;
        internal ProfileRemovalLease(RobloxSessionLinkService owner, Guid[] ids, bool stoppedOperation)
        { _owner = owner; _ids = ids; StoppedOperation = stoppedOperation; }
        public bool StoppedOperation { get; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndProfileRemoval(_ids);
    }
}
