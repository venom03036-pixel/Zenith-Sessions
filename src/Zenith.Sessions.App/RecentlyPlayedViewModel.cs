using System.Collections.ObjectModel;
using System.Globalization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>A place observed in a client log on this PC, with the universe when the log named one. Diagnostic and unverified.</summary>
public sealed record RecentlyPlayedSource(long PlaceId,long? UniverseId,DateTimeOffset ObservedUtc);

public sealed class RecentlyPlayedChip(long placeId):Observable
{
    private string? _name;
    public long PlaceId {get;}=placeId;
    public string Name=>_name??"Place "+PlaceId.ToString(CultureInfo.InvariantCulture);
    public bool HasName=>_name is not null;
    public string AutomationName=>Name+". Recently played, from client logs on this PC; not verified.";
    internal void SetName(string? name) {if(string.Equals(name,_name,StringComparison.Ordinal)) return;_name=name;Raise(nameof(Name));Raise(nameof(HasName));}
}

/// <summary>
/// UI-context owner of the recently played chips: places observed in client logs, newest first, at most six. Names come
/// from one batch request for universes the logs named plus single lookups for the rest, remembered for the visit so no
/// place is asked twice. Directories and browser are borrowed; a newer update fences the previous naming run. No retries,
/// timers or polling; the shell calls <see cref="UpdateAsync"/> on every desktop presentation while the page is open. A
/// refusal made on this PC (both shared request slots busy, a 15 s timeout) is not a Roblox cooldown, but it is not asked
/// again by those updates either: the run ends and its unnamed places wait for an explicit trigger, the page opened again
/// (<see cref="Cancel"/>) or another account chosen there (<see cref="ForgetLocalRefusals"/>). A newly observed place is
/// still asked. A Roblox pause or an unnamed place is not asked again this visit.
/// </summary>
public sealed class RecentlyPlayedViewModel:Observable,IAsyncDisposable
{
    public const int MaximumChips=PublicRobloxGameBatchLimits.MaximumUniverses;
    private readonly IPublicRobloxGameBatchDirectory? _batch;
    private readonly IPublicRobloxGameDirectory? _single;
    private readonly ServerBrowserViewModel _browser;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failed;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly Dictionary<long,string> _names=new();
    private readonly HashSet<long> _attempted=new();
    /// <summary>Places a run left unnamed after a refusal made on this PC; not asked by updates until an explicit trigger.</summary>
    private readonly HashSet<long> _refused=new();
    private readonly List<Task> _runs=[];
    private CancellationTokenSource? _stop;
    private Task _current=Task.CompletedTask;
    private long _generation;
    private bool _disposed;
    public ObservableCollection<RecentlyPlayedChip> Chips {get;}=[];
    public bool HasChips=>Chips.Count>0;
    public Command Use {get;}
    /// <summary>Every tracked naming task; completes once all started runs have finished or been fenced out.</summary>
    public Task ActiveOperation {get {_runs.RemoveAll(run=>run.IsCompleted);return _runs.Count==0?Task.CompletedTask:Task.WhenAll(_runs);}}
    private bool Allowed=>!_disposed && !_lifetime.IsCancellationRequested && _canUse();

    public RecentlyPlayedViewModel(IPublicRobloxGameBatchDirectory? batch,IPublicRobloxGameDirectory? single,ServerBrowserViewModel browser,Func<bool> canUse,Action<Exception>? failed=null)
    {
        ArgumentNullException.ThrowIfNull(browser);ArgumentNullException.ThrowIfNull(canUse);
        _batch=batch;_single=single;_browser=browser;_canUse=canUse;_failed=failed??(_=>{});
        Use=new(UseChip);
    }

