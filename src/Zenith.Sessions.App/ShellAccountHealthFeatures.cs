using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class AccountRow
{
    /// <summary>The cached protected-record summary says this saved sign-in may still be reused.</summary>
    public bool HasLinkedSavedSignIn => _authorizationMetadata?.Health == AuthorizationHealth.Linked;
    /// <summary>
    /// The row's button opens Roblox sign-in instead of launching. Decided from the cached protected-record summary
    /// (no saved sign-in, not Linked, or expired), never from the display wording, so any status text, including
    /// "Security check required", cannot turn a sign-in into a launch.
    /// </summary>
    public bool NeedsSignIn => !HasLinkedSavedSignIn || _signInExpired;
}

/// <summary>
/// On-demand check of the checked rows' saved sign-ins (at most one page). It runs in the same
/// single sign-in lane as Check saved sign-in, so Cancel, Lock, Panic, Exit, Disconnect and Remove
/// stop it. It never runs automatically and never retries: the first unreachable, busy or timed-out
/// result stops the batch, because each check fences its account before contacting Roblox. A check
/// Roblox never processed restores its own account (or keeps another operation's newer sign-in), and a
/// check refused because another account operation holds the sign-in service changes nothing; either
/// way that account counts as not checked. A row whose status the busy protected store could not read
/// is counted as such, not as having no saved sign-in. A row being removed from the library while the
/// batch runs, or already removed, is skipped and counted as such: nothing is sent to Roblox for it.
/// </summary>
public sealed partial class ShellViewModel
{
    private const int MaximumSignInChecks = 100;
    private static readonly TimeSpan SignInCheckGap = TimeSpan.FromMilliseconds(400);
    public AsyncCommand CheckCheckedSignIns { get; private set; } = null!;

    private void InitializeAccountHealthFeatures() => CheckCheckedSignIns = new(_ => CheckCheckedSignInsAsync(), error => Fail(error, ManagerSubsystem.SignIn));

