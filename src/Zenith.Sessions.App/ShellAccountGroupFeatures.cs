using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private CancellationTokenSource? _groupMembershipStop;

    // Capture recipients before the existing profile lane can wait or selection can change.
    public Task AddAccountsToGroupAsync(IReadOnlyList<AccountProfile> expected, string group)
    {
        if (IsLocked || _shuttingDown || !_ready)
            return Task.FromException(new InvalidOperationException("Unlock the available account library before changing groups."));
        if (_accounts is not IAccountGroupEditor editor)
            return Task.FromException(new InvalidOperationException("This library provider does not support multiple group memberships."));
        if (expected is null || expected.Count is < 1 or > 100)
            return Task.FromException(new ArgumentException("Select between one and 100 accounts on the current page."));
        AccountProfile[] pinned = expected.ToArray();
        string name = AccountGroups.ValidateName(group);
        long generation = _directoryGeneration;
        return OwnProfileOperation(async () =>
        {
            if (generation != _directoryGeneration || IsLocked || _shuttingDown)
                throw new OperationCanceledException("The group change was stopped before it began.");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _groupMembershipStop = stop;
            try
            {
                int changed = await Task.Run(() => editor.ApplyGroupAsync(pinned, name, stop.Token), stop.Token);
                // A cancellation after commit cannot undo saved metadata. Reload it, but retain Panic/Lock feedback.
                await LoadAccountsAsync(_lifetime.Token);
                if (generation != _directoryGeneration || IsLocked || _shuttingDown) return;
                Notice = changed == 0 ? "Every selected account already belongs to that group. Nothing changed."
                    : $"Added {changed} account(s) to the group. Existing memberships and queued requests were preserved.";
                if (changed > 0) _diagnostics.Record(ManagerEventKind.ProfileSaved);
            }
            finally { if (ReferenceEquals(_groupMembershipStop, stop)) _groupMembershipStop = null; }
        });
    }
}
