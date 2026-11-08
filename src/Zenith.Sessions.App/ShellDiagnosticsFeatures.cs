using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed record DiagnosticRow(ManagerDiagnosticEvent Event)
{
    public long Sequence=>Event.Sequence;
    public string Time=>Event.Utc.ToLocalTime().ToString("HH:mm:ss.fff",CultureInfo.InvariantCulture);
    public string Level=>Event.Level.ToString();
    public string Subsystem=>SubsystemLabel(Event.Subsystem);
    public string Description=>Event.Summary;
    public string ErrorType=>Event.ErrorType??"—";
    // Fixed application-owned labels for the closed subsystem set; the report keeps the enum name.
    public static string SubsystemLabel(ManagerSubsystem? subsystem)=>subsystem switch
    {
        null=>"—",
        ManagerSubsystem.Library=>"Account library",
        ManagerSubsystem.Vault=>"Credential vault",
        ManagerSubsystem.Queue=>"Launch queue",
        ManagerSubsystem.SignIn=>"Sign-in",
        ManagerSubsystem.Directory=>"Games and servers",
        ManagerSubsystem.Telemetry=>"Client monitoring",
        ManagerSubsystem.ClientWindows=>"Client windows",
        ManagerSubsystem.Previews=>"Client previews",
        ManagerSubsystem.MacroHost=>"Macro host",
        ManagerSubsystem.Settings=>"Settings",
        ManagerSubsystem.Diagnostics=>"Diagnostics",
        ManagerSubsystem.Launch=>"Native launch",
        ManagerSubsystem.Installation=>"Roblox installation",
        ManagerSubsystem.ClientPriority=>"Client priority",
        ManagerSubsystem.Lifecycle=>"Startup and shutdown",
        ManagerSubsystem.ClientMemory=>"Client memory",
        ManagerSubsystem.RepeatLaunch=>"Repeat launch",
        ManagerSubsystem.ClientSound=>"Client sound",
        ManagerSubsystem.ClientLogs=>"Roblox client logs",
        ManagerSubsystem.ClientUpdate=>"Roblox update",
        ManagerSubsystem.KeepAwake=>"Keep clients awake",
        ManagerSubsystem.ScheduledShutdown=>"Scheduled shutdown",
        ManagerSubsystem.PublicNetwork=>"Public-network guard",
        ManagerSubsystem.DiscordAlerts=>"Discord alerts",
        ManagerSubsystem.DiscordPresence=>"Discord status",
        ManagerSubsystem.UploadLimit=>"Upload limit",
        _=>"Unrecognized subsystem"
    };
}

