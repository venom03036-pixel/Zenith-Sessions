using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private long _groupChangeEpoch;
    private CancellationTokenSource? _groupChangeStop;
    public Task<AccountGroupChangePreview> PreviewGroupChangeAsync(AccountGroupChange change, CancellationToken ct)
        => RunOwnedGroupChangeAsync(token => Task.Run(() => GroupLifecycle.PreviewGroupChangeAsync(change, token), token), ct);
    public Task<int> ApplyGroupChangeAsync(AccountGroupChangePreview expected, CancellationToken ct)
    {
        long epoch = Interlocked.Read(ref _groupChangeEpoch);
        return RunOwnedGroupChangeAsync(async token =>
        {
            int changed = await Task.Run(() => GroupLifecycle.ApplyGroupChangeAsync(expected, token), token);
            // The database commit is authoritative even if a later refresh is cancelled or fails.
            // Never turn this into a second write or remove accounts as rollback.
            if (!IsLocked && !_shuttingDown && epoch == Interlocked.Read(ref _groupChangeEpoch))
            {
                if (string.Equals(Group.Value, expected.Change.SourceName, StringComparison.OrdinalIgnoreCase))
                {
                    _updatingGroups = true;
                    try { _group = expected.Change.Kind == AccountGroupChangeKind.Rename
                        ? new(expected.Change.DestinationName, expected.Change.DestinationName) : GroupFilter.All; Raise(nameof(Group)); }
                    finally { _updatingGroups = false; }
                }
            }
            await LoadAccountsAsync(token);
            if (!IsLocked && !_shuttingDown && epoch == Interlocked.Read(ref _groupChangeEpoch))
            {
                Notice = changed==0&&expected.SourceExists ? "Saved the group change. No account memberships needed changing."
                    : $"Updated group memberships on {changed:N0} account(s). Saved sign-ins and Roblox clients were preserved.";
                AddEvent("Account group metadata updated."); _diagnostics.Record(ManagerEventKind.ProfileSaved);
            }
            return changed;
        }, ct);
    }
    private IAccountGroupLifecycle GroupLifecycle => _accounts as IAccountGroupLifecycle
        ?? throw new InvalidOperationException("This library does not support shared group changes.");
    private async Task<T> RunOwnedGroupChangeAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long epoch = Interlocked.Read(ref _groupChangeEpoch); T result = default!;
        await OwnProfileOperation(async () =>
        {
            if(!_ready)throw new InvalidOperationException("Open the local account library before changing groups.");
            if (IsLocked || _shuttingDown || epoch != Interlocked.Read(ref _groupChangeEpoch))
                throw new OperationCanceledException("Group changes were stopped before this operation began.");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            _groupChangeStop = stop;
            try { result = await work(stop.Token); }
            finally { if (ReferenceEquals(_groupChangeStop, stop)) _groupChangeStop = null; }
        }, ct);
        return result;
    }
    private void StopGroupChanges()
    { Interlocked.Increment(ref _groupChangeEpoch); _groupChangeStop?.Cancel(); }
}
