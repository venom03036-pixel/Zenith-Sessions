using System.Collections.ObjectModel;
using System.Globalization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>
/// UI-context owner of the private-server tab: one explicit share-link check with the selected account's saved
/// session, the result row, saving a private server and the saved list. Codes live only in protected holders; the
/// result's code is disposed when replaced, cancelled or disposed. No automatic requests, retries or timers.
/// </summary>
public sealed class ShareLinkViewModel:Observable,IAsyncDisposable
{
    public const string LauncherNote="Joining private servers needs launcher support (coming next)";
    private const string IdleStatus="Paste a private server or game share link, then choose Check link.";
    private const string AccountChangedStatus="Share link check stopped because the selected account changed. Paste the link again to check it with the selected account.";
    private const string OwnerStoppedStatus="Share link check stopped because the account, or its saved sign-in, was removed. Paste the link again to check it with a signed-in account.";
    private const string NotCheckedStatus="A previous share link check is still running or stopping. This link was not checked; paste it again when it finishes.";
    private const string NotCheckedNote="The share link pasted while this check ran was not checked; paste it again to check it.";
    private readonly IRobloxShareLinkResolver? _resolver;
    private readonly IPrivateServerStore? _store;
    private readonly ServerBrowserViewModel _browser;
    private readonly IPublicRobloxGameDirectory? _games;
    private readonly Func<Guid?> _selectedProfile;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failed;
    private readonly Func<Guid,CancellationToken,Task>? _sessionRejected;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly List<Task> _operations=[];
    private CancellationTokenSource? _stop,_storeStop;
    private Task _active=Task.CompletedTask;
    private Task? _dispose;
    private long _generation,_storeGeneration;
    private Guid? _checkProfile; // The account whose saved session the running check uses, while its answer may still be shown.
    private bool _busy,_storeBusy,_disposed;
    private bool _refusedWhileChecking; // A routed link was refused while the running check ran, so that check's answer must not pass for it.
    private string _input="",_saveName="",_status=IdleStatus,_resultTitle="",_resultDetail="";
    private string? _resultGame; // The checked game's public name, kept with a saved server so the list shows the game, not its place ID.
    private RobloxShareLinkResolution? _result;
    private SavedPrivateServer? _selectedSaved;
    public ObservableCollection<SavedPrivateServer> Saved {get;}=[];
    public string LinkInput {get=>_input;set {if(value is null || value.Length>RobloxShareLinkParser.MaximumInputLength) return;if(Set(ref _input,value)) Raise(nameof(CanCheck));}}
    public string SaveName {get=>_saveName;set {if(value is null || value.Length>SavedPrivateServer.MaximumNameLength) return;Set(ref _saveName,value);}}
    public string Status=>_status;
    public bool IsBusy=>_busy || _storeBusy;
    public bool CanCheck=>Allowed && !_busy && !string.IsNullOrWhiteSpace(_input);
    public bool HasResult=>_result is not null;
    public bool HasPrivateResult=>_result?.Outcome==RobloxShareLinkOutcome.PrivateServer;
    public string ResultTitle=>_resultTitle;
    public string ResultDetail=>_resultDetail;
    /// <summary>The native handoff accepts only plain game and server joins today; RequestPrivateGame is its own reviewed change.</summary>
    public bool CanJoinPrivate=>false;
    public string JoinNote=>LauncherNote;
    public bool CanSave=>Allowed && !_busy && !_storeBusy && HasPrivateResult && _store is not null;
    public SavedPrivateServer? SelectedSaved {get=>_selectedSaved;set {if(Set(ref _selectedSaved,value)) Raise(nameof(CanRemoveSaved));}}
    public bool CanRemoveSaved=>Allowed && !_storeBusy && _selectedSaved is { } selected && Saved.Contains(selected);
    /// <summary>The check and every saved-server operation still running, including fenced ones; joined on dispose.</summary>
    public Task ActiveOperation {get {_operations.RemoveAll(task=>task.IsCompleted);return _operations.Count==0?Task.CompletedTask:Task.WhenAll(_operations);}}
    public AsyncCommand Check {get;}
    public AsyncCommand SaveAsFavorite {get;}
    public AsyncCommand Remove {get;}
    public AsyncCommand RefreshSaved {get;}
    private bool Allowed=>!_disposed && !_lifetime.IsCancellationRequested && _canUse();

