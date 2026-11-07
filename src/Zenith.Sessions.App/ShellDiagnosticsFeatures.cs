using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed record DiagnosticRow(ManagerDiagnosticEvent Event)
{
    public long Sequence=>Event.Sequence;
    public string Time=>Event.Utc.ToLocalTime().ToString("HH:mm:ss.fff",CultureInfo.InvariantCulture);
    public string Level=>Event.Level.ToString();
    public string Description=>Event.Summary;
    public string ErrorType=>Event.ErrorType??"—";
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
    public string DiagnosticViewStatus=>$"{(DiagnosticsPaused?"Paused view":"Live view")} · {DiagnosticRows.Count} shown · newest 512 events retained · session totals above";
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
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {Fail(ex);}
        });
        ExportDiagnostics=new(p=>p is string path?ExportDiagnosticsAsync(path):Task.CompletedTask,Fail);
    }
    private async Task ExportDiagnosticsAsync(string path)
    {
        if(IsLocked || _shuttingDown) return;
        string report=BuildDiagnosticReport(),dataRoot=DataRoot,applicationRoot=AppContext.BaseDirectory;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_diagnosticExportStop=stop;
        try
        {
            await Task.Run(()=>ExplicitReportFile.WriteAsync(path,Encoding.UTF8.GetBytes(report),
                candidate=>DiagnosticExportPath.Validate(candidate,dataRoot,applicationRoot),stop.Token),stop.Token);
            // Panic, Lock and shutdown own the notice once they land; a write that already committed stays on disk silently.
            if(!stop.IsCancellationRequested && !IsLocked && !_shuttingDown)
                Notice="Redacted diagnostic report saved. No account names, credentials, game links, paths or raw exception messages were included.";
        }
        catch(Exception error) when(error is not OperationCanceledException && (stop.IsCancellationRequested || IsLocked || _shuttingDown))
        {
            // A late disk/path failure stays observable as a typed event without replacing the stop or lock message.
            _diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType());
        }
        finally {if(ReferenceEquals(_diagnosticExportStop,stop)) _diagnosticExportStop=null;}
    }
    private void CancelDiagnosticExport()=>_diagnosticExportStop?.Cancel();
    private void RefreshDiagnosticsView(bool explicitRequest=false)
    {
        if(_shuttingDown || IsLocked || DiagnosticsPaused || (!explicitRequest && !IsDiagnostics)) return;
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
    private void RaiseDiagnosticLabels()
    {foreach(string name in new[]{nameof(DiagnosticErrors),nameof(DiagnosticWarnings),nameof(DiagnosticInformation),nameof(DiagnosticViewStatus),nameof(DiagnosticStorageStatus)}) Raise(name);}
    public string BuildVisibleDiagnosticText()
    {
        if(IsLocked || _shuttingDown) throw new InvalidOperationException("Unlock the dashboard before copying diagnostics.");
        RefreshDiagnosticsView(true);
        return string.Join(Environment.NewLine,DiagnosticRows.Select(r=>$"{r.Time}  {r.Level}  {r.Description}  {r.ErrorType}"));
    }
    public string BuildDiagnosticReport()
    {
        if(IsLocked || _shuttingDown) throw new InvalidOperationException("Unlock the dashboard before exporting diagnostics.");
        RefreshDiagnosticsView(true);
        var snapshot=_diagnosticSnapshot??_diagnostics.GetSnapshot();
        var report=new
        {
            FormatVersion=1,Product="Zenith Sessions Personal",Version=typeof(ShellViewModel).Assembly.GetName().Version?.ToString(),
            CreatedUtc=DateTimeOffset.UtcNow,Runtime=Environment.Version.ToString(),Architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            SessionTotals=new {snapshot.TotalEvents,snapshot.InfoCount,snapshot.WarningCount,snapshot.ErrorCount},
            snapshot.DroppedEvents,PersistenceAvailable=snapshot.Failure is null,PausedView=DiagnosticsPaused,
            Scope="Filtered in-memory view, newest 512 events; no prior-session replay or raw log files.",
            Events=DiagnosticRows.Select(r=>new {r.Event.Sequence,r.Event.Utc,r.Event.Kind,r.Event.Level,r.Event.ErrorType,r.Event.Count}).ToArray()
        };
        return JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true,Converters={new JsonStringEnumConverter()}});
    }
}
