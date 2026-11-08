using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private IRobloxSignedInServerClient _signedServerClient = null!;
    private IReadOnlyList<PublicServerRow> _signedPublicServers = [];
    private IReadOnlyList<OwnedPrivateServer> _ownedPrivateServers = [];
    private RobloxAccountContext? _signedServerContext;
    private SignedInServerQuery? _signedServerQuery;
    private string? _signedServerCursor;
    private readonly HashSet<string> _signedServerCursors = new(StringComparer.Ordinal);
    private long _signedServerEpoch;
    private bool _readingSignedServers;
    private bool _publishingAccountRows;
    private DateTimeOffset _signedServersNotBefore;
    private PrivateServerRenameReview? _privateRenameReview;
    private PublicServerRow? _selectedSignedServer;
    private OwnedPrivateServer? _selectedOwnedServer;
    private string _privateServerName = "", _signedServerStatus = "Choose an account and fetch a signed-in page explicitly. Nothing loads automatically.";
    public IReadOnlyList<PublicServerRow> SignedPublicServers => _signedPublicServers;
    public IReadOnlyList<OwnedPrivateServer> OwnedPrivateServers => _ownedPrivateServers;
    public PublicServerRow? SelectedSignedServer { get => _selectedSignedServer; set => Set(ref _selectedSignedServer, value); }
    public OwnedPrivateServer? SelectedOwnedServer { get => _selectedOwnedServer; set { if (Set(ref _selectedOwnedServer, value)) _privateRenameReview = null; } }
    public string PrivateServerName { get => _privateServerName; set { if (Set(ref _privateServerName, value)) _privateRenameReview = null; } }
    public string SignedServerStatus { get => _signedServerStatus; private set => Set(ref _signedServerStatus, value); }
    public bool CanBrowseSignedServers => CanObserveAccounts() && ObservationContext(SelectedAccount) is not null && ServerBrowser.ReadyDestination is not null;
    public bool CanNextSignedServers => CanBrowseSignedServers && _signedServerCursor is not null && CurrentSignedServerContext();
    public AsyncCommand BrowseSignedServers { get; private set; } = null!;
    public AsyncCommand NextSignedServers { get; private set; } = null!;
    public AsyncCommand RenameOwnedPrivateServer { get; private set; } = null!;
    public Command UseSignedServer { get; private set; } = null!;
    public Command ClearSignedServers { get; private set; } = null!;

    public sealed record PrivateServerRenameReview(RobloxAccountContext Context, OwnedPrivateServer Server, string Name, long Epoch)
    { public override string ToString() => "[Private-server rename review]"; }
    public PrivateServerRenameReview? CapturePrivateServerRenameReview()
    {
        _privateRenameReview = null;
        string name = PrivateServerName.Trim();
        if (!CanBrowseSignedServers || !CurrentSignedServerContext() || _signedServerQuery?.Order != SignedInServerOrder.OwnedPrivate
            || SelectedOwnedServer is not { } row || !_ownedPrivateServers.Any(r => ReferenceEquals(r,row)) || !OwnedPrivateServer.IsNewName(name)) return null;
        return _privateRenameReview = new(_signedServerContext!,row,name,_signedServerEpoch);
    }
    public void DiscardPrivateServerRenameReview(PrivateServerRenameReview review)
    { if (ReferenceEquals(_privateRenameReview,review)) _privateRenameReview = null; }

    private void InitializeSignedServerFeatures()
    {
        BrowseSignedServers = new(p => Enum.TryParse<SignedInServerOrder>(p?.ToString(), out var order) && Enum.IsDefined(order)
            ? ReadSignedServerPageAsync(order,false) : Task.CompletedTask, e => Fail(e,ManagerSubsystem.SignIn));
        NextSignedServers = new(_ => _signedServerQuery is { } query ? ReadSignedServerPageAsync(query.Order,true) : Task.CompletedTask, e => Fail(e,ManagerSubsystem.SignIn));
        RenameOwnedPrivateServer = new(p => RenamePrivateServerAsync(p as PrivateServerRenameReview), e => Fail(e,ManagerSubsystem.SignIn));
        ClearSignedServers = new(_ => ClearSignedServerObservations());
        UseSignedServer = new(_ =>
        {
            if (!CanBrowseSignedServers || !CurrentSignedServerContext() || SelectedSignedServer is not { } row
                || !_signedPublicServers.Any(r => ReferenceEquals(r,row)) || DateTimeOffset.UtcNow - row.Server.ObservedUtc is var age && (age < TimeSpan.Zero || age > TimeSpan.FromMinutes(5))) return;
            ServerBrowser.UseInvitedServer(row.Server.PlaceId,row.Server.JobId);
        });
        PropertyChanged += (_,args) =>
        {
            if (args.PropertyName == nameof(Tab) && !IsJoinGame) ClearSignedServerObservations();
            if (args.PropertyName == nameof(SelectedAccount)) ReconcileSignedServerSelection();
            if (args.PropertyName is nameof(SelectedAccount) or nameof(IsAuthorizing) or nameof(IsLocked) or nameof(IsReady)) NotifySignedServerAvailability();
        };
        ServerBrowser.PropertyChanged += (_,args) =>
        {
            if (args.PropertyName == nameof(ServerBrowserViewModel.Destination) && _signedServerQuery is { } query && ServerBrowser.Destination?.PlaceId != query.PlaceId)
                ClearSignedServerObservations();
            if (args.PropertyName is nameof(ServerBrowserViewModel.Destination) or nameof(ServerBrowserViewModel.CanUseDestination)) NotifySignedServerAvailability();
        };
    }
    private bool CurrentSignedServerContext() => _signedServerContext is { } context && ObservationContext(SelectedAccount) == context
        && _signedServerQuery?.PlaceId == ServerBrowser.ReadyDestination?.PlaceId;
    private void ReconcileSignedServerSelection()
    {
        if(!_publishingAccountRows && _signedServerContext is { } context && ObservationContext(SelectedAccount)!=context)
            ClearSignedServerObservations();
    }
    private async Task ReadSignedServerPageAsync(SignedInServerOrder order, bool next)
    {
        if (!CanBrowseSignedServers || ObservationContext(SelectedAccount) is not { } context || ServerBrowser.ReadyDestination is not { } destination) return;
        if (DateTimeOffset.UtcNow < _signedServersNotBefore) { SignedServerStatus="Wait for Roblox's server request limit to reset before fetching again."; return; }
        if (next && !CanNextSignedServers) return;
        var query = next ? _signedServerQuery! with { Cursor = _signedServerCursor } : new(destination.PlaceId,order,ServerBrowser.HideFull);
        if (!next) ClearSignedServerObservations();
        long epoch = _signedServerEpoch;
        _signedServerContext = context; _signedServerQuery = query; _readingSignedServers = true;
        SignedServerStatus = "Loading one page with the selected account…";
        try
        {
            await WithAccountObservationsAsync(async ct =>
            {
                _linkingProfile = context.ProfileId;
                var result = await Task.Run(() => _linkService.BrowseSignedInAsync(context,query,ct),ct);
                ct.ThrowIfCancellationRequested();
                if (epoch != _signedServerEpoch || SelectedAccount?.Profile.Id != context.ProfileId || ServerBrowser.ReadyDestination?.PlaceId != query.PlaceId) return;
                _signedServerContext = result.Context; _privateRenameReview = null;
                RememberServerCooldown(result);
                if (result.Outcome != RobloxAccountOutcome.Success || result.Page is not { } page)
                {
                    _signedServerCursor = null;
                    SignedServerStatus = result.Outcome is RobloxAccountOutcome.RateLimited or RobloxAccountOutcome.Challenge
                        ? "Roblox refused or limited this request. No automatic retry was made; review sign-in before trying again."
                        : "Signed-in servers are unavailable. No anonymous fallback or automatic retry was made.";
                    return;
                }
                if (page.NextCursor is { } cursor && !_signedServerCursors.Add(cursor))
                { _signedServerCursor=null; SignedServerStatus="Roblox repeated a page cursor. Paging stopped."; return; }
                if(_signedServerCursors.Count>100) { _signedServerCursor=null; SignedServerStatus="This browse reached its 100-page limit. Fetch from the start to begin another browse."; return; }
                _signedServerCursor = page.NextCursor;
                _signedPublicServers = Array.AsReadOnly(page.PublicServers.Select(row => new PublicServerRow(row)).ToArray());
                _ownedPrivateServers = page.PrivateServers; SelectedSignedServer = null; SelectedOwnedServer = null;
                Raise(nameof(SignedPublicServers)); Raise(nameof(OwnedPrivateServers));
                SignedServerStatus = order == SignedInServerOrder.OwnedPrivate
                    ? $"{page.PrivateServers.Count} owned servers on this page. Other owners were excluded. " + (page.JoinRestricted ? "Roblox reports joining restricted. " : "") + "Rename is a separate reviewed action; joining uses a checked saved private link."
                    : $"{page.PublicServers.Count} public servers on this signed-in page. " + (order == SignedInServerOrder.BestLatency ? "Sorted by reported ping within this page; missing ping stays unknown. " : "Roblox's recommended order. ") + "Nothing has joined and slots are not reserved.";
            });
        }
        finally { _readingSignedServers=false; NotifySignedServerAvailability(); }
    }
    private async Task RenamePrivateServerAsync(PrivateServerRenameReview? review)
    {
        var pending=_privateRenameReview; _privateRenameReview=null;
        if (review is null || !ReferenceEquals(review,pending) || !CanBrowseSignedServers || !CurrentSignedServerContext() || review.Context != _signedServerContext
            || review.Epoch != _signedServerEpoch || !ReferenceEquals(review.Server,SelectedOwnedServer) || PrivateServerName.Trim()!=review.Name) return;
        if (DateTimeOffset.UtcNow < _signedServersNotBefore) { SignedServerStatus="Wait for Roblox's server request limit to reset before renaming."; return; }
        _readingSignedServers=true;
        try
        {
            await WithAccountObservationsAsync(async ct =>
            {
                _linkingProfile=review.Context.ProfileId;
                var result=await Task.Run(()=>_linkService.RenamePrivateAsync(review.Context,review.Server,review.Name,ct),ct);
                ct.ThrowIfCancellationRequested();
                if (review.Epoch!=_signedServerEpoch || SelectedAccount?.Profile.Id!=review.Context.ProfileId) return;
                _signedServerContext=result.Context; _signedServerCursor=null;
                RememberServerCooldown(result);
                SignedServerStatus=result.Outcome==RobloxAccountOutcome.Success
                    ? "Rename request accepted. Fetch owned private servers again to confirm the remote name. No subscription or access setting was changed."
                    : "Rename was not confirmed. Fetch the owned-server list again before retrying; no automatic retry was made.";
            });
        }
        finally { _readingSignedServers=false; NotifySignedServerAvailability(); }
    }
    private void ClearSignedServerObservations()
    {
        _signedServerEpoch++; _privateRenameReview=null; _signedServerContext=null; _signedServerQuery=null;
        _signedServerCursor=null; _signedServerCursors.Clear(); _signedPublicServers=[]; _ownedPrivateServers=[];
        _selectedSignedServer=null; _selectedOwnedServer=null; _privateServerName="";
        _signedServerStatus="No signed-in server observations retained.";
        // A token subscriber can throw after the stop is set. That must not skip the account
        // details/friendship clears owned by our caller or the remaining notifications.
        if (_readingSignedServers)
            try { _authorizationStop?.Cancel(); }
            catch(Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.SignIn,error.GetType()); }
        foreach(string name in new[]{nameof(SignedPublicServers),nameof(OwnedPrivateServers),nameof(SelectedSignedServer),nameof(SelectedOwnedServer),nameof(PrivateServerName),nameof(SignedServerStatus),nameof(CanNextSignedServers)})
            try { Raise(name); } catch(Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.SignIn,error.GetType()); }
    }
    private void NotifySignedServerAvailability() { Raise(nameof(CanBrowseSignedServers)); Raise(nameof(CanNextSignedServers)); }
    private void RememberServerCooldown(SignedInServerResult result)
    {
        if(result.RetryAfter is { } wait && wait>TimeSpan.Zero)
        {
            var now=DateTimeOffset.UtcNow;
            _signedServersNotBefore=wait>=DateTimeOffset.MaxValue-now?DateTimeOffset.MaxValue:now+wait;
        }
    }
}
