using System.Collections.ObjectModel;
using System.IO;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>Explicit metadata operations only. The caller owns the store and browser; no polling, launch or HTTP is performed.</summary>
public sealed class FavoriteDestinationsViewModel : Observable,IAsyncDisposable
{
    private readonly IFavoriteDestinationStore _store;
    private readonly ServerBrowserViewModel _browser;
    private readonly Func<bool> _canUse;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _operationStop;
    private Task _active=Task.CompletedTask;
    private Task? _dispose;
    private long _generation;
    private bool _busy;
    private string _name="",_status="Save a named game or server request for later. Favorites do not confirm availability or arrival.";
    private FavoriteDestination? _selected;
    public ObservableCollection<FavoriteDestination> Favorites {get;}=[];
    public FavoriteDestination? SelectedFavorite
    {
        get=>_selected;
        set {if(Set(ref _selected,value)) {NameInput=value?.Name??"";Raise(nameof(CanUseFavorite));}}
    }
    public string NameInput
    {
        get=>_name;
        set
        {
            if(value is null || value.Length>FavoriteDestination.MaximumNameLength) {Status="Favorite names are limited to 80 characters.";return;}
            Set(ref _name,value);
        }
    }
    public string Status {get=>_status;private set=>Set(ref _status,value);}
    public bool IsBusy {get=>_busy;private set {if(Set(ref _busy,value)) Raise(nameof(CanUseFavorite));}}
    public bool CanUseFavorite=>CanStart() && !IsBusy && SelectedFavorite is { } selected && Favorites.Contains(selected);
    public Task ActiveOperation=>_active;
    public AsyncCommand Refresh {get;}
    public AsyncCommand SaveCurrent {get;}
    public AsyncCommand Rename {get;}
    /// <summary>The host presents its confirmation before invoking this command or DeleteSelectedAsync.</summary>
    public AsyncCommand Delete {get;}
    public Command Use {get;}
    /// <summary>Chip click: select the given saved favorite and use it. Only a listed favorite is accepted.</summary>
    public Command UseFavorite {get;}