    /// <summary>Replace the chips from the latest observations and name the ones not yet named this visit. Returns the tracked naming task.</summary>
    public Task UpdateAsync(IReadOnlyList<RecentlyPlayedSource> sources,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(sources);ct.ThrowIfCancellationRequested();
        if(!Allowed) return Task.CompletedTask;
        var newest=new Dictionary<long,RecentlyPlayedSource>();
        foreach(var source in sources)
        {
            if(source.PlaceId<=0) continue;
            if(!newest.TryGetValue(source.PlaceId,out var current) || source.ObservedUtc>current.ObservedUtc) newest[source.PlaceId]=source;
        }
        var ordered=newest.Values.OrderByDescending(source=>source.ObservedUtc).Take(MaximumChips).ToArray();
        var entries=new List<(RecentlyPlayedChip Chip,RecentlyPlayedSource Source)>(ordered.Length);
        _runs.RemoveAll(run=>run.IsCompleted);
        if(Chips.Count==ordered.Length && Chips.Select(chip=>chip.PlaceId).SequenceEqual(ordered.Select(source=>source.PlaceId)))
        {
            // Same games: keep the chips and the naming already in flight for them. Desktop presentations repeat the same
            // observations about every 0.5-1 s; restarting here would cancel and re-send the lookup on every presentation.
            if(_stop is not null) return _current;
            for(int index=0;index<ordered.Length;index++) entries.Add((Chips[index],ordered[index]));
        }
        else
        {
            CancelRun();Chips.Clear();
            foreach(var source in ordered)
            {
                var chip=new RecentlyPlayedChip(source.PlaceId);
                if(_names.TryGetValue(source.PlaceId,out var name)) chip.SetName(name);
                Chips.Add(chip);entries.Add((chip,source));
            }
            Raise(nameof(HasChips));
        }
        var pending=entries.Where(entry=>!entry.Chip.HasName && !_attempted.Contains(entry.Chip.PlaceId) && !_refused.Contains(entry.Chip.PlaceId)).ToArray();
        if(pending.Length==0) return Task.CompletedTask;
        CancelRun();
        var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,ct);
        long generation=Interlocked.Increment(ref _generation);_stop=stop;
        var run=NameAsync(pending,generation,stop);_runs.Add(run);_current=run;return run;
    }

    private async Task NameAsync((RecentlyPlayedChip Chip,RecentlyPlayedSource Source)[] pending,long generation,CancellationTokenSource stop)
    {
        try
        {
            var batched=_batch is null?[]:pending.Where(entry=>entry.Source.UniverseId is >0).ToArray();
            if(batched.Length!=0)
            {
                long[] universes=batched.Select(entry=>entry.Source.UniverseId!.Value).Distinct().Take(PublicRobloxGameBatchLimits.MaximumUniverses).ToArray();
                var result=await _batch!.LookupGamesAsync(universes,stop.Token);
                if(!Current(generation,stop)) return;
                if(PublicDirectoryDeferral.IsLocal(result)) {Refuse(pending);return;} // Turned down on this PC: end the run.
                var names=new Dictionary<long,string>();
                if(result.State==PublicDirectoryState.Success && result.Value is { } games) foreach(var game in games) names[game.UniverseId]=game.Name;
                foreach(var entry in batched)
                {
                    _attempted.Add(entry.Chip.PlaceId);
                    if(names.TryGetValue(entry.Source.UniverseId!.Value,out var name)) {_names[entry.Chip.PlaceId]=name;entry.Chip.SetName(name);}
                }
            }
            foreach(var entry in pending.Except(batched))
            {
                if(!Current(generation,stop)) return;
                if(_single is null) {_attempted.Add(entry.Chip.PlaceId);continue;} // No lookup capability: the place ID stays.
                var result=await _single.LookupGameAsync(entry.Chip.PlaceId,stop.Token);
                if(!Current(generation,stop)) return;
                if(PublicDirectoryDeferral.IsLocal(result)) {Refuse(pending);return;}
                _attempted.Add(entry.Chip.PlaceId);
                if(result.State==PublicDirectoryState.Success && result.Value is { } game) {_names[entry.Chip.PlaceId]=game.Name;entry.Chip.SetName(game.Name);}
            }
        }
        catch(OperationCanceledException) { }
        catch(Exception error) {Report(error);}
        finally {if(ReferenceEquals(_stop,stop)) _stop=null;stop.Dispose();}
    }
    /// <summary>Every place this run had not finished waits for an explicit trigger, so presentation updates cannot re-ask a slow Roblox.</summary>
    private void Refuse((RecentlyPlayedChip Chip,RecentlyPlayedSource Source)[] pending)
    {foreach(var entry in pending) if(!_attempted.Contains(entry.Chip.PlaceId)) _refused.Add(entry.Chip.PlaceId);}
    private bool Current(long generation,CancellationTokenSource stop)=>Allowed && generation==Interlocked.Read(ref _generation) && !stop.IsCancellationRequested;

    private void UseChip(object? parameter)
    {
        if(parameter is not RecentlyPlayedChip chip || !Allowed || !Chips.Contains(chip)) return;
        _browser.UsePreferredPlace(chip.PlaceId); // The game only: an old server from a log is never requested.
    }

    /// <summary>
    /// Join a game opened or another account was chosen there: places turned down on this PC may be asked once more by the
    /// next update. Called only on those explicit triggers, never per presentation. Roblox pauses and unnamed places stay attempted.
    /// </summary>
    public void ForgetLocalRefusals()=>_refused.Clear();
    /// <summary>Leaving the page, Lock or Panic: cancel naming, clear the chips and forget this visit's names.</summary>
    public void Cancel()
    {
        CancelRun();_names.Clear();_attempted.Clear();_refused.Clear();
        if(Chips.Count!=0) {Chips.Clear();Raise(nameof(HasChips));}
    }
    private void CancelRun()
    {Interlocked.Increment(ref _generation);try {_stop?.Cancel();} catch(ObjectDisposedException) { } _stop=null;_current=Task.CompletedTask;}
    private void Report(Exception error)
    {
        _=error; // Provider details never reach diagnostics; only the fixed application-owned failure does.
        try {_failed(new InvalidOperationException("The recently played lookup failed."));} catch(Exception) { /* Diagnostics never become a second owner. */ }
    }
    public async ValueTask DisposeAsync()
    {
        _disposed=true;Cancel();
        try {_lifetime.Cancel();} catch(ObjectDisposedException) { }
        foreach(var run in _runs.ToArray()) {try {await run;} catch(Exception) { /* Faults were already reported per run. */ }}
        _runs.Clear();_lifetime.Dispose();
    }
}