    private async Task CheckCheckedSignInsAsync()
    {
        if (IsLocked || !_ready || _shuttingDown || IsAuthorizing) return;
        if (_nativeLaunch.IsBusy) { Notice = "Finish or stop the native trial before checking saved sign-ins."; return; }
        // Pin the work list now. Later checkbox, search or page changes cannot redirect it.
        var targets = new List<Guid>(); int anyChecked = 0, statusUnread = 0, noSavedSignIn = 0, needSignIn = 0, overflow = 0;
        foreach (var row in Accounts)
        {
            if (!row.IsChecked) continue;
            anyChecked++;
            if (row.SignInStatusBusy) statusUnread++; // The store was busy: this saved sign-in is unknown, not missing.
            else if (row.KnownUserId is null) noSavedSignIn++;
            else if (!row.HasLinkedSavedSignIn) needSignIn++; // Already known; Roblox is not contacted.
            else if (targets.Count < MaximumSignInChecks) targets.Add(row.Profile.Id);
            else overflow++;
        }
        if (anyChecked == 0) { Notice = "Check one or more accounts on this page, then choose Check checked sign-ins."; return; }
        if (targets.Count == 0)
        { Notice = "Nothing was sent to Roblox." + DescribeSignInChecks(0, needSignIn, 0, noSavedSignIn, statusUnread, 0, overflow, false, false); return; }

        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _authorizationStop = stop; _linkingProfile = null; _preserveStopNotice = false; Raise(nameof(IsAuthorizing));
        Notice = $"Checking {targets.Count} saved {Plural(targets.Count, "sign-in", "sign-ins")} one at a time…";
        // stoppedUnchanged: the check that stopped the batch left its account unchanged, so it counts as not checked.
        int verified = 0, securityCheck = 0, attempted = 0, stoppedUnchanged = 0, removed = 0; var challenged = new HashSet<Guid>();
        string? stoppedBecause = null; bool cancelled = false, inFlight = false;
        try
        {
            try
            {
                foreach (Guid profileId in targets)
                {
                    // Fixed spacing between Roblox requests; the delay observes Cancel, Lock and Panic.
                    if (attempted > 0) await Task.Delay(SignInCheckGap, stop.Token);
                    attempted++; _linkingProfile = profileId; inFlight = true;
                    try { await Task.Run(() => _linkService.CheckSavedAsync(profileId, stop.Token), stop.Token); verified++; }
                    catch (RobloxSessionException error) when (error.Failure == RobloxSessionFailure.Challenge) { securityCheck++; challenged.Add(profileId); }
                    catch (RobloxSessionException error) when (error.Failure is RobloxSessionFailure.Network or RobloxSessionFailure.TimedOut)
                    {
                        // The fence already marked this account as needing sign-in. Stop before
                        // an outage can demote every remaining account.
                        needSignIn++; stoppedBecause = error.Failure == RobloxSessionFailure.TimedOut ? "a check timed out" : "Roblox could not be reached";
                        break;
                    }
                    catch (RobloxSessionException error) when (error.Failure == RobloxSessionFailure.NotProcessed)
                    {
                        // Roblox never processed this check: its account was restored, or another operation's
                        // newer sign-in was kept. Stop anyway: the rest would meet the same outage or rate limit.
                        stoppedUnchanged = 1; stoppedBecause = "Roblox could not be reached or was busy";
                        break;
                    }
                    catch (AccountLinkException error) when (error.ProfileRemoved)
                    {
                        // This account is being removed from the library, or already was. Its check was refused before
                        // anything was read or sent to Roblox; it is not an answer about its sign-in. Check the rest.
                        removed++;
                    }
                    catch (AccountLinkException error) when (error.OperationInProgress)
                    {
                        // A share-link check, handoff inspection or launch preparation holds the sign-in service.
                        // This check read and changed nothing; stop rather than refuse every remaining account.
                        stoppedUnchanged = 1; stoppedBecause = "another account operation was running. Try again when it finishes";
                        break;
                    }
                    catch (Exception error) when (error is RobloxSessionException or AccountLinkException) { needSignIn++; }
                    finally { _linkingProfile = null; }
                    inFlight = false;
                }
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception error) when (!_lifetime.IsCancellationRequested && !_shuttingDown)
            {
                // Storage or programming failure: show what the protected store now holds, then report it.
                await LoadAccountsAsync(_lifetime.Token);
                if (stop.IsCancellationRequested || _preserveStopNotice || IsLocked) { _diagnostics.RecordFailure(ManagerSubsystem.SignIn,error.GetType()); return; }
                throw;
            }
            if (_lifetime.IsCancellationRequested || _shuttingDown) return;
            await LoadAccountsAsync(_lifetime.Token);
            foreach (var row in Accounts)
                if (challenged.Contains(row.Profile.Id) && row.KnownUserId is not null && !row.HasLinkedSavedSignIn)
                    row.SetAuthorization("Security check required", "Roblox asked for a human security check. Choose Sign in with Roblox and complete it in the official window; this saved sign-in is not reused until then.");
            if (_preserveStopNotice || IsLocked) return;
            string head = cancelled ? "Saved sign-in check cancelled." : stoppedBecause is { } reason ? $"Saved sign-in check stopped because {reason}." : "Saved sign-in check finished.";
            string summary = head + DescribeSignInChecks(verified, needSignIn, securityCheck, noSavedSignIn, statusUnread, removed,
                targets.Count - attempted + stoppedUnchanged + overflow, stoppedBecause is not null && stoppedUnchanged == 0, cancelled && inFlight);
            Notice = summary; AddEvent(summary);
        }
        finally
        {
            if (ReferenceEquals(_authorizationStop, stop)) { _authorizationStop = null; _linkingProfile = null; }
            stop.Dispose(); Raise(nameof(IsAuthorizing));
        }
    }

    private static string DescribeSignInChecks(int verified, int needSignIn, int securityCheck, int noSavedSignIn, int statusUnread, int removed,
        int notChecked, bool stoppedAccountFenced, bool interruptedByCancel)
    {
        string text = "";
        var parts = new List<string>(3);
        if (verified > 0) parts.Add($"{verified} verified");
        if (needSignIn > 0) parts.Add($"{needSignIn} {Plural(needSignIn, "needs", "need")} sign-in");
        if (securityCheck > 0) parts.Add($"{securityCheck} {Plural(securityCheck, "needs", "need")} a Roblox security check");
        if (parts.Count > 0) text += " " + string.Join(", ", parts) + ".";
        if (stoppedAccountFenced) text += " The account being checked when it stopped needs sign-in because its check could not finish.";
        if (interruptedByCancel) text += " The account being checked when you cancelled may need a new sign-in.";
        if (noSavedSignIn > 0) text += $" {noSavedSignIn} checked {Plural(noSavedSignIn, "account has", "accounts have")} no saved sign-in.";
        if (statusUnread > 0) text += $" {statusUnread} checked {Plural(statusUnread, "account's", "accounts'")} sign-in status could not be read because protected storage was busy. Choose Refresh, then check again.";
        if (removed > 0) text += $" {removed} checked {Plural(removed, "account was", "accounts were")} being removed from your library, so {Plural(removed, "it was", "they were")} not checked.";
        if (notChecked > 0) text += $" {notChecked} not checked; {Plural(notChecked, "its saved sign-in was", "their saved sign-ins were")} left unchanged.";
        return text;
    }
    private static string Plural(int count, string one, string many) => count == 1 ? one : many;
}
