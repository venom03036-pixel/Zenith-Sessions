using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private long _accountSetupEpoch;
    public bool CanAddRobloxAccount => _ready && !IsLocked && !_shuttingDown && !IsAuthorizing && !_nativeLaunch.IsBusy;

    private async Task AddRobloxAccountAsync()
    {
        if (!CanAddRobloxAccount) return;
        // Own the same sign-in lane before the first asynchronous operation. The
        // current selection is a presentation choice, never this new account's ID.
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _authorizationStop = stop; _linkingProfile = null; _preserveStopNotice = false;
        long epoch = Interlocked.Increment(ref _accountSetupEpoch);
        Guid? selectionAtStart = SelectedAccount?.Profile.Id;
        Raise(nameof(IsAuthorizing));
        Notice = "Sign in on Roblox’s page, then choose Save this Roblox sign-in. Your account will be added after its player ID is verified.";
        try
        {
            var added = await Task.Run(() => _linkService.AddAsync(stop.Token), stop.Token);
            bool keepSelection = SelectedAccount?.Profile.Id != selectionAtStart;
            if (!await ReloadAfterAccountSetupAsync() || !OwnsFeedback()) return;
            if (stop.IsCancellationRequested)
            {
                Notice = "Account setup stopped. Any account already saved was preserved for review.";
                return;
            }
            if (!keepSelection && SelectedAccount?.Profile.Id == selectionAtStart)
                SelectedAccount = Accounts.FirstOrDefault(row => row.Profile.Id == added.ProfileId) ?? SelectedAccount;
            if (added.Refreshed)
            {
                Notice = $"Refreshed the saved sign-in for @{added.Identity.Username}. This account was already in your library, so no new row was added and its name, notes and groups were kept.";
                AddEvent("Saved Roblox sign-in refreshed for an account already in the library.");
            }
            else
            {
                Notice = $"Added @{added.Identity.Username}. The sign-in is saved with Windows protection; native launching is verified separately.";
                AddEvent("Roblox account added from its verified sign-in identity.");
            }
            _diagnostics.Record(ManagerEventKind.AuthorizationLinked);
        }
        catch (OperationCanceledException)
        {
            // Closing the official sign-in window is cancellation even when its
            // provider did not cancel the caller's token. Completed metadata is kept.
            if (await ReloadAfterAccountSetupAsync() && OwnsFeedback()) Notice = "Account setup cancelled. Existing accounts were preserved; any already-saved account remains available for review.";
        }
        catch (Exception error)
        {
            bool reloaded = await ReloadAfterAccountSetupAsync();
            if (reloaded && OwnsFeedback() && !stop.IsCancellationRequested) Fail(error);
            else _diagnostics.Record(ManagerEventKind.OperationFailed, error.GetType());
        }
        finally
        {
            if (ReferenceEquals(_authorizationStop, stop)) { _authorizationStop = null; _linkingProfile = null; }
            stop.Dispose(); Raise(nameof(IsAuthorizing));
        }

        bool OwnsFeedback() => epoch == Interlocked.Read(ref _accountSetupEpoch) && !_preserveStopNotice && !IsLocked && !_shuttingDown && !_lifetime.IsCancellationRequested;
    }

    private async Task<bool> ReloadAfterAccountSetupAsync()
    {
        if (_lifetime.IsCancellationRequested || _shuttingDown) return false;
        try { await LoadAccountsAsync(_lifetime.Token); return true; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            _diagnostics.Record(ManagerEventKind.OperationFailed, error.GetType());
            if (!_preserveStopNotice && !IsLocked && !_shuttingDown)
                Notice = "The account list could not refresh. Saved files were preserved. Use Refresh or reopen Zenith to review them.";
            return false;
        }
    }
}
