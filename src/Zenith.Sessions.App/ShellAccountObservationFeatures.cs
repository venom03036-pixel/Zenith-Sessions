using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class AccountRow
{
    private string _serverPresence = "Not refreshed";
    public string ServerPresenceText { get => _serverPresence; internal set => Set(ref _serverPresence, value); }
}
public sealed record AccountFriendshipReview(RobloxFriendshipReview Batch, string Summary)
{ public override string ToString() => "[Friendship review]"; }

public sealed partial class ShellViewModel
{
    private IRobloxAccountDataClient _accountDataClient = null!;
    private readonly Dictionary<Guid, (RobloxAccountContext Context, string Text)> _serverPresence = [];
    private AccountFriendshipReview? _friendshipReview;
    private Guid? _observationSelection;
    private bool _readingPrivateDetails;
    private bool _observingAccountAction;
    public string CancelAccountActionLabel => _observingAccountAction ? "Cancel account action" : "Cancel account setup";
    private string _privateAccountDetails = "Choose Refresh private details to ask Roblox. Unknown fields stay unknown.";
    private string _friendshipResults = "No friendship run this session.";
    public string PrivateAccountDetails { get => _privateAccountDetails; private set => Set(ref _privateAccountDetails, value); }
    public string FriendshipResults { get => _friendshipResults; private set => Set(ref _friendshipResults, value); }
    public AsyncCommand RefreshPresence { get; private set; } = null!;
    public AsyncCommand RefreshPrivateDetails { get; private set; } = null!;
    public AsyncCommand BefriendReviewedAccounts { get; private set; } = null!;

