using System.Collections.ObjectModel;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>
/// One entry in the History picker. A live entry is the ordinary client selection. An ended entry (<see cref="Live"/> is null)
/// only shows retained measurements; it never becomes <see cref="ShellViewModel.SelectedSession"/>, so join, close, arrange,
/// preview and log actions cannot target a process that is no longer observed.
/// </summary>
public sealed record ClientHistoryChoice(SessionProcessKey Process, string Label, SessionRow? Live);

public sealed partial class ShellViewModel
{
    private readonly TelemetryHistory _telemetryHistory=new();
    public TelemetryDisplay Telemetry {get;private set;}=null!;
    public GpuTelemetryDisplay GpuTelemetry { get; } = new();
    public Command ShowClientHistoryMode {get;private set;}=null!;
    private bool _clientHistoryMode;
    public bool IsClientHistoryMode=>_clientHistoryMode;
    /// <summary>Live clients, then clients that a fresh complete scan recorded as no longer observed and whose series is retained.</summary>
    public ObservableCollection<ClientHistoryChoice> HistoryChoices {get;}=[];
    private ClientHistoryChoice? _selectedHistoryChoice;
    // Dispatcher-thread scratch, reused on every presentation (Rule 7).
    private readonly HashSet<SessionProcessKey> _liveHistoryKeys=[];
    public ClientHistoryChoice? SelectedHistoryChoice
    {
        get=>_selectedHistoryChoice;
        set
        {
            if(value is not null && !HistoryChoices.Contains(value)) return;
            if(!Set(ref _selectedHistoryChoice,value)) return;
            // The picker writes null only when its item leaves the list; the shell then decides what replaces it.
            if(value is not null) SelectedSession=value.Live;
            RefreshTelemetryPresentation();
        }
    }
    private void InitializeTelemetry()
    {
        Telemetry=new(_telemetryHistory);
        ShowClientHistoryMode=new(_=>SetSessionPresentation(false,true));
    }
    private void RefreshTelemetryPresentation()
    {
        if(Telemetry is null || IsLocked || _shuttingDown || !IsOverview && !(IsSessions && IsClientHistoryMode)) return;
        var selected=SelectedSession?.Observation;
        SessionProcessKey? key=null;bool ended=false;
        if(selected is {ProcessId:>0,ProcessStartUtcTicks:>0}) key=new(null,selected.ProcessId,selected.ProcessStartUtcTicks);
        else if(SelectedSession is null && _selectedHistoryChoice is {Live:null} last) {key=last.Process;ended=true;}
        Telemetry.Update(key,DateTimeOffset.UtcNow,ended);
        GpuTelemetry.Update(Volatile.Read(ref _latestDesktop)?.Resources.Gpu, DateTimeOffset.UtcNow);
    }
    /// <summary>Keeps the picker on the row chosen elsewhere (table, previews). Clearing the row leaves an ended choice alone.</summary>
    private void FollowSelectedSessionInHistory(SessionRow? row)
    {
        if(row is null && _selectedHistoryChoice is {Live:null}) return;
        ClientHistoryChoice? match=null;
        if(row is not null)
            for(int i=0;i<HistoryChoices.Count;i++)
                if(ReferenceEquals(HistoryChoices[i].Live,row)) {match=HistoryChoices[i];break;}
        if(ReferenceEquals(match,_selectedHistoryChoice)) return;
        _selectedHistoryChoice=match;Raise(nameof(SelectedHistoryChoice));
    }
    /// <summary>
    /// Runs on each presentation after the rows and Recent clients have taken the sample. Existing choice objects are kept
    /// so the picker keeps its selection. Ended entries come only from Recent clients, which records a client as no longer
    /// observed from a fresh complete scan; an incomplete scan's absence never creates one.
    /// </summary>
    private void PresentHistoryChoices()
    {
        var departed=SelectedSession is { } chosen && !Sessions.Contains(chosen)?chosen:null;
        _liveHistoryKeys.Clear();
        int count=0;
        for(int i=0;i<Sessions.Count;i++)
        {
            var row=Sessions[i];
            if(_liveHistoryKeys.Add(row.ProcessKey)) PlaceHistoryChoice(count++,row.ProcessKey,row);
        }
        var recent=RecentClients.Rows;
        for(int i=0;i<recent.Count;i++)
        {
            var entry=recent[i].Entry;
            // Telemetry keys carry no host; Recent clients keys may.
            var key=new SessionProcessKey(null,entry.Process.ProcessId,entry.Process.ProcessStartUtcTicks);
            if(entry.NoLongerObservedUtc is null || !_liveHistoryKeys.Add(key)
                || _telemetryHistory.GetClientSnapshot(key) is not {Points.Count:>0}) continue;
            PlaceHistoryChoice(count++,key,null);
        }
        while(HistoryChoices.Count>count) HistoryChoices.RemoveAt(HistoryChoices.Count-1);
        if(departed is not null)
        {
            // In History, a client that ended while selected stays on screen as last-known. The row itself is released.
            ClientHistoryChoice? ended=null;
            if(IsClientHistoryMode)
                for(int i=0;i<HistoryChoices.Count;i++)
                    if(HistoryChoices[i] is {Live:null} candidate && candidate.Process==departed.ProcessKey) {ended=candidate;break;}
            if(ended is not null && !ReferenceEquals(ended,_selectedHistoryChoice)) {_selectedHistoryChoice=ended;Raise(nameof(SelectedHistoryChoice));}
            SelectedSession=null;
        }
        else if(_selectedHistoryChoice is { } current && !HistoryChoices.Contains(current)) SelectedHistoryChoice=null;
    }
    private void PlaceHistoryChoice(int index,SessionProcessKey key,SessionRow? live)
    {
        for(int i=index;i<HistoryChoices.Count;i++)
        {
            var existing=HistoryChoices[i];
            if(existing.Process!=key || !ReferenceEquals(existing.Live,live)) continue;
            if(i!=index) HistoryChoices.Move(i,index);
            return;
        }
        HistoryChoices.Insert(index,new(key,live?.ProcessLabel??$"PID {key.ProcessId} · ended · last-known",live));
    }
}
