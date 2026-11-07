using System.Collections.ObjectModel;
using System.Globalization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed class RobloxFavoriteChip(PublicRobloxFavoriteGame game)
{
    public PublicRobloxFavoriteGame Game {get;}=game;
    public string Name=>Game.Name;
    public string AutomationName=>Name+". Public Roblox favorite of the selected account; not verified access or arrival.";
}

/// <summary>
/// UI-context owner of the selected account's public Roblox favorites: one page per visit, loaded once when Join a
/// game opens or the account changes, asked again only on an explicit refresh. Directory and browser are borrowed;
/// a page for a previous account or visit is fenced out. No sign-in, retries, timers or cache.
/// </summary>
public sealed class RobloxFavoriteGamesViewModel:Observable,IAsyncDisposable
{
    public const int VisibleChips=6;
    private readonly IPublicRobloxFavoriteGamesDirectory? _directory;
    private readonly ServerBrowserViewModel _browser;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failed;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly List<RobloxFavoriteChip> _all=[];
    private readonly List<Task> _runs=[];
    private CancellationTokenSource? _stop;
    private Task? _dispose;
    private long _generation;
    private bool _busy,_showAll,_disposed;
    private AccountKey? _account,_loaded;
    private string _accountName="";
    private string _status="Select an account to show its Roblox favorites.";
    public ObservableCollection<RobloxFavoriteChip> Chips {get;}=[];
    public string Status=>_status;
    public string HeaderLabel=>_accountName.Length==0?"On Roblox":"On Roblox · "+_accountName;
    public bool IsBusy=>_busy;
    public bool HasChips=>Chips.Count>0;
    public bool HasMore=>_all.Count>VisibleChips;
    public int MoreCount=>_all.Count>Chips.Count?_all.Count-Chips.Count:0;
    public string MoreLabel=>_showAll?"Show fewer":"+"+MoreCount.ToString(CultureInfo.InvariantCulture)+" more";
    /// <summary>Every tracked request, including fenced ones still unwinding; completes once all have finished.</summary>
    public Task ActiveOperation {get {PruneRuns();return _runs.Count==0?Task.CompletedTask:Task.WhenAll(_runs);}}
    public AsyncCommand Refresh {get;}
    public Command Use {get;}
    public Command ShowAll {get;}
    private bool Allowed=>!_disposed && !_lifetime.IsCancellationRequested && _canUse();

    public RobloxFavoriteGamesViewModel(IPublicRobloxFavoriteGamesDirectory? directory,ServerBrowserViewModel browser,Func<bool> canUse,Action<Exception>? failed=null)
    {
        ArgumentNullException.ThrowIfNull(browser);ArgumentNullException.ThrowIfNull(canUse);
        _directory=directory;_browser=browser;_canUse=canUse;_failed=failed??(_=>{});
        Refresh=new(_=>RefreshAsync(),ReportFailure);Use=new(UseChip);ShowAll=new(_=>ToggleShowAll());
    }

    /// <summary>Bind to the account selected in Accounts. Another account cancels and clears; the same one keeps its page.</summary>
    public void SetAccount(Guid? profileId,long? userId,long? authorizationGeneration,string? accountName)
    {
        AccountKey? key=profileId is { } id?new(id,userId is >0?userId:null,authorizationGeneration is >0?authorizationGeneration:null):null;
        string name=accountName??"";
        if(key==_account && string.Equals(name,_accountName,StringComparison.Ordinal)) return;
        _account=key;_accountName=name;
        Invalidate();Raise(nameof(HeaderLabel));
    }