    private void InitializeAccountObservationFeatures()
    {
        RefreshPresence = new(_ => ReadPresenceAsync(), error => Fail(error, ManagerSubsystem.SignIn));
        RefreshPrivateDetails = new(_ => ReadPrivateDetailsAsync(), error => Fail(error, ManagerSubsystem.SignIn));
        BefriendReviewedAccounts = new(p => RunFriendshipReviewAsync(p as AccountFriendshipReview), error => Fail(error, ManagerSubsystem.SignIn));
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SelectedAccount)) return;
            if (_observationSelection != SelectedAccount?.Profile.Id)
            {
                _observationSelection = SelectedAccount?.Profile.Id;
                PrivateAccountDetails = "Not refreshed for this account.";
                if (_readingPrivateDetails) _authorizationStop?.Cancel();
            }
            foreach (var row in Accounts)
                if (_serverPresence.TryGetValue(row.Profile.Id, out var presence) && row.KnownUserId == presence.Context.UserId
                    && row.AuthorizationGeneration == presence.Context.Generation) row.ServerPresenceText = presence.Text;
        };
    }
    private static RobloxAccountContext? ObservationContext(AccountRow? row) => row is { NeedsSignIn: false, SignInStatusBusy: false,
        KnownUserId: > 0, AuthorizationGeneration: > 0 } ? new(row.Profile.Id, row.KnownUserId.Value, row.AuthorizationGeneration.Value) : null;
    private bool CanObserveAccounts() => CanUseDirectories() && !IsAuthorizing && !_nativeLaunch.IsBusy;
    private async Task ReadPresenceAsync()
    {
        if (!CanObserveAccounts()) return;
        var checkedRows = Accounts.Where(a => a.IsChecked).ToArray();
        var rows = checkedRows.Length > 0 ? checkedRows : SelectedAccount is { } selected ? [selected] : Array.Empty<AccountRow>();
        if (rows.Length is 0 or > 100) { Notice = "Select an account or check up to 100 accounts on this page."; return; }
        var contexts = rows.Select(ObservationContext).Where(c => c is not null).Cast<RobloxAccountContext>().ToArray();
        foreach (var row in rows) { row.ServerPresenceText = "Not refreshed"; _serverPresence.Remove(row.Profile.Id); }
        await WithAccountObservationsAsync(async ct =>
        {
            int count = 0;
            foreach (var context in contexts)
            {
                if (count > 0) await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
                _linkingProfile = context.ProfileId;
                var result = await Task.Run(() => _linkService.ReadAccountAsync(context, RobloxAccountRead.Presence, null, ct), ct);
                ct.ThrowIfCancellationRequested(); count++;
                string text = result.Observation?.Presence is { } presence ? presence.State switch
                {
                    RobloxReportedPresence.Offline => "Roblox reports offline",
                    RobloxReportedPresence.Online => "Roblox reports online",
                    RobloxReportedPresence.InStudio => "Roblox reports in Studio",
                    _ => presence.PlaceId is { } place ? $"Roblox reports in game · place {place}" : "Roblox reports in game · place unknown"
                } : "Roblox presence unavailable";
                if (_serverPresence.Count >= 100 && !_serverPresence.ContainsKey(context.ProfileId)) _serverPresence.Remove(_serverPresence.Keys.First());
                _serverPresence[context.ProfileId] = (result.Context, text);
                if (result.Outcome is RobloxAccountOutcome.Challenge or RobloxAccountOutcome.RateLimited) { Notice = "Presence refresh stopped: Roblox requires a security check or is busy."; return; }
            }
            Notice = $"Presence refresh finished for {count} accounts. This is Roblox-reported state, not client arrival evidence.";
        });
    }
    private async Task ReadPrivateDetailsAsync()
    {
        if (!CanObserveAccounts() || ObservationContext(SelectedAccount) is not { } context) return;
        var fields = new[] { RobloxAccountRead.Robux, RobloxAccountRead.Premium, RobloxAccountRead.EmailVerified, RobloxAccountRead.TwoStepMethods,
            RobloxAccountRead.BanState, RobloxAccountRead.FriendsCount, RobloxAccountRead.FollowersCount };
        var values = new Dictionary<RobloxAccountRead, RobloxAccountObservation?>();
        _readingPrivateDetails = true; PrivateAccountDetails = "Refreshing private details…";
        try
        {
            await WithAccountObservationsAsync(async ct =>
            {
                foreach (var field in fields)
                {
                    _linkingProfile = context.ProfileId;
                    var result = await Task.Run(() => _linkService.ReadAccountAsync(context, field, null, ct), ct);
                    ct.ThrowIfCancellationRequested(); context = result.Context;
                    values[field] = result.Outcome == RobloxAccountOutcome.Success ? result.Observation : null;
                    if (SelectedAccount?.Profile.Id != context.ProfileId) return;
                    PrivateAccountDetails = string.Join("\n", fields.Select(Describe));
                    if (result.Outcome is RobloxAccountOutcome.Challenge or RobloxAccountOutcome.RateLimited) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
                }
                Notice = "Private account details refreshed where available. Unknown fields were not inferred; nothing was saved or logged.";
                string Describe(RobloxAccountRead field)
                {
                    var value = values.GetValueOrDefault(field);
                    string label = field switch { RobloxAccountRead.EmailVerified => "Email verified", RobloxAccountRead.TwoStepMethods => "2-step methods", RobloxAccountRead.BanState => "Banned", RobloxAccountRead.FriendsCount => "Friends", RobloxAccountRead.FollowersCount => "Followers", _ => field.ToString() };
                    return label + ": " + (value?.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? value?.Methods ?? (value?.Flag is { } flag ? flag ? "Yes" : "No" : "Unknown"));
                }
            });
        }
        finally { _readingPrivateDetails = false; }
    }
    public AccountFriendshipReview? CaptureFriendshipReview()
    {
        _friendshipReview = null;
        if (!CanObserveAccounts()) return null;
        var rows = Accounts.Where(a => a.IsChecked).ToArray();
        var contexts = rows.Select(ObservationContext).ToArray();
        if (rows.Length is < 2 or > 20 || contexts.Any(c => c is null) || contexts.Select(c => c!.UserId).Distinct().Count() != rows.Length)
        { Notice = "Check 2–20 different accounts with valid saved sign-ins before reviewing friendship pairs."; return null; }
        var batch = new RobloxFriendshipReview(contexts.Cast<RobloxAccountContext>().ToArray());
        var labels = rows.ToDictionary(a => a.Profile.Id, a => $"{a.Label} (ID {a.KnownUserId})");
        string summary = "This run can send and accept Roblox friend requests between these saved accounts only. Every pair is listed below. No requests are sent until you choose Befriend these pairs.\n\n"
            + string.Join("\n", batch.Pairs.Select(p => $"{labels[p.First.ProfileId]} ↔ {labels[p.Second.ProfileId]}"))
            + "\n\nRequests run one at a time. A security challenge, busy response or uncertain result stops the run. Cancelling cannot undo requests Roblox already processed.";
        return _friendshipReview = new(batch, summary);
    }
    public void DiscardFriendshipReview(AccountFriendshipReview review) { if (ReferenceEquals(_friendshipReview, review)) _friendshipReview = null; }
    private async Task RunFriendshipReviewAsync(AccountFriendshipReview? review)
    {
        if (!CanObserveAccounts() || review is null || !ReferenceEquals(_friendshipReview, review)) return;
        _friendshipReview = null; FriendshipResults = "Friendship run underway…";
        var reported = new List<RobloxFriendshipPairResult>();
        await WithAccountObservationsAsync(async ct =>
        {
            await new RobloxFriendshipBatch(_linkService).RunConfirmedAsync(review.Batch, ct, result =>
            {
                // Ordinary cancellation retains the partial report. Lock/Panic/exit discard it and fence late callbacks.
                if (_preserveStopNotice || IsLocked || _shuttingDown) return;
                reported.Add(result);
                FriendshipResults = string.Join("\n", reported.Select(row => $"IDs {row.Pair.First.UserId} ↔ {row.Pair.Second.UserId}: {row.Status}"));
            });
            ct.ThrowIfCancellationRequested();
            Notice = "Friendship run finished. Review each pair's result before starting another run.";
        });
    }
    private async Task WithAccountObservationsAsync(Func<CancellationToken, Task> work)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _authorizationStop = stop; _preserveStopNotice = false; _observingAccountAction = true;
        Raise(nameof(IsAuthorizing)); Raise(nameof(CancelAccountActionLabel));
        try { await work(stop.Token); }
        catch (OperationCanceledException)
        { if (!_preserveStopNotice && !IsLocked && !_shuttingDown) Notice = "Account request cancelled. An interrupted account may need a new sign-in. Processed friendship requests cannot be undone here."; }
        catch (Exception error) when (error is RobloxSessionException or AccountLinkException)
        {
            _diagnostics.RecordFailure(ManagerSubsystem.SignIn, error.GetType());
            if (!_preserveStopNotice && !IsLocked && !_shuttingDown) Notice = "Account request stopped. Review the account and sign in again if needed before retrying; no private response was logged.";
        }
        finally
        {
            try { if (!_lifetime.IsCancellationRequested && !_shuttingDown) await LoadAccountsAsync(_lifetime.Token); }
            finally
            {
                if (ReferenceEquals(_authorizationStop, stop)) { _authorizationStop = null; _linkingProfile = null; }
                stop.Dispose(); _observingAccountAction = false; Raise(nameof(IsAuthorizing)); Raise(nameof(CancelAccountActionLabel));
            }
        }
    }
    private void ClearPrivateAccountObservations()
    {
        ClearSignedServerObservations();
        _friendshipReview = null; _serverPresence.Clear();
        // Clear backing data first; a failing presentation subscriber must not skip the remaining clears or shutdown.
        _privateAccountDetails = "Not refreshed for this account."; _friendshipResults = "No friendship results retained.";
        foreach (var row in Accounts) Notify(() => row.ServerPresenceText = "Not refreshed");
        Notify(() => Raise(nameof(PrivateAccountDetails))); Notify(() => Raise(nameof(FriendshipResults)));
        void Notify(Action publish)
        { try { publish(); } catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.SignIn, error.GetType()); } }
    }
}
