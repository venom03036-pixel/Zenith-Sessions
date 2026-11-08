using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacroSuite.App.Themes;
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
    private void InitializeDiagnosticsFeatures()
    {
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
    private async Task ExportDiagnosticsAsync(string path)
    {
        if(IsLocked || _shuttingDown) return;
        string report=BuildDiagnosticReport(),dataRoot=DataRoot,applicationRoot=AppContext.BaseDirectory;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_diagnosticExportStop=stop;
        try
        {
            await Task.Run(()=>ExplicitReportFile.WriteReportAsync(path,Encoding.UTF8.GetBytes(report),dataRoot,applicationRoot,stop.Token),stop.Token);
            // Panic, Lock and shutdown own the notice once they land; a write that already committed stays on disk silently.
            if(!stop.IsCancellationRequested && !IsLocked && !_shuttingDown)
                Notice="Redacted diagnostic report saved with the Windows version and the active performance, theme and capacity settings. No account names, credentials, game links, paths or raw exception messages were included.";
        }
        catch(Exception error) when(error is not OperationCanceledException && (stop.IsCancellationRequested || IsLocked || _shuttingDown))
        {
            // A late disk/path failure stays observable as a typed event without replacing the stop or lock message.
            _diagnostics.RecordFailure(ManagerSubsystem.Diagnostics,error.GetType());
        }
        finally {if(ReferenceEquals(_diagnosticExportStop,stop)) _diagnosticExportStop=null;}
    }
    private void CancelDiagnosticExport()=>_diagnosticExportStop?.Cancel();
    internal const string RepeatLaunchStoppedStatus="a new launch cannot bring this window forward until you exit from the tray and start Zenith Sessions again";
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
        bool live=DiagnosticsLive;long dropped=_diagnostics.DroppedEvents;
        string? stopped=live?null:_overviewMonitorStopped?"monitoring stopped":_libraryUnavailable?"startup needs attention":null;
        // A disk-writer failure always wins so the header agrees with the storage footer.
        string status=_diagnostics.Failure??(stopped is not null
            ? $"{(dropped>0?$"Diagnostics busy: {dropped} routine events skipped":"Diagnostics recording")} · {stopped} · view updates when refreshed"
            : !live?"Diagnostics starting"
            : dropped>0?$"Diagnostics busy: {dropped} routine events skipped.":"Diagnostics active · bounded to three log files");
        // A stopped restore listener is a separate fact, so it is kept even next to a disk-writer failure.
        DiagnosticsStatus=_repeatLaunchListenerStopped?status+" · "+RepeatLaunchStoppedStatus:status;
        // Keep the view label honest even when the tab is hidden, paused or locked as liveness changes.
        if(live!=_diagnosticsLiveShown) {_diagnosticsLiveShown=live;Raise(nameof(DiagnosticViewStatus));}
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
    public string BuildDiagnosticReport()
    {
        if(IsLocked || _shuttingDown) throw new InvalidOperationException("Unlock the dashboard before exporting diagnostics.");
        RefreshDiagnosticsView(true);
        var snapshot=_diagnosticSnapshot??_diagnostics.GetSnapshot();
        // Applied policy and committed limits only; unsaved drafts, the main account, sign-in and window-layout settings stay out.
        var performance=Volatile.Read(ref _activePerformance);var limits=Volatile.Read(ref _savedSettings).Capacity!;
        var report=new
        {
            FormatVersion=1,Product="Zenith Sessions Personal",Version=typeof(ShellViewModel).Assembly.GetName().Version?.ToString(),
            CreatedUtc=DateTimeOffset.UtcNow,Runtime=Environment.Version.ToString(),Architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            Os=new {Description=RuntimeInformation.OSDescription,Version=Environment.OSVersion.Version.ToString()},
            Performance=new {performance.Profile,CustomTiming=performance.Profile==ManagerPerformanceProfile.Custom,
                performance.ProcessSampleMs,performance.PresentationMs,performance.StaggerMs,performance.StabilizationMs},
            // The catalog id, never the stored text, so an edited settings file cannot carry free text into a report.
            Theme=ThemeCatalog.Resolve(_settings.Theme).Id,
            Capacity=new {limits.SparePhysicalPercent,limits.SparePhysicalGiB,limits.SpareCommitPercent,limits.SpareCommitGiB,
                limits.NewClientPhysicalGiB,limits.NewClientCommitGiB,limits.UserMaximum},
            SessionTotals=new {snapshot.TotalEvents,snapshot.InfoCount,snapshot.WarningCount,snapshot.ErrorCount},
            snapshot.DroppedEvents,PersistenceAvailable=snapshot.Failure is null,PausedView=DiagnosticsPaused,
            Scope="Filtered in-memory view, newest 512 events; no prior-session replay or raw log files.",
            Events=DiagnosticRows.Select(r=>new {r.Event.Sequence,r.Event.Utc,r.Event.Kind,r.Event.Level,r.Event.Subsystem,r.Event.ErrorType,r.Event.Count}).ToArray()
        };
        return JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true,Converters={new JsonStringEnumConverter()}});
    }
}