public sealed partial class ShellViewModel
{
    private ManagerDiagnosticSnapshot? _diagnosticSnapshot;
    private CancellationTokenSource? _diagnosticExportStop;
    private long _diagnosticAfterSequence;
    private bool _diagnosticsPaused,_showErrors=true,_showWarnings=true,_showInformation=true;
    private string _diagnosticFilter="";
    public ObservableCollection<DiagnosticRow> DiagnosticRows {get;}=[];
    public string DiagnosticFilter {get=>_diagnosticFilter;set {value=(value??"");if(value.Length>120)value=value[..120];if(Set(ref _diagnosticFilter,value)) FilterDiagnostics();}}
    public bool ShowErrors {get=>_showErrors;set {if(Set(ref _showErrors,value)) FilterDiagnostics();}}
    public bool ShowWarnings {get=>_showWarnings;set {if(Set(ref _showWarnings,value)) FilterDiagnostics();}}
    public bool ShowInformation {get=>_showInformation;set {if(Set(ref _showInformation,value)) FilterDiagnostics();}}
    public bool DiagnosticsPaused {get=>_diagnosticsPaused;set {if(!Set(ref _diagnosticsPaused,value))return;Raise(nameof(DiagnosticsPauseLabel));if(!value) RefreshDiagnosticsView(true);RaiseDiagnosticLabels();}}
    public string DiagnosticsPauseLabel=>DiagnosticsPaused?"Resume view":"Pause view";
    public string DiagnosticErrors=>$"Errors {_diagnosticSnapshot?.ErrorCount??0}";
    public string DiagnosticWarnings=>$"Warnings {_diagnosticSnapshot?.WarningCount??0}";
    public string DiagnosticInformation=>$"Info {_diagnosticSnapshot?.InfoCount??0}";
    public string DiagnosticViewStatus=>$"{(DiagnosticsPaused?"Paused view":DiagnosticsLive?"Live view":"Updates when refreshed")} · {DiagnosticRows.Count} shown · newest 512 events retained · session totals above";
    // Live means the owned monitor loop is still pushing this view. After a startup failure or a
    // monitor fault, only tab switches, explicit refreshes and later failures update it.
    private bool DiagnosticsLive=>!_monitorTask.IsCompleted && !_overviewMonitorStopped;
    private bool _diagnosticsLiveShown;
    public string DiagnosticStorageStatus=>_diagnosticSnapshot?.Failure??
        $"Disk log skips: {_diagnosticSnapshot?.DroppedEvents??0}. Logs rotate across three bounded files. Clear view keeps those files.";
    public Command PauseDiagnostics {get;private set;}=null!;
    public Command ClearDiagnostics {get;private set;}=null!;
    public Command RefreshDiagnostics {get;private set;}=null!;
    public Command OpenDiagnosticsFolder {get;private set;}=null!;
    public AsyncCommand ExportDiagnostics {get;private set;}=null!;
    private IClientLogTailReader _clientLogTails=null!;
    private bool _clientLogTailsPermitted,_includeClientLogTails;
    /// <summary>Save report only, off until ticked, and cleared again after each report it was included in; Copy diagnostics never includes client log entries.</summary>
    public bool IncludeClientLogTails {get=>_includeClientLogTails;set {if(!CanIncludeClientLogTails) value=false;Set(ref _includeClientLogTails,value);}}
    /// <summary>False in the sample profile unless a reader was injected, so the sample profile never reads the real Roblox folders.</summary>
    public bool CanIncludeClientLogTails=>_clientLogTailsPermitted;
    private void InitializeDiagnosticsFeatures(IClientLogTailReader? clientLogTails)
    {
        _clientLogTailsPermitted=!IsSample || clientLogTails is not null;
        _clientLogTails=clientLogTails??new RobloxClientLogTails();
        PauseDiagnostics=new(_=> {if(!IsLocked && !_shuttingDown) DiagnosticsPaused=!DiagnosticsPaused;});
        ClearDiagnostics=new(_=>
        {
            if(IsLocked || _shuttingDown) return;
            _diagnosticAfterSequence=_diagnostics.GetSnapshot().LatestSequence;
            DiagnosticRows.Clear();RaiseDiagnosticLabels();Notice="Diagnostic view cleared. Saved log files were kept.";
        });
        RefreshDiagnostics=new(_=> {if(!IsLocked && !_shuttingDown) RefreshDiagnosticsView(true);});
        OpenDiagnosticsFolder=new(_=>
        {
            if(IsLocked || _shuttingDown) return;
            try
            {
                string folder=Path.Combine(DataRoot,"diagnostics");
                if(!Directory.Exists(folder)) throw new IOException("The diagnostics folder is unavailable.");
                Process.Start(new ProcessStartInfo {FileName=folder,UseShellExecute=true});
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {Fail(ex,ManagerSubsystem.Diagnostics);}
        });
        ExportDiagnostics=new(p=>p is string path?ExportDiagnosticsAsync(path):Task.CompletedTask,error=>Fail(error,ManagerSubsystem.Diagnostics));
    }
    private const string ReportSavedNotice="Redacted diagnostic report saved with the Windows version and the active performance, theme and capacity settings. No account names, credentials, game links, paths or raw exception messages were included.";
    private async Task ExportDiagnosticsAsync(string path)
    {
        if(IsLocked || _shuttingDown) return;
        bool includeClientLogs=IncludeClientLogTails && CanIncludeClientLogTails;
        string dataRoot=DataRoot,applicationRoot=AppContext.BaseDirectory;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_diagnosticExportStop=stop;
        try
        {
            ClientLogTailReport? clientLogs=null;
            if(includeClientLogs)
            {
                // The tails are read off the UI thread under this export's own stop (a Step in StopOperations), so a Panic or Lock
                // that lands here ends the read, writes nothing and keeps its own notice.
                clientLogs=await _clientLogTails.ReadAsync(stop.Token);
                if(stop.IsCancellationRequested || IsLocked || _shuttingDown) return;
            }
            string report=BuildDiagnosticReport(clientLogs);
            await Task.Run(()=>ExplicitReportFile.WriteReportAsync(path,Encoding.UTF8.GetBytes(report),dataRoot,applicationRoot,stop.Token),stop.Token);
            // Panic, Lock and shutdown own the notice once they land; a write that already committed stays on disk silently.
            if(!stop.IsCancellationRequested && !IsLocked && !_shuttingDown)
            {
                Notice=clientLogs is null?ReportSavedNotice:ReportSavedNotice+" "+DescribeClientLogTails(clientLogs);
                // Each saved report is its own opt-in: the tick clears once a report carried the entries.
                if(clientLogs is not null) IncludeClientLogTails=false;
            }
        }
        catch(Exception error) when(error is not OperationCanceledException && (stop.IsCancellationRequested || IsLocked || _shuttingDown))
        {
            // A late disk/path failure stays observable as a typed event without replacing the stop or lock message.
            _diagnostics.RecordFailure(ManagerSubsystem.Diagnostics,FailureType(error));
        }
        finally {if(ReferenceEquals(_diagnosticExportStop,stop)) _diagnosticExportStop=null;}
    }
    private void CancelDiagnosticExport()=>_diagnosticExportStop?.Cancel();
    // The fact is short enough for the trimmed header; the remedy ends the status, for the tooltip and the Guide.
    internal const string RepeatLaunchStoppedStatus="a new launch cannot show Zenith";
    internal const string RepeatLaunchStoppedRemedy="exit from the tray and start Zenith Sessions again to let a new launch show this window";
    // UI thread only: set by the dispatcher step below, read by RefreshDiagnosticsStatus.
    private bool _repeatLaunchListenerStopped;
    /// <summary>
    /// The single-instance restore listener's fault reporter, called on the listener's own task. The type alone is recorded
    /// (Rule 10). Once the listener has stopped, Diagnostics says that a new launch cannot show this window, because that
    /// launch can only say "already starting". The status and visible rows update now, not on the next monitor tick.
    /// </summary>
    internal void RestoreListenerFaulted(Type errorType,bool stopped)
    {
        _diagnostics.RecordFailure(ManagerSubsystem.RepeatLaunch,errorType);
        if(_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(()=>
        {
            if(_shuttingDown) return;
            if(stopped) _repeatLaunchListenerStopped=true;
            RefreshDiagnosticsView();
        }));
    }
    private void RefreshDiagnosticsStatus()
    {
        bool live=DiagnosticsLive;
        DiagnosticsStatus=DiagnosticsStatusText(_diagnostics.Failure,live,_overviewMonitorStopped,_libraryUnavailable,_diagnostics.DroppedEvents,_repeatLaunchListenerStopped);
        // Keep the view label honest even when the tab is hidden, paused or locked as liveness changes.
        if(live!=_diagnosticsLiveShown) {_diagnosticsLiveShown=live;Raise(nameof(DiagnosticViewStatus));}
    }
    /// <summary>
    /// The Diagnostics page header draws this on one trimmed 320 px line (about 60 characters at 12 px); only its tooltip
    /// and the Guide show the rest. So the facts come in the order a reader needs them, each short enough to be read
    /// without hovering: a stopped monitor or startup first, then a stopped restore listener (the only place that says
    /// why a new launch shows nothing), then the disk writer's failure or its skip count. The storage footer repeats the
    /// failure and the count, so the count is the one that may fall behind the ellipsis when all three are set. The
    /// restore listener's remedy comes last, for the tooltip. The first fact carries the line's capital; the writer's
    /// failure text is a sentence of its own and keeps its own.
    /// </summary>
    internal static string DiagnosticsStatusText(string? failure,bool live,bool monitorStopped,bool startupNeedsAttention,long dropped,bool restoreListenerStopped)
    {
        string? stopped=live?null:monitorStopped?"monitoring stopped":startupNeedsAttention?"startup needs attention":null;
        // The skip count mixes a full writer queue with entries lost to a disk error (any kind, failures and Panic
        // included), so it names no cause; a disk-writer failure replaces it so the header agrees with the storage footer.
        string? unwritten=dropped<=0?null:dropped==1?"1 event not saved to disk":$"{dropped} events not saved to disk";
        var parts=new List<string>(6);
        if(stopped is not null) parts.Add(stopped);
        if(restoreListenerStopped) parts.Add(RepeatLaunchStoppedStatus);
        if(failure is not null) parts.Add(failure);
        else if(stopped is not null) {if(unwritten is not null) parts.Add(unwritten);parts.Add("diagnostics still recording");}
        else if(!live) parts.Add("diagnostics starting");
        else {parts.Add("diagnostics active");parts.Add(unwritten??"bounded to three log files");}
        if(stopped is not null) parts.Add("view updates when refreshed");
        if(restoreListenerStopped) parts.Add(RepeatLaunchStoppedRemedy);
        string status=string.Join(" · ",parts);
        return char.ToUpperInvariant(status[0])+status[1..];
    }
    private void RefreshDiagnosticsView(bool explicitRequest=false)
    {
        if(_shuttingDown) return;
        RefreshDiagnosticsStatus();
        if(IsLocked || DiagnosticsPaused || (!explicitRequest && !IsDiagnostics)) return;
        // An unchanged presentation tick copies no ring and raises no labels. Pause, Clear, the filters and
        // liveness raise the labels on their own paths; Failure or skip-count changes still pass this check.
        if(!explicitRequest && !_diagnostics.HasChangedSince(_diagnosticSnapshot)) return;
        var snapshot=_diagnostics.GetSnapshot();
        bool rowsChanged=_diagnosticSnapshot?.LatestSequence!=snapshot.LatestSequence;
        _diagnosticSnapshot=snapshot;
        if(rowsChanged || explicitRequest) FilterDiagnostics();else RaiseDiagnosticLabels();
    }
    private void FilterDiagnostics()
    {
        if(_diagnosticSnapshot is not { } snapshot) return;
        var visible=snapshot.Events.Where(e=>e.Sequence>_diagnosticAfterSequence &&
            (e.Level switch {DiagnosticLevel.Error=>ShowErrors,DiagnosticLevel.Warning=>ShowWarnings,_=>ShowInformation}) &&
            (DiagnosticFilter.Length==0 || e.Summary.Contains(DiagnosticFilter,StringComparison.OrdinalIgnoreCase) ||
             e.Kind.ToString().Contains(DiagnosticFilter,StringComparison.OrdinalIgnoreCase) ||
             (e.Subsystem is not null && DiagnosticRow.SubsystemLabel(e.Subsystem).Contains(DiagnosticFilter,StringComparison.OrdinalIgnoreCase)) ||
             (e.ErrorType?.Contains(DiagnosticFilter,StringComparison.OrdinalIgnoreCase)??false))).ToArray();
        var keep=visible.Select(e=>e.Sequence).ToHashSet();
        for(int i=DiagnosticRows.Count-1;i>=0;i--) if(!keep.Contains(DiagnosticRows[i].Sequence)) DiagnosticRows.RemoveAt(i);
        int index=0;
        foreach(var item in visible)
        {
            if(index>=DiagnosticRows.Count || DiagnosticRows[index].Sequence!=item.Sequence) DiagnosticRows.Insert(index,new(item));
            index++;
        }
        RaiseDiagnosticLabels();
    }
    private static readonly string[] DiagnosticLabelNames=[nameof(DiagnosticErrors),nameof(DiagnosticWarnings),nameof(DiagnosticInformation),nameof(DiagnosticViewStatus),nameof(DiagnosticStorageStatus)];
    private void RaiseDiagnosticLabels()
    {foreach(string name in DiagnosticLabelNames) Raise(name);}
    public string BuildVisibleDiagnosticText()
    {
        if(IsLocked || _shuttingDown) throw new InvalidOperationException("Unlock the dashboard before copying diagnostics.");
        RefreshDiagnosticsView(true);
        return string.Join(Environment.NewLine,DiagnosticRows.Select(r=>$"{r.Time}  {r.Level}  {r.Subsystem}  {r.Description}  {r.ErrorType}"));
    }
    /// <summary>The clipboard report (Copy diagnostics): never carries Roblox client log entries, whatever the Save report tick says.</summary>
    public string BuildDiagnosticReport()=>BuildDiagnosticReport(null);
    internal string BuildDiagnosticReport(ClientLogTailReport? clientLogs)
    {
        if(IsLocked || _shuttingDown) throw new InvalidOperationException("Unlock the dashboard before exporting diagnostics.");
        RefreshDiagnosticsView(true);
        var snapshot=_diagnosticSnapshot??_diagnostics.GetSnapshot();
        // Applied policy and committed limits only; unsaved drafts, the main account, sign-in and window-layout settings stay out.
        var performance=Volatile.Read(ref _activePerformance);var limits=Volatile.Read(ref _savedSettings).Capacity!;
        // Format 2 added Os, Performance, Theme, Capacity and the per-event Subsystem; format 3 added ClientLogs (opt-in typed Roblox client
        // log entries, Included=false otherwise); advance it again whenever an allowlisted field is added.
        // This counter is the Diagnostics report's own; the launch-evidence report is format 2 (see BuildNativeTrialEvidenceReport before changing either).
        var report=new
        {
            FormatVersion=3,Product="Zenith Sessions Personal",Version=typeof(ShellViewModel).Assembly.GetName().Version?.ToString(),
            CreatedUtc=DateTimeOffset.UtcNow,Runtime=Environment.Version.ToString(),Architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            Os=new {Description=RuntimeInformation.OSDescription,Version=Environment.OSVersion.Version.ToString()},
            Performance=new {performance.Profile,CustomTiming=performance.Profile==ManagerPerformanceProfile.Custom,
                performance.ProcessSampleMs,performance.PresentationMs,performance.StaggerMs,performance.StabilizationMs},
            // The catalog id, never the stored text, so an edited settings file cannot carry free text into a report.
            Theme=SessionsThemes.Normalize(_settings.Theme),
            Capacity=new {limits.SparePhysicalPercent,limits.SparePhysicalGiB,limits.SpareCommitPercent,limits.SpareCommitGiB,
                limits.NewClientPhysicalGiB,limits.NewClientCommitGiB,limits.UserMaximum},
            SessionTotals=new {snapshot.TotalEvents,snapshot.InfoCount,snapshot.WarningCount,snapshot.ErrorCount},
            snapshot.DroppedEvents,PersistenceAvailable=snapshot.Failure is null,PausedView=DiagnosticsPaused,
            Scope=clientLogs is null
                ? "Filtered in-memory view, newest 512 events; no prior-session replay or raw log files."
                : "Filtered in-memory view, newest 512 events; no prior-session replay. Roblox client log entries are parsed fields only (time, kind, place and server IDs, disconnect code): no raw lines, player IDs, URLs or paths.",
            Events=DiagnosticRows.Select(r=>new {r.Event.Sequence,r.Event.Utc,r.Event.Kind,r.Event.Level,r.Event.Subsystem,r.Event.ErrorType,r.Event.Count}).ToArray(),
            // Typed fields copied out of the correlator's own parse; the reader keeps no line text, path or name, so none can appear here.
            ClientLogs=clientLogs is null?new {Included=false}:(object)new
            {
                Included=true,clientLogs.ReadUtc,clientLogs.FoldersChecked,clientLogs.FoldersUnreadable,clientLogs.LogsFound,clientLogs.BudgetLimited,
                Policy=$"Player logs written in the last day, newest {RobloxClientLogTails.MaximumLogs}, last {RobloxClientLogTails.TailBytes/1024} KiB of each, newest {RobloxClientLogTails.MaximumEntriesPerLog} recognised lines of each, as parsed fields only.",
                Logs=clientLogs.Logs.Select(log=>new
                {
                    log.Order,log.StorePackage,log.ModifiedUtc,log.Length,log.BytesRead,log.LinesRead,log.EntriesDropped,log.Outcome,
                    Entries=log.Entries.Select(entry=>new {entry.LoggedUtc,entry.Fact,entry.PlaceId,entry.JobId,entry.DisconnectReason}).ToArray()
                }).ToArray()
            }
        };
        return JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true,Converters={new JsonStringEnumConverter()}});
    }
    /// <summary>The sentence added to the saved-report notice when client log entries were asked for; counts only, never a path or name.</summary>
    internal static string DescribeClientLogTails(ClientLogTailReport report)
    {
        if(report.FoldersChecked==0 && report.FoldersUnreadable==0) return "No Roblox logs folder was found for this Windows user, so no client log entries were included.";
        if(report.Logs.Count==0) return report.FoldersUnreadable>0
            ? "A Roblox logs folder could not be read, so no client log entries were included."
            : "No Roblox Player log written in the last day was found, so no client log entries were included.";
        int entries=report.EntryCount;
        string logs=report.Logs.Count==1?"1 recent Roblox client log":$"{report.Logs.Count} recent Roblox client logs";
        string head=entries==0?$"No recognised entries were found in {logs}"
            : $"{(entries==1?"1 recognised entry":$"{entries} recognised entries")} from {logs} included as parsed fields only";
        int unread=report.Logs.Count(log=>log.Outcome!=ClientLogTailOutcome.Read);
        string unreadText=unread==0?"":unread==1?" 1 log could not be read.":$" {unread} logs could not be read.";
        string limit=report.BudgetLimited?" The log read stopped at its limit.":"";
        return head+"; no raw log lines, player IDs, URLs or paths."+unreadText+limit;
    }
}