    public ShareLinkViewModel(IRobloxShareLinkResolver? resolver,IPrivateServerStore? store,ServerBrowserViewModel browser,Func<Guid?> selectedProfile,
        Func<bool> canUse,Action<Exception>? failed=null,IPublicRobloxGameDirectory? games=null,Func<Guid,CancellationToken,Task>? sessionRejected=null)
    {
        ArgumentNullException.ThrowIfNull(browser);ArgumentNullException.ThrowIfNull(selectedProfile);ArgumentNullException.ThrowIfNull(canUse);
        _resolver=resolver;_store=store;_browser=browser;_selectedProfile=selectedProfile;_canUse=canUse;_failed=failed??(_=>{});_games=games;
        _sessionRejected=sessionRejected;
        Check=new(_=>CheckAsync(),ReportFailure);SaveAsFavorite=new(_=>SaveAsFavoriteAsync(),ReportFailure);
        Remove=new(_=>RemoveSelectedAsync(),ReportFailure);RefreshSaved=new(_=>LoadSavedAsync(),ReportFailure);
    }

    /// <summary>Parse the box and check it. Only a roblox.com/share link starts a request; anything else is refused without clearing the box.</summary>
    public Task CheckAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(!Allowed || _busy || !_active.IsCompleted) return Task.CompletedTask;
        if(!RobloxShareLinkParser.TryParse(_input,out var link)) {SetStatus("Paste a roblox.com/share link.");return Task.CompletedTask;}
        return CheckAsync(link!,ct);
    }
    /// <summary>
    /// Check an already parsed link (also the Find box route). Takes ownership of the link; the box is cleared before any request.
    /// Lock, Panic and disposal refuse it silently. While a check runs or is still stopping, the Find box cannot see that, so the
    /// link is refused with a status that says it was not checked, instead of being dropped under a stale "Checking…" line.
    /// </summary>
    public Task CheckAsync(RobloxShareLink link,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(link);
        if(!Allowed) {link.Dispose();return Task.CompletedTask;}
        if(_busy || !_active.IsCompleted) {link.Dispose();_refusedWhileChecking=true;SetStatus(NotCheckedStatus);return Task.CompletedTask;}
        ClearInput();
        if(_selectedProfile() is not { } profile || profile==Guid.Empty) {link.Dispose();SetStatus("Select a signed-in account first. Share links are checked with that account.");return Task.CompletedTask;}
        if(_resolver is null) {link.Dispose();SetStatus("Share links cannot be checked in this build.");return Task.CompletedTask;}
        ClearResult();
        // What the user had asked for when the check started: a game result may replace only this, never a newer choice or draft.
        var requested=new RequestedSnapshot(_browser.Destination,_browser.LinkInput);
        var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,ct);
        long generation=Interlocked.Increment(ref _generation);_stop=stop;_busy=true;_checkProfile=profile;_refusedWhileChecking=false;_status="Checking the share link with the selected account…";
        Raise(nameof(Status));Raise(nameof(IsBusy));NotifyAvailability();
        _active=RunCheckAsync(link,profile,requested,generation,stop);Track(_active);return _active;
    }
    private sealed record RequestedSnapshot(RobloxRequestedDestination? Destination,string Draft);
    private void Track(Task operation) {_operations.RemoveAll(task=>task.IsCompleted);_operations.Add(operation);}
    private async Task RunCheckAsync(RobloxShareLink link,Guid profile,RequestedSnapshot requested,long generation,CancellationTokenSource stop)
    {
        RobloxShareLinkResolution? resolution=null;
        try
        {
            bool invite=link.Type is RobloxShareLinkType.ExperienceInvite or RobloxShareLinkType.NotificationExperienceInvite;
            try {resolution=await _resolver!.ResolveShareLinkAsync(profile,link,stop.Token);}
            finally {link.Dispose();}
            if(MayPublish(profile,generation,stop))
            {
                bool rejected=resolution.Outcome==RobloxShareLinkOutcome.SignInRequired;
                await PublishAsync(resolution,invite,profile,requested,generation,stop);
                // The answer is for the earlier link: keep saying the refused one was not checked, so it is not saved as that link.
                if(_refusedWhileChecking && Current(generation,stop)) SetStatus(_status+" "+NotCheckedNote);
                if(rejected) await ReportSessionRejectedAsync(profile);
            }
        }
        // The link service ends the check itself when the shell removes its account, or that account's saved sign-in: the list
        // then reloads with nothing selected, which SelectedAccountChanged rightly ignores, so the status is set here. This view
        // model's own stops (another account, Cancel, disposal) have already said what happened, and Current is false then.
        catch(OperationCanceledException) {if(Current(generation,stop)) SetStatus(OwnerStoppedStatus);}
        catch(Exception error) {if(Current(generation,stop)) SetStatus("The share link could not be checked right now. Try again later.");ReportSafeFailure(error);}
        finally
        {
            if(!ReferenceEquals(resolution,_result)) resolution?.Dispose(); // Only a shown result keeps its code; every other exit, cancellation included, zeroes it.
            if(ReferenceEquals(_stop,stop)) _stop=null;stop.Dispose();_busy=false;_checkProfile=null;Raise(nameof(IsBusy));NotifyAvailability();
        }
    }
    private async Task PublishAsync(RobloxShareLinkResolution resolution,bool invite,Guid profile,RequestedSnapshot requested,long generation,CancellationTokenSource stop)
    {
        if(resolution.Outcome is not (RobloxShareLinkOutcome.Experience or RobloxShareLinkOutcome.PrivateServer or RobloxShareLinkOutcome.ServerInstance)
            || resolution.PlaceId is not { } placeId)
        {
            SetStatus(resolution.Outcome switch
            {
                RobloxShareLinkOutcome.SignInRequired=>"Sign in with the selected account to check share links.",
                RobloxShareLinkOutcome.Expired=>"This share link has expired. Ask for a new one.",
                RobloxShareLinkOutcome.Invalid=>"This share link is not valid for a game or private server.",
                RobloxShareLinkOutcome.Busy=>"Another account operation is running. Try again when it finishes.",
                RobloxShareLinkOutcome.NotListed=>"This link's game is restricted or not publicly listed, so it cannot be opened from Zenith.",
                RobloxShareLinkOutcome.Cancelled=>"Share link check cancelled.",
                _=>"The share link could not be checked right now. Try again later."
            });
            return;
        }
        string? name=await LookupNameAsync(placeId,stop.Token);
        if(!MayPublish(profile,generation,stop)) return;
        string place=placeId.ToString(CultureInfo.InvariantCulture);
        _result=resolution;_resultGame=name;_resultTitle=name is null?"Place "+place:name+" · Place "+place;
        // Same fence as the Find box (DestinationSearchViewModel): a late result never overwrites a newer destination or erases a draft.
        bool applies=StillRequested(requested);
        if(resolution.Outcome==RobloxShareLinkOutcome.ServerInstance && applies)
        {
            // An invite names the inviter's server: a request for that exact server, never proof of room or arrival.
            _browser.UseInvitedServer(placeId,resolution.JobId!);
            _resultDetail="Server invite. The inviter's server is now the requested destination for the accounts you launch; nothing has launched.";
            _status="Invite checked. The inviter's server is the requested destination; Roblox decides whether it still has room.";
        }
        else if(resolution.Outcome==RobloxShareLinkOutcome.Experience && invite && applies)
        {
            _browser.UsePreferredPlace(placeId);
            _resultDetail="Game invite. It no longer names a server you can join, so the game is the requested destination; nothing has launched.";
            _status="Invite checked. Its server cannot be joined, so the game is the requested destination; choose Quick join or Browse servers.";
        }
        else if(resolution.Outcome==RobloxShareLinkOutcome.Experience && applies)
        {
            _browser.UsePreferredPlace(placeId);
            _resultDetail="Game share link. It is now the requested destination for the accounts you launch; nothing has launched.";
            _status="Share link checked. The game is the requested destination; choose Quick join or Browse servers.";
        }
        else if(resolution.Outcome is RobloxShareLinkOutcome.Experience or RobloxShareLinkOutcome.ServerInstance)
        {
            string kind=resolution.Outcome==RobloxShareLinkOutcome.ServerInstance?"Server invite":invite?"Game invite":"Game share link";
            _resultDetail=kind+". It was not applied because you changed the destination while it was checked; nothing has launched.";
            _status="Share link checked. You picked another game or typed in the Find box while it ran, so your newer choice was kept. Check the link again to use this game.";
        }
        else
        {
            var code=resolution.PrivateServerCode!;
            _resultDetail="Private server · Link code …"+code.Last4
                +(resolution.OwnerUserId is { } owner?" · Owner "+owner.ToString(CultureInfo.InvariantCulture):"")
                +(resolution.UniverseId is { } universe?" · Universe "+universe.ToString(CultureInfo.InvariantCulture):"");
            _status="Private server link checked. Save it as a favorite; joining needs launcher support that is coming next.";
        }
        Raise(nameof(HasResult));Raise(nameof(HasPrivateResult));Raise(nameof(ResultTitle));Raise(nameof(ResultDetail));Raise(nameof(Status));NotifyAvailability();
    }
    /// <summary>
    /// The service marks a saved session Roblox refused as needing sign-in. Ask the owner to reload the account so its
    /// row and launch button agree with this panel. Awaited inside the tracked check, so disposal joins it; a reload
    /// failure keeps the sign-in status and reaches diagnostics only as the fixed application-owned failure.
    /// </summary>
    private async Task ReportSessionRejectedAsync(Guid profile)
    {
        if(_sessionRejected is null) return;
        try {await _sessionRejected(profile,_lifetime.Token);}
        catch(OperationCanceledException) { }
        catch(Exception error) {ReportSafeFailure(error);}
    }
    /// <summary>
    /// Whether the check may still show its answer. It used the saved session of the account selected when it started; if
    /// another account, or none, is selected now, the check stops here and says so instead of answering under that selection.
    /// </summary>
    private bool MayPublish(Guid profile,long generation,CancellationTokenSource stop)
    {
        if(!Current(generation,stop)) return false;
        if(_selectedProfile()==profile) return true;
        StopForAccountChange();return false;
    }
    private async Task<string?> LookupNameAsync(long placeId,CancellationToken ct)
    {
        if(_games is null) return null;
        try
        {
            var result=await _games.LookupGameAsync(placeId,ct);
            return result.State==PublicDirectoryState.Success && result.Value is { } game?game.Name:null;
        }
        catch(OperationCanceledException) {throw;}
        catch(Exception) {return null;} // The name is decoration; the check result stands without it.
    }

    /// <summary>Save the checked private server. The save works on its own copy of the code, so leaving the page cannot clear it mid-write.</summary>
    public Task SaveAsFavoriteAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(!Allowed || _busy || _storeBusy) return Task.CompletedTask;
        if(_result is not {Outcome:RobloxShareLinkOutcome.PrivateServer,PrivateServerCode:{ } code,PlaceId:{ } placeId,UniverseId:{ } universeId} result)
        {SetStatus("Check a private server link first.");return Task.CompletedTask;}
        if(_store is not { } store) {SetStatus("Saved private servers are unavailable in this build.");return Task.CompletedTask;}
        string name=SavedPrivateServer.NormalizeName(_saveName);
        if(name.Length==0) name=_resultTitle.Length==0?"Private server":_resultTitle.Split(" · ")[0];
        long? owner=result.OwnerUserId;string? game=SavedPrivateServer.NormalizeGameName(_resultGame);ProtectedPrivateServerCode? copy=null;
        code.Reveal(span=>copy=new ProtectedPrivateServerCode(span)); // UI thread, like ClearResult: no torn copy is possible.
        return StartStore(async(token,current)=>
        {
            try
            {
                SavedPrivateServer saved;
                try {saved=await store.AddAsync(name,placeId,universeId,owner,game,copy!,token);}
                catch(ArgumentException) {if(current()) SetStatus("Choose a short plain name without links or codes.");return;}
                catch(PrivateServerDuplicateException duplicate)
                {
                    if(!current()) return;
                    // Point at the row already kept, when the list shows it, instead of adding a second copy of the same link.
                    if(Saved.FirstOrDefault(server=>server.Id==duplicate.Existing.Id) is { } row) SelectedSaved=row;
                    SetStatus("This private server is already saved as “"+duplicate.Existing.Name+"”. Nothing was added.");return;
                }
                catch(PrivateServerCapacityException) {if(current()) SetStatus("The 100-server limit for saved private servers is reached. Remove one before saving another.");return;}
                catch(PrivateServerStorageException) {if(current()) SetStatus("Saved private servers could not be written. Existing files were preserved.");return;}
                if(!current()) return;
                Saved.Add(saved);SaveName="";SetStatus("Private server saved as a favorite. Only the last four characters of its code are ever shown.");
            }
            finally {copy!.Dispose();}
        },ct);
    }

    public Task RemoveSelectedAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(!CanRemoveSaved || _store is not { } store || _selectedSaved is not { } selected) return Task.CompletedTask;
        return StartStore(async(token,current)=>
        {
            try {await store.DeleteAsync(selected.Id,selected.Revision,token);}
            catch(PrivateServerConflictException) {if(current()) SetStatus("This saved private server changed elsewhere. Refresh the list before removing it.");return;}
            catch(PrivateServerStorageException) {if(current()) SetStatus("Saved private servers could not be written. Existing files were preserved.");return;}
            if(!current()) return;
            Saved.Remove(selected);SelectedSaved=null;SetStatus("Saved private server removed. Its code was deleted from this PC.");
        },ct);
    }

    /// <summary>List the saved private servers (metadata only; codes stay in the store). Safe to discard: failures become a status.</summary>
    public Task LoadSavedAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(!Allowed || _storeBusy || _store is not { } store) return Task.CompletedTask;
        return StartStore(async(token,current)=>
        {
            IReadOnlyList<SavedPrivateServer> servers;
            try {servers=await store.ReadAsync(token);}
            catch(PrivateServerStorageException) {if(current()) SetStatus("Saved private servers could not be read. Existing files were preserved; review the data folder.");return;}
            if(!current()) return;
            Saved.Clear();foreach(var server in servers) Saved.Add(server);
            SelectedSaved=null;
        },ct,"Saved private servers could not be read right now. Choose Refresh list to try again.");
    }

    /// <summary>
    /// One saved-server operation at a time, owned and tracked by this view model: cancelled by Cancel (leave page,
    /// Lock, Panic) and disposal, fenced by its own generation so a late result never repopulates a page the user
    /// left, and joined by DisposeAsync before the shell releases the store. Unexpected failures become a status and a
    /// fixed application-owned diagnostic, so a discarded task never faults.
    /// </summary>
    private Task StartStore(Func<CancellationToken,Func<bool>,Task> operation,CancellationToken ct,
        string failureStatus="Saved private servers could not be updated right now. Try again.")
    {
        var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,ct);
        long generation=Interlocked.Read(ref _storeGeneration);
        _storeStop=stop;_storeBusy=true;Raise(nameof(IsBusy));NotifyAvailability();
        var run=RunStoreAsync(operation,generation,stop,failureStatus);Track(run);return run;
    }
    private async Task RunStoreAsync(Func<CancellationToken,Func<bool>,Task> operation,long generation,CancellationTokenSource stop,string failureStatus)
    {
        bool Current()=>Allowed && generation==Interlocked.Read(ref _storeGeneration) && !stop.IsCancellationRequested;
        try {await operation(stop.Token,Current);}
        catch(OperationCanceledException) { }
        catch(Exception error) {if(Current()) SetStatus(failureStatus);ReportSafeFailure(error);}
        finally
        {
            if(ReferenceEquals(_storeStop,stop)) {_storeStop=null;_storeBusy=false;}
            stop.Dispose();Raise(nameof(IsBusy));NotifyAvailability();
        }
    }

    /// <summary>
    /// The selected account changed. A check uses the saved session of the account selected when it started, so selecting a
    /// different account cancels it now and its answer is never shown. Re-selecting the same account (its row was reloaded)
    /// or the momentary empty selection while the list reloads keeps it running; it still answers only under its own account.
    /// </summary>
    public void SelectedAccountChanged()
    {
        if(_checkProfile is not { } running || _selectedProfile() is not { } selected || selected==running) return;
        StopForAccountChange();
    }
    private void StopForAccountChange()
    {
        _checkProfile=null;Interlocked.Increment(ref _generation);try {_stop?.Cancel();} catch(ObjectDisposedException) { }
        SetStatus(AccountChangedStatus);
    }

    /// <summary>Leaving the page, Lock or Panic: cancel a running check and saved-server operation, and clear the box, the result (its code) and the list.</summary>
    public void Cancel()
    {
        _checkProfile=null;Interlocked.Increment(ref _generation);try {_stop?.Cancel();} catch(ObjectDisposedException) { }
        Interlocked.Increment(ref _storeGeneration);try {_storeStop?.Cancel();} catch(ObjectDisposedException) { }
        if(_storeStop is not null) {_storeStop=null;_storeBusy=false;Raise(nameof(IsBusy));} // The cancelled operation is fenced and disposes its own source.
        ClearInput();ClearResult();Saved.Clear();SelectedSaved=null;
        _status=IdleStatus;Raise(nameof(Status));NotifyAvailability();
    }
    private void ClearInput() {if(_input.Length!=0) {_input="";Raise(nameof(LinkInput));Raise(nameof(CanCheck));}}
    private void ClearResult()
    {
        _result?.Dispose();_result=null;_resultGame=null;_resultTitle="";_resultDetail="";
        Raise(nameof(HasResult));Raise(nameof(HasPrivateResult));Raise(nameof(ResultTitle));Raise(nameof(ResultDetail));
    }
    private bool Current(long generation,CancellationTokenSource stop)=>Allowed && generation==Interlocked.Read(ref _generation) && !stop.IsCancellationRequested;
    /// <summary>Any re-pick creates a new destination object, so even choosing the same game again counts as a newer choice.</summary>
    private bool StillRequested(RequestedSnapshot requested)=>ReferenceEquals(_browser.Destination,requested.Destination)
        && string.Equals(_browser.LinkInput,requested.Draft,StringComparison.Ordinal);
    private void SetStatus(string status) {_status=status;Raise(nameof(Status));}
    private void NotifyAvailability() {Raise(nameof(CanCheck));Raise(nameof(CanSave));Raise(nameof(CanRemoveSaved));}
    private void ReportFailure(Exception error) {if(!_disposed) SetStatus("The share link operation could not finish. Try again.");ReportSafeFailure(error);}
    private void ReportSafeFailure(Exception error)
    {
        _=error; // Provider or storage details could quote a code; only the fixed application-owned failure reaches diagnostics.
        try {_failed(new InvalidOperationException("The share link operation failed."));} catch(Exception) { /* Diagnostics never become a second owner. */ }
    }
    public ValueTask DisposeAsync()
    {
        if(_dispose is not null) return new(_dispose);
        _disposed=true;_dispose=StopAsync(_operations.ToArray());
        try {_lifetime.Cancel();} catch(ObjectDisposedException) { }
        Cancel();
        return new(_dispose);
    }
    /// <summary>Joins the check and every saved-server operation, so the shell may release the store afterwards.</summary>
    private async Task StopAsync(Task[] operations)
    {
        try {foreach(var operation in operations) {try {await operation;} catch(OperationCanceledException) { }}}
        finally {_operations.Clear();_lifetime.Dispose();}
    }
}