    /// <summary>
    /// One request per visit for an eligible account; nothing happens while the current account's request is running.
    /// A request fenced by an account change or Cancel does not block the next one; it unwinds as a tracked run.
    /// </summary>
    public Task LoadAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(!Allowed || _busy) return Task.CompletedTask;
        if(_account is not { } account) {SetStatus("Select an account to show its Roblox favorites.");return Task.CompletedTask;}
        if(account.UserId is not { } userId || account.Generation is null) {SetStatus("Sign in to show this account's Roblox favorites");return Task.CompletedTask;}
        if(_directory is null) {SetStatus("Roblox favorites are unavailable in this build.");return Task.CompletedTask;}
        if(_loaded==account) return Task.CompletedTask;
        return Start(account,userId,ct);
    }
    private Task RefreshAsync() {_loaded=null;return LoadAsync();}

    private Task Start(AccountKey account,long userId,CancellationToken caller)
    {
        var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,caller);
        long generation=Interlocked.Increment(ref _generation);
        _stop=stop;_busy=true;_loaded=account;_status="Loading favorites…";
        ClearChips();Raise(nameof(Status));Raise(nameof(IsBusy));
        PruneRuns();var run=RunAsync(account,userId,generation,stop);_runs.Add(run);return run;
    }
    private void PruneRuns()=>_runs.RemoveAll(run=>run.IsCompleted);
    private bool Current(long generation,AccountKey account,CancellationTokenSource stop)
        =>Allowed && generation==Interlocked.Read(ref _generation) && _account==account && !stop.IsCancellationRequested;
    private async Task RunAsync(AccountKey account,long userId,long generation,CancellationTokenSource stop)
    {
        try
        {
            var result=await _directory!.GetFavoriteGamesAsync(userId,stop.Token);
            if(!Current(generation,account,stop)) return;
            if(result.State==PublicDirectoryState.Success && result.Value is { } games)
            {
                _all.Clear();foreach(var game in games) _all.Add(new RobloxFavoriteChip(game));
                _showAll=false;PublishChips();
                _status=_all.Count==0?"No public favorites on this profile."
                    :_all.Count.ToString(CultureInfo.InvariantCulture)+" public favorites on Roblox · newest first";
            }
            else _status=result.State==PublicDirectoryState.Deferred?"Favorites deferred · wait for the Roblox cooldown":"Favorites unavailable";
            Raise(nameof(Status));
        }
        catch(OperationCanceledException) { }
        catch(Exception error) {if(Current(generation,account,stop)) SetStatus("Favorites unavailable");ReportSafeFailure(error);}
        finally
        {
            // Only the current run owns the busy state; a fenced run unwinding late must not end a newer load.
            if(ReferenceEquals(_stop,stop)) {_stop=null;_busy=false;Raise(nameof(IsBusy));}
            stop.Dispose();
        }
    }

    private void UseChip(object? parameter)
    {
        if(parameter is not RobloxFavoriteChip chip || !Allowed || !_all.Contains(chip)) return;
        _browser.UsePreferredPlace(chip.Game.RootPlaceId);
    }
    private void ToggleShowAll() {if(!HasMore && !_showAll) return;_showAll=!_showAll;PublishChips();}
    private void PublishChips()
    {
        Chips.Clear();
        foreach(var chip in _showAll?_all:_all.Take(VisibleChips)) Chips.Add(chip);
        Raise(nameof(HasChips));Raise(nameof(HasMore));Raise(nameof(MoreCount));Raise(nameof(MoreLabel));
    }
    private void ClearChips() {_all.Clear();_showAll=false;PublishChips();}

    /// <summary>Leaving the page, Lock, Panic or an account change: cancel, clear and forget this visit's page.</summary>
    public void Cancel()=>Invalidate();
    private void Invalidate()
    {
        Interlocked.Increment(ref _generation);try {_stop?.Cancel();} catch(ObjectDisposedException) { }
        if(_stop is not null) {_stop=null;_busy=false;Raise(nameof(IsBusy));} // The cancelled run is fenced; it disposes its own source.
        _loaded=null;ClearChips();
        _status=_account is not { } account?"Select an account to show its Roblox favorites."
            :account.UserId is null || account.Generation is null?"Sign in to show this account's Roblox favorites"
            :_directory is null?"Roblox favorites are unavailable in this build.":"Roblox favorites load when Join a game opens.";
        Raise(nameof(Status));
    }
    private void SetStatus(string status) {_status=status;Raise(nameof(Status));}
    private void ReportFailure(Exception error) {if(!_disposed) SetStatus("Favorites unavailable");ReportSafeFailure(error);}
    private void ReportSafeFailure(Exception error)
    {
        _=error; // Provider details never reach diagnostics; only the fixed application-owned failure does.
        try {_failed(new InvalidOperationException("The Roblox favorites operation failed."));} catch(Exception) { /* Diagnostics never become a second owner. */ }
    }
    public ValueTask DisposeAsync()
    {
        if(_dispose is not null) return new(_dispose);
        _disposed=true;_dispose=StopAsync(_runs.ToArray());
        try {_lifetime.Cancel();} catch(ObjectDisposedException) { }
        Invalidate();
        return new(_dispose);
    }
    private async Task StopAsync(Task[] runs)
    {
        try {foreach(var run in runs) {try {await run;} catch(OperationCanceledException) { }}}
        finally {_runs.Clear();_lifetime.Dispose();}
    }
    private readonly record struct AccountKey(Guid ProfileId,long? UserId,long? Generation);
}