    public FavoriteDestinationsViewModel(IFavoriteDestinationStore store,ServerBrowserViewModel browser,Func<bool>? canUse=null)
    {
        ArgumentNullException.ThrowIfNull(store);ArgumentNullException.ThrowIfNull(browser);
        _store=store;_browser=browser;_canUse=canUse??(()=>true);
        Refresh=new(_=>LoadAsync(),ReportFailure);SaveCurrent=new(_=>SaveCurrentDestinationAsync(NameInput),ReportFailure);
        Rename=new(_=>RenameSelectedAsync(NameInput),ReportFailure);Delete=new(_=>DeleteSelectedAsync(),ReportFailure);
        Use=new(_=>UseSelectedFavorite());
        UseFavorite=new(parameter=>{if(parameter is FavoriteDestination favorite && Favorites.Contains(favorite)) {SelectedFavorite=favorite;UseSelectedFavorite();}});
    }
    public Task LoadAsync(CancellationToken ct=default)=>StartAsync(async token=>
    {
        var snapshot=await _store.LoadAsync(token);return new Completion(snapshot,SelectedFavorite?.Id,"Favorites loaded. Select one and use it to set a requested destination.");
    },ct);
    public Task SaveCurrentDestinationAsync(string name,CancellationToken ct=default)
    {
        if(!CanStart()) return Task.CompletedTask;
        if(_browser.ReadyDestination is not { } target)
        {
            Status=_browser.HasDestinationDraft?"Use or discard the pending destination before saving a favorite."
                :"Choose a valid requested game or public server before saving a favorite.";
            return Task.CompletedTask;
        }
        FavoriteDestination draft;
        try {draft=new FavoriteDestination(Guid.NewGuid(),name,target.PlaceId,target.JobId).Validated();}
        catch(ArgumentException ex) {ReportFailure(ex);return Task.CompletedTask;}
        return StartAsync(async token=>
        {
            var saved=await _store.SaveAsync(draft,token);token.ThrowIfCancellationRequested();
            var snapshot=await _store.LoadAsync(token);return new Completion(snapshot,saved.Id,"Favorite saved. This is a requested destination; no account was launched or joined.");
        },ct);
    }
    public Task RenameSelectedAsync(string name,CancellationToken ct=default)
    {
        if(!CanStart()) return Task.CompletedTask;
        if(SelectedFavorite is not { } selected || !Favorites.Contains(selected)) {Status="Select a saved favorite before renaming it.";return Task.CompletedTask;}
        FavoriteDestination draft;
        try {draft=(selected with {Name=name}).Validated();}
        catch(ArgumentException ex) {ReportFailure(ex);return Task.CompletedTask;}
        return StartAsync(async token=>
        {
            var saved=await _store.SaveAsync(draft,token);token.ThrowIfCancellationRequested();
            var snapshot=await _store.LoadAsync(token);return new Completion(snapshot,saved.Id,"Favorite renamed. Its saved destination and identity are unchanged.");
        },ct);
    }
    public Task DeleteSelectedAsync(CancellationToken ct=default)
    {
        if(!CanStart()) return Task.CompletedTask;
        if(SelectedFavorite is not { } selected || !Favorites.Contains(selected)) {Status="Select a saved favorite before removing it.";return Task.CompletedTask;}
        return StartAsync(async token=>
        {
            await _store.DeleteAsync(selected.Id,selected.Revision,selected.ChangeId,token);token.ThrowIfCancellationRequested();
            var snapshot=await _store.LoadAsync(token);return new Completion(snapshot,null,"Favorite removed. The requested destination is unchanged; no client was changed.");
        },ct);
    }
    public void UseSelectedFavorite()
    {
        if(!CanStart() || !CanUseFavorite || SelectedFavorite is not { } selected) return;
        if(_browser.UseFavoriteDestination(selected)) Status="Favorite selected as a request. No server lookup, account launch or join was performed.";
    }
    private sealed record Completion(FavoriteDestinationSnapshot Snapshot,Guid? SelectedId,string Message);
    private bool CanStart()=>!_lifetime.IsCancellationRequested && _dispose is null && _canUse();
    private Task StartAsync(Func<CancellationToken,Task<Completion>> work,CancellationToken ct)
    {
        if(!CanStart()) return Task.CompletedTask;
        if(IsBusy) {Status="A favorites operation is still running or stopping. Wait for it to finish before another request.";return Task.CompletedTask;}
        ct.ThrowIfCancellationRequested();long generation=_generation;
        var stop=CancellationTokenSource.CreateLinkedTokenSource(ct,_lifetime.Token);_operationStop=stop;IsBusy=true;
        Status="Loading or saving favorite destinations…";
        return _active=RunAsync(work,generation,stop);
    }
    private async Task RunAsync(Func<CancellationToken,Task<Completion>> work,long generation,CancellationTokenSource stop)
    {
        try
        {
            var completion=await work(stop.Token);
            if(stop.IsCancellationRequested)
            {
                if(generation==_generation && CanStart()) Status="Favorites work was cancelled. A completed save may already be durable; refresh favorites before continuing.";
                return;
            }
            if(generation!=_generation || !CanStart()) return;
            var snapshot=completion.Snapshot;
            if(snapshot is null || snapshot.Revision<1 || snapshot.Destinations is null || snapshot.Destinations.Count>FavoriteDestination.MaximumCount)
                throw new InvalidDataException();
            var rows=snapshot.Destinations.Select(item=>item.Validated()).ToArray();
            if(rows.Any(item=>item.Revision<1 || item.Revision>snapshot.Revision) || rows.Select(item=>item.Id).Distinct().Count()!=rows.Length) throw new InvalidDataException();
            Array.Sort(rows,(left,right)=>{int name=StringComparer.OrdinalIgnoreCase.Compare(left.Name,right.Name);return name!=0?name:left.Id.CompareTo(right.Id);});
            // The caller's UI context is retained. No cancelled or locked continuation may restore the list.
            if(stop.IsCancellationRequested || generation!=_generation || !CanStart()) return;
            Favorites.Clear();foreach(var row in rows) Favorites.Add(row);
            SelectedFavorite=rows.FirstOrDefault(item=>item.Id==completion.SelectedId);
            if(SelectedFavorite is null) NameInput="";
            Status=snapshot.Notice is null?completion.Message:"Favorites loaded from a preserved copy or an interrupted save. Original files were preserved; review recovery before another change.";
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested)
        {
            if(generation==_generation && CanStart()) Status="Favorites work was cancelled. A completed save may already be durable; refresh favorites before continuing.";
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or FavoriteDestinationConflictException or FavoriteDestinationCapacityException)
        {if(generation==_generation && !stop.IsCancellationRequested && CanStart()) ReportFailure(ex);}
        finally
        {
            if(ReferenceEquals(_operationStop,stop)) _operationStop=null;
            stop.Dispose();IsBusy=false;
        }
    }
    private void ReportFailure(Exception error)=>Status=error switch
    {
        FavoriteDestinationConflictException=>"This favorite changed elsewhere. Refresh favorites before saving or removing it.",
        FavoriteDestinationCapacityException=>"The 500-destination favorites storage limit is reached. Remove a favorite before adding another; saved accounts are not limited.",
        FavoriteDestinationStorageException {Problem:FavoriteDestinationStorageProblem.UnsupportedVersion}=>"Favorites use an unsupported storage version. Existing files were preserved; use a compatible app version.",
        FavoriteDestinationStorageException {Problem:FavoriteDestinationStorageProblem.RecoveryBlocked}=>"Favorites need manual file recovery. The damaged-original recovery slot is occupied or too large; existing files were preserved.",
        ArgumentException=>"Use a name of 1 to 80 characters without controls, links, access codes or credentials.",
        _=>"Favorites could not be read or saved. Existing files were preserved; check the data folder and refresh before retrying."
    };
    public void CancelWork(bool clear=true)
    {
        Interlocked.Increment(ref _generation);_operationStop?.Cancel();
        if(clear) {Favorites.Clear();SelectedFavorite=null;NameInput="";}
        Status="Favorites work cancelled. Saved favorites remain on disk; no client was changed.";
    }
    public ValueTask DisposeAsync()=>new(_dispose??=StopAsync());
    private async Task StopAsync()
    {
        _lifetime.Cancel();CancelWork();
        // File-system calls cannot promise a hard stop deadline. Keep ownership until actual work ends.
        try {await _active;}
        catch(OperationCanceledException) { }
        finally {_lifetime.Dispose();}
    }
}
