using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class DiagnosticsViewModelTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-diagnostic-ui-tests-"+Guid.NewGuid().ToString("N"));
    private string Data=>Path.Combine(_root,"data");
    [Fact]
    public void EveryShellFailureRecordNamesTheSubsystemThatFailed()
    {
        // Only the last-chance unhandled-fault seams cannot know which part failed; every other shell failure names it.
        string app=Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"src","Zenith.Sessions.App");
        var unnamed=Directory.GetFiles(app,"*.cs").SelectMany(file=>File.ReadAllLines(file).Select((line,index)=>(File:Path.GetFileName(file),Line:index+1,Text:line)))
            .Where(line=>line.Text.Contains("_diagnostics.Record(ManagerEventKind.OperationFailed",StringComparison.Ordinal)
                && !line.Text.Contains("_diagnostics.Record(ManagerEventKind.OperationFailed,errorType)",StringComparison.Ordinal))
            .Select(line=>line.File+":"+line.Line).ToArray();
        Assert.Empty(unnamed);
    }
    [Fact]
    public void EveryManagerSubsystemHasItsOwnFixedLabel()
    {
        // The enum is append-only. A member added without a label would read "Unrecognized subsystem" in the view and the report.
        string[] labels=Enum.GetValues<ManagerSubsystem>().Select(subsystem=>DiagnosticRow.SubsystemLabel(subsystem)).ToArray();
        Assert.DoesNotContain("Unrecognized subsystem",labels);
        Assert.Equal(labels.Length,labels.Distinct(StringComparer.Ordinal).Count());
    }
    [Fact]
    public Task FiltersAndReportsExposeOnlyTypedEventsNotAccountOrExceptionText()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        vm.Navigate.Execute("Diagnostics");vm.ClearDiagnostics.Execute(null);
        const string secret="PRIVATE-FIXTURE-TOKEN-and-https://example.invalid/secret";
        await vm.SaveAccountAsync(AccountProfile.Create(secret));vm.Fail(new IOException(secret),ManagerSubsystem.Library);
        vm.RefreshDiagnostics.Execute(null);Assert.NotEmpty(vm.DiagnosticRows);
        Assert.Contains(vm.DiagnosticRows,r=>r.Level=="Error");Assert.Contains(vm.DiagnosticRows,r=>r.Level=="Info");
        vm.ShowInformation=false;vm.ShowWarnings=false;Assert.All(vm.DiagnosticRows,r=>Assert.Equal("Error",r.Level));
        string report=vm.BuildDiagnosticReport();using var parsed=JsonDocument.Parse(report);
        Assert.Equal(2,parsed.RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.All(parsed.RootElement.GetProperty("Events").EnumerateArray(),e=>Assert.Equal("Error",e.GetProperty("Level").GetString()));
        Assert.DoesNotContain(secret,report);Assert.DoesNotContain(Data,report);Assert.DoesNotContain(secret,vm.BuildVisibleDiagnosticText());
        vm.DiagnosticFilter=secret;Assert.Empty(vm.DiagnosticRows);
        report=vm.BuildDiagnosticReport();Assert.DoesNotContain(secret,report);using var empty=JsonDocument.Parse(report);
        Assert.Equal(0,empty.RootElement.GetProperty("Events").GetArrayLength());
        vm.Lock.Execute(null);Assert.Throws<InvalidOperationException>(()=>vm.BuildDiagnosticReport());
        Assert.Throws<InvalidOperationException>(()=>vm.BuildVisibleDiagnosticText());
    });
    [Fact]
    public Task ReportCarriesTheWindowsVersionAndActiveSettingsAsNumbersAndIdsOnly()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        const string label="Sample report account label";
        await vm.SaveAccountAsync(AccountProfile.Create(label));
        vm.ApplyTheme.Execute("ocean-blue");await vm.ApplyTheme.Execution;
        vm.SelectedPerformanceProfile=vm.PerformanceProfiles.Single(p=>p.Profile==ManagerPerformanceProfile.Custom);
        vm.CustomProcessSample="2500";vm.CustomPresentation="1500";vm.CustomStagger="6000";vm.CustomStabilization="40000";
        var culture=System.Globalization.CultureInfo.CurrentCulture;
        vm.SpareRam=7.ToString(culture);vm.PhysicalPerClient=1.5.ToString(culture);vm.CommitPerClient=2.5.ToString(culture);vm.MaximumClients="4";
        vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;Assert.Equal("Settings saved",vm.SaveStatus);
        // An unsaved draft is not the active policy, so the report keeps the saved values.
        vm.CustomProcessSample="3000";vm.SpareRam=9.ToString(culture);
        string report=vm.BuildDiagnosticReport();using var parsed=JsonDocument.Parse(report);var root=parsed.RootElement;
        // Format 2 added Os, Performance, Theme, Capacity and the per-event Subsystem, so a reader can tell it from format 1.
        Assert.Equal(2,root.GetProperty("FormatVersion").GetInt32());
        Assert.True(root.TryGetProperty("Os",out var os),"The report has no Os section.");
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.OSDescription,os.GetProperty("Description").GetString());
        Assert.Equal(Environment.OSVersion.Version.ToString(),os.GetProperty("Version").GetString());
        Assert.True(root.TryGetProperty("Performance",out var performance),"The report has no Performance section.");
        Assert.Equal("Custom",performance.GetProperty("Profile").GetString());Assert.True(performance.GetProperty("CustomTiming").GetBoolean());
        Assert.Equal(2500,performance.GetProperty("ProcessSampleMs").GetInt32());Assert.Equal(1500,performance.GetProperty("PresentationMs").GetInt32());
        Assert.Equal(6000,performance.GetProperty("StaggerMs").GetInt32());Assert.Equal(40000,performance.GetProperty("StabilizationMs").GetInt32());
        Assert.True(root.TryGetProperty("Theme",out var theme),"The report has no Theme id.");Assert.Equal("ocean-blue",theme.GetString());
        Assert.True(root.TryGetProperty("Capacity",out var capacity),"The report has no Capacity section.");
        Assert.Equal(7,capacity.GetProperty("SparePhysicalGiB").GetDouble());Assert.Equal(20,capacity.GetProperty("SparePhysicalPercent").GetDouble());
        Assert.Equal(1.5,capacity.GetProperty("NewClientPhysicalGiB").GetDouble());Assert.Equal(2.5,capacity.GetProperty("NewClientCommitGiB").GetDouble());
        Assert.Equal(10,capacity.GetProperty("SpareCommitPercent").GetDouble());Assert.Equal(2,capacity.GetProperty("SpareCommitGiB").GetDouble());
        Assert.Equal(4,capacity.GetProperty("UserMaximum").GetInt32());
        // Decoded string values catch a path even though JSON escapes its backslashes in the raw text.
        var strings=new List<string>();CollectStrings(root,strings);string decoded=string.Join("\n",strings);
        string[] forbidden=[label,Data,Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),Environment.UserName,Environment.MachineName,Environment.UserDomainName];
        foreach(string value in forbidden.Where(v=>v.Length>=4))
        {Assert.DoesNotContain(value,report,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain(value,decoded,StringComparison.OrdinalIgnoreCase);}
        Assert.DoesNotContain("\\",decoded,StringComparison.Ordinal);
    });
    private static void CollectStrings(JsonElement element,List<string> values)
    {
        switch(element.ValueKind)
        {
            case JsonValueKind.String:values.Add(element.GetString()!);break;
            case JsonValueKind.Object:foreach(var property in element.EnumerateObject()) {values.Add(property.Name);CollectStrings(property.Value,values);}break;
            case JsonValueKind.Array:foreach(var item in element.EnumerateArray()) CollectStrings(item,values);break;
        }
    }
    [Fact]
    public Task PauseAndClearAffectOnlyTheViewAndResumeGetsTheLatestBoundedHistory()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        vm.Navigate.Execute("Diagnostics");vm.RefreshDiagnostics.Execute(null);
        var before=vm.DiagnosticRows.Select(r=>r.Sequence).ToArray();Assert.NotEmpty(before);
        vm.PauseDiagnostics.Execute(null);Assert.True(vm.DiagnosticsPaused);
        await vm.SaveAccountAsync(AccountProfile.Create("Added while paused"));vm.RefreshDiagnostics.Execute(null);
        Assert.Equal(before,vm.DiagnosticRows.Select(r=>r.Sequence));
        vm.PauseDiagnostics.Execute(null);Assert.False(vm.DiagnosticsPaused);
        Assert.Contains(vm.DiagnosticRows,r=>r.Sequence>before.Max());
        vm.ClearDiagnostics.Execute(null);Assert.Empty(vm.DiagnosticRows);
        vm.RefreshDiagnostics.Execute(null);Assert.Empty(vm.DiagnosticRows);
        Assert.DoesNotContain("Info 0",vm.DiagnosticInformation);
        await vm.SaveAccountAsync(AccountProfile.Create("After clear"));vm.RefreshDiagnostics.Execute(null);
        Assert.NotEmpty(vm.DiagnosticRows);Assert.All(vm.DiagnosticRows,r=>Assert.True(r.Sequence>before.Max()));
        Assert.Contains("Clear view keeps",vm.DiagnosticStorageStatus);
    });
    [Fact]
    public Task ExportIsAtomicRedactedAndCannotReplaceApplicationData()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        string settings=Path.Combine(Data,"settings.json");byte[] originalSettings=await File.ReadAllBytesAsync(settings);
        vm.ExportDiagnostics.Execute(settings);await vm.ExportDiagnostics.Execution;
        Assert.Equal(originalSettings,await File.ReadAllBytesAsync(settings));Assert.Contains("outside the app",vm.ErrorNotice);
        vm.ExportDiagnostics.Execute("\\\\?\\"+settings);await vm.ExportDiagnostics.Execution;
        Assert.Equal(originalSettings,await File.ReadAllBytesAsync(settings));Assert.Contains("normal, nonredirected",vm.ErrorNotice);
        string folder=Path.Combine(_root,"share");Directory.CreateDirectory(folder);string report=Path.Combine(folder,"report.json");
        await File.WriteAllTextAsync(report,"old report");File.SetAttributes(report,FileAttributes.ReadOnly);
        try {vm.ExportDiagnostics.Execute(report);await vm.ExportDiagnostics.Execution;Assert.Equal("old report",await File.ReadAllTextAsync(report));}
        finally {File.SetAttributes(report,FileAttributes.Normal);}
        Assert.Empty(Directory.GetFiles(folder,"*.tmp"));
        vm.ExportDiagnostics.Execute(report);await vm.ExportDiagnostics.Execution;
        using var parsed=JsonDocument.Parse(await File.ReadAllTextAsync(report));Assert.Equal("Zenith Sessions Personal",parsed.RootElement.GetProperty("Product").GetString());
        Assert.Contains("report saved",vm.Notice);Assert.Empty(Directory.GetFiles(folder,"*.tmp"));
    });
    [Fact]
    public Task PanicOrLockDuringExportKeepsTheStopNotice()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        string folder=Path.Combine(_root,"share");Directory.CreateDirectory(folder);string report=Path.Combine(folder,"report.json");
        // Execute returns at the background write, so each stop lands while that export is still in flight.
        vm.ExportDiagnostics.Execute(report);vm.Panic.Execute(null);await vm.ExportDiagnostics.Execution;
        Assert.StartsWith("Panic stop",vm.Notice);Assert.Empty(Directory.GetFiles(folder,"*.tmp"));
        // A stop fences only the export it interrupted; the next explicit export still reports success.
        vm.ExportDiagnostics.Execute(report);await vm.ExportDiagnostics.Execution;Assert.Contains("report saved",vm.Notice);
        vm.ExportDiagnostics.Execute(report);vm.Lock.Execute(null);await vm.ExportDiagnostics.Execution;
        Assert.StartsWith("Controls locked",vm.Notice);Assert.Empty(Directory.GetFiles(folder,"*.tmp"));
    });
    [Fact]
    public Task ExportReplacesAReportBrieflyHeldByAnotherReader()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        string folder=Path.Combine(_root,"share");Directory.CreateDirectory(folder);string report=Path.Combine(folder,"report.json");
        await File.WriteAllTextAsync(report,"old report");
        // A scanner or viewer holding the old report without delete sharing blocks File.Replace until it lets go.
        var held=new FileStream(report,FileMode.Open,FileAccess.Read,FileShare.Read);
        var release=new Thread(()=>{Thread.Sleep(80);held.Dispose();}) {IsBackground=true};
        try {vm.ExportDiagnostics.Execute(report);release.Start();await vm.ExportDiagnostics.Execution;}
        finally {if(release.IsAlive)release.Join();held.Dispose();}
        Assert.Contains("report saved",vm.Notice);
        using var parsed=JsonDocument.Parse(await File.ReadAllTextAsync(report));Assert.Equal("Zenith Sessions Personal",parsed.RootElement.GetProperty("Product").GetString());
        Assert.Empty(Directory.GetFiles(folder,"*.tmp"));
    });
    [Fact]
    public Task ExportFailureInTheChosenReportFolderDoesNotBlameTheDataFolder()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        string folder=Path.Combine(_root,"share");Directory.CreateDirectory(folder);string report=Path.Combine(folder,"report.json");
        await File.WriteAllTextAsync(report,"old report");File.SetAttributes(report,FileAttributes.ReadOnly);
        try {vm.ExportDiagnostics.Execute(report);await vm.ExportDiagnostics.Execution;}
        finally {File.SetAttributes(report,FileAttributes.Normal);}
        Assert.Equal("old report",await File.ReadAllTextAsync(report));Assert.Empty(Directory.GetFiles(folder,"*.tmp"));
        Assert.DoesNotContain("data folder",vm.ErrorNotice);Assert.Contains("chosen folder",vm.ErrorNotice);Assert.DoesNotContain(folder,vm.ErrorNotice,StringComparison.OrdinalIgnoreCase);
        // The notice wrapper is not journaled: the row and the activity line keep the cause, so a denied target stays distinguishable.
        Assert.Contains("Operation failed (UnauthorizedAccessException).",vm.Events[0]);
        Assert.Equal("UnauthorizedAccessException",LatestFailureType(vm,ManagerSubsystem.Diagnostics));
        // A chosen folder that no longer exists is named as such, still without its path, and is not recreated.
        string gone=Path.Combine(_root,"gone");
        vm.ExportDiagnostics.Execute(Path.Combine(gone,"report.json"));await vm.ExportDiagnostics.Execution;
        Assert.DoesNotContain("data folder",vm.ErrorNotice);Assert.Contains("chosen folder",vm.ErrorNotice);Assert.DoesNotContain(gone,vm.ErrorNotice,StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(gone));
        Assert.Contains("Operation failed (DirectoryNotFoundException).",vm.Events[0]);
        Assert.Equal("IOException",LatestFailureType(vm,ManagerSubsystem.Diagnostics));
    });
    [Fact]
    public Task AnExportThatFailsAsLockLandsIsJournaledByItsCauseNotItsWrapper()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        string folder=Path.Combine(_root,"share");Directory.CreateDirectory(folder);string report=Path.Combine(folder,"report.json");
        await File.WriteAllTextAsync(report,"old report");File.SetAttributes(report,FileAttributes.ReadOnly);
        try
        {
            using var removal=new StagedReportRemoval(folder);
            // The write has already failed, but its continuation waits for this UI step, so Lock lands first.
            vm.ExportDiagnostics.Execute(report);removal.Wait();vm.Lock.Execute(null);await vm.ExportDiagnostics.Execution;
        }
        finally {File.SetAttributes(report,FileAttributes.Normal);}
        // The late branch kept the lock message and recorded the failure without a notice.
        Assert.StartsWith("Controls locked",vm.Notice);Assert.DoesNotContain("chosen folder",vm.ErrorNotice);
        vm.Unlock.Execute(null);
        Assert.Equal("UnauthorizedAccessException",LatestFailureType(vm,ManagerSubsystem.Diagnostics));
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AnExportThatFailsAfterPanicOrLockKeepsTheStopNoticeAndTheOldReport(bool locking)=>OnUi(async ()=>
    {
        // PanicOrLockDuringExportKeepsTheStopNotice covers a write that succeeds; this one fails after the stop has landed.
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        string folder=Path.Combine(_root,"share");Directory.CreateDirectory(folder);string report=Path.Combine(folder,"report.json");
        await File.WriteAllTextAsync(report,"old report");File.SetAttributes(report,FileAttributes.ReadOnly);
        string stop=locking?"Controls locked":"Panic stop";
        try
        {
            using var removal=new StagedReportRemoval(folder);
            // The write has already failed, but its continuation waits for this UI step, so the stop lands first.
            vm.ExportDiagnostics.Execute(report);removal.Wait();
            if(locking) vm.Lock.Execute(null);else vm.Panic.Execute(null);
            await vm.ExportDiagnostics.Execution;
        }
        finally {File.SetAttributes(report,FileAttributes.Normal);}
        // The stop owns the message: no error beside it and no failure line after it in the activity list.
        Assert.StartsWith(stop,vm.Notice);
        Assert.False(vm.HasErrorNotice,"An export that failed after the stop showed an error beside the stop notice.");
        Assert.DoesNotContain(vm.Events,line=>line.Contains("Operation failed",StringComparison.Ordinal));
        Assert.Equal("old report",await File.ReadAllTextAsync(report));Assert.Empty(Directory.GetFiles(folder,"*.tmp"));
        // The failure is still journaled once, by its cause, against Diagnostics.
        if(locking) vm.Unlock.Execute(null);
        vm.RefreshDiagnostics.Execute(null);
        var failure=Assert.Single(vm.DiagnosticRows,row=>row.Event.Kind==ManagerEventKind.OperationFailed && row.Event.Subsystem==ManagerSubsystem.Diagnostics);
        Assert.Equal("UnauthorizedAccessException",failure.Event.ErrorType);
    });
    private static string? LatestFailureType(ShellViewModel vm,ManagerSubsystem subsystem)
    {
        vm.RefreshDiagnostics.Execute(null);
        return vm.DiagnosticRows.Where(row=>row.Event.Kind==ManagerEventKind.OperationFailed && row.Event.Subsystem==subsystem)
            .MaxBy(row=>row.Event.Sequence)?.Event.ErrorType;
    }
    [Fact]
    public Task FailureRowsCopyAndReportNameWhichSubsystemFailed()=>OnUi(async ()=>
    {
        // Two real failures from different subsystems: a sampler fault and a read-only chosen report file.
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new FaultingMonitor());await vm.InitializeAsync();
        var until=DateTime.UtcNow.AddSeconds(3);
        while(!vm.Events.Any(e=>e.Contains("Operation failed (InvalidOperationException)",StringComparison.Ordinal)))
        {if(DateTime.UtcNow>until) throw new TimeoutException("The monitor fault was not reported.");await Task.Delay(20);}
        string folder=Path.Combine(_root,"share");Directory.CreateDirectory(folder);string report=Path.Combine(folder,"report.json");
        await File.WriteAllTextAsync(report,"old report");File.SetAttributes(report,FileAttributes.ReadOnly);
        try {vm.ExportDiagnostics.Execute(report);await vm.ExportDiagnostics.Execution;}
        finally {File.SetAttributes(report,FileAttributes.Normal);}
        vm.Navigate.Execute("Diagnostics");vm.RefreshDiagnostics.Execute(null);
        using(var parsed=JsonDocument.Parse(vm.BuildDiagnosticReport()))
        {
            var events=parsed.RootElement.GetProperty("Events").EnumerateArray().ToArray();
            var failures=events.Where(e=>e.GetProperty("Level").GetString()=="Error").ToArray();Assert.True(failures.Length>=2);
            Assert.All(failures,e=>Assert.True(e.TryGetProperty("Subsystem",out var value) && value.ValueKind==JsonValueKind.String,
                "A failure event in the report does not say which subsystem failed."));
            string[] subsystems=failures.Select(e=>e.GetProperty("Subsystem").GetString()!).ToArray();
            Assert.Contains("Telemetry",subsystems);Assert.Contains("Diagnostics",subsystems);
            // Only names from the closed enum appear, never free text.
            Assert.All(subsystems,name=>Assert.True(Enum.TryParse<ManagerSubsystem>(name,ignoreCase:false,out var parsed) && Enum.IsDefined(parsed)
                && parsed.ToString()==name,$"Unexpected subsystem value '{name}'."));
            // Routine events are not failures, so they name no subsystem.
            Assert.All(events.Where(e=>e.GetProperty("Level").GetString()=="Info"),e=>Assert.Equal(JsonValueKind.Null,e.GetProperty("Subsystem").ValueKind));
        }
        // Copy visible and the text filter use the same plain labels as the Subsystem column.
        string visible=vm.BuildVisibleDiagnosticText();
        Assert.Contains("Client monitoring",visible);Assert.Contains("Diagnostics",visible);
        Assert.Contains(vm.DiagnosticRows,r=>r.Event.Subsystem==ManagerSubsystem.Telemetry && r.Subsystem=="Client monitoring");
        Assert.Contains(vm.DiagnosticRows,r=>r.Event.Subsystem==ManagerSubsystem.Diagnostics && r.Subsystem=="Diagnostics");
        Assert.All(vm.DiagnosticRows.Where(r=>r.Level=="Info"),r=>Assert.Equal("—",r.Subsystem));
        vm.DiagnosticFilter="client monitoring";
        Assert.NotEmpty(vm.DiagnosticRows);Assert.All(vm.DiagnosticRows,r=>Assert.Equal(ManagerSubsystem.Telemetry,r.Event.Subsystem));
    });
    [Fact]
    public Task StartupFailureDoesNotLeaveDiagnosticsStartingOrClaimALiveView()=>OnUi(async ()=>
    {
        Directory.CreateDirectory(Data);await File.WriteAllTextAsync(Path.Combine(Data,"settings.json"),"damaged settings");
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        Assert.Equal("Startup needs attention",vm.SaveStatus);
        Assert.NotEqual("Diagnostics starting",vm.DiagnosticsStatus);Assert.Contains("Startup needs attention",vm.DiagnosticsStatus);
        vm.Navigate.Execute("Diagnostics");
        Assert.DoesNotContain("Live view",vm.DiagnosticViewStatus);Assert.StartsWith("Updates when refreshed",vm.DiagnosticViewStatus);
        long newest=vm.DiagnosticRows.Select(r=>r.Sequence).DefaultIfEmpty().Max();
        // No monitor loop pushes this view, so a later failure must show without pressing Refresh.
        vm.Fail(new IOException("x"),ManagerSubsystem.Library);
        Assert.Contains(vm.DiagnosticRows,r=>r.Sequence>newest && r.Level=="Error");
    });
    [Fact]
    public Task PresentationTickWithNoNewDiagnosticEventsRaisesNoDiagnosticLabels()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        vm.Navigate.Execute("Diagnostics");vm.RefreshDiagnostics.Execute(null);
        string[] labels=[nameof(vm.DiagnosticErrors),nameof(vm.DiagnosticWarnings),nameof(vm.DiagnosticInformation),
            nameof(vm.DiagnosticViewStatus),nameof(vm.DiagnosticStorageStatus)];
        var raised=new List<string>();
        vm.PropertyChanged+=(_,e)=>{if(e.PropertyName is { } name && labels.Contains(name)) raised.Add(name);};
        // Refresh forces a presentation tick, the same path the monitor loop takes while this tab is open.
        vm.Refresh.Execute(null);await vm.Refresh.Execution;
        Assert.Empty(raised);
        // A new event still reaches the labels and the rows on the next tick, without an explicit diagnostics refresh.
        long newest=vm.DiagnosticRows.Select(r=>r.Sequence).DefaultIfEmpty().Max();string information=vm.DiagnosticInformation;
        await vm.SaveAccountAsync(AccountProfile.Create("Saved between ticks"));
        vm.Refresh.Execute(null);await vm.Refresh.Execution;
        Assert.Contains(nameof(vm.DiagnosticInformation),raised);Assert.NotEqual(information,vm.DiagnosticInformation);
        Assert.Contains(vm.DiagnosticRows,r=>r.Sequence>newest && r.Event.Kind==ManagerEventKind.ProfileSaved);
    });
    [Fact]
    public Task MonitorFaultOnFirstSampleDoesNotFreezeDiagnosticsStatus()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new FaultingMonitor());await vm.InitializeAsync();
        var until=DateTime.UtcNow.AddSeconds(3);
        while(!vm.Events.Any(e=>e.Contains("Operation failed (InvalidOperationException)",StringComparison.Ordinal)))
        {if(DateTime.UtcNow>until) throw new TimeoutException("The monitor fault was not reported.");await Task.Delay(20);}
        Assert.NotEqual("Diagnostics starting",vm.DiagnosticsStatus);Assert.Contains("Monitoring stopped",vm.DiagnosticsStatus);
        vm.Navigate.Execute("Diagnostics");
        Assert.DoesNotContain("Live view",vm.DiagnosticViewStatus);Assert.StartsWith("Updates when refreshed",vm.DiagnosticViewStatus);
    });
    [Fact]
    public Task ARecoveredLogCorrelationFailureIsRecordedAsClientMonitoringByTypeOnly()=>OnUi(async ()=>
    {
        // The correlator reports from its own worker thread; the rows already show Unavailable, so no error notice is raised.
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        await Task.Run(()=>vm.RecordRecoveredLogCorrelationFailure(typeof(ArgumentException)));
        vm.Navigate.Execute("Diagnostics");vm.RefreshDiagnostics.Execute(null);
        var row=Assert.Single(vm.DiagnosticRows,r=>r.Event.Kind==ManagerEventKind.OperationFailed);
        Assert.Equal(ManagerSubsystem.Telemetry,row.Event.Subsystem);Assert.Equal("ArgumentException",row.Event.ErrorType);
        Assert.False(vm.HasErrorNotice);
    });
    [Fact]
    public Task ARestoreListenerThatStoppedIsRecordedByTypeAndShownOnDiagnostics()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        vm.Navigate.Execute("Diagnostics");
        // The listener reports from its own task, never the UI thread: first when a run of faults begins.
        await Task.Run(()=>vm.RestoreListenerFaulted(typeof(IOException),stopped:false));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Contains(vm.DiagnosticRows,r=>r.Event.Subsystem==ManagerSubsystem.RepeatLaunch && r.ErrorType=="IOException" && r.Subsystem=="Repeat launch");
        Assert.DoesNotContain(ShellViewModel.RepeatLaunchStoppedStatus,vm.DiagnosticsStatus);
        // Then again when the run stops the listener: from here on a new launch only says "already starting".
        await Task.Run(()=>vm.RestoreListenerFaulted(typeof(IOException),stopped:true));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(2,vm.DiagnosticRows.Count(r=>r.Event.Subsystem==ManagerSubsystem.RepeatLaunch));
        Assert.Contains(ShellViewModel.RepeatLaunchStoppedStatus,vm.DiagnosticsStatus);
        // A later status refresh keeps saying so.
        vm.RefreshDiagnostics.Execute(null);Assert.Contains(ShellViewModel.RepeatLaunchStoppedStatus,vm.DiagnosticsStatus);
    });
    [Fact]
    public Task AHealthyMonitorKeepsTheLiveDiagnosticsWording()=>OnUi(async ()=>
    {
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        var until=DateTime.UtcNow.AddSeconds(3);
        while(vm.DiagnosticsStatus=="Diagnostics starting")
        {if(DateTime.UtcNow>until) throw new TimeoutException("The first sample did not update the diagnostics status.");await Task.Delay(20);}
        Assert.StartsWith("Diagnostics active",vm.DiagnosticsStatus);Assert.DoesNotContain("refreshed",vm.DiagnosticsStatus);
        vm.Navigate.Execute("Diagnostics");Assert.StartsWith("Live view",vm.DiagnosticViewStatus);
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AnEntryLostToADiskErrorIsNotCalledBusyOrRoutineOnceTheLogRecovers(bool monitoringStopped)=>OnUi(async ()=>
    {
        // The lost entry is a failure record, and nothing was busy: another program held the log for one write.
        IDesktopMonitor monitor=monitoringStopped?new FaultingMonitor():new SyntheticMonitor();
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,monitor);await vm.InitializeAsync();
        string log=Path.Combine(Data,"diagnostics","manager.jsonl");
        await UntilAsync(()=>File.Exists(log));
        vm.Navigate.Execute("Diagnostics");
        if(monitoringStopped) await UntilAsync(()=>{vm.RefreshDiagnostics.Execute(null);return vm.DiagnosticsStatus.Contains("Monitoring stopped",StringComparison.Ordinal);});
        FileStream? blocker=null;
        await UntilAsync(()=>(blocker=TryOpenDenyingWriters(log)) is not null);
        using(blocker)
        {
            vm.Fail(new IOException("synthetic"),ManagerSubsystem.Library);
            await UntilAsync(()=>{vm.RefreshDiagnostics.Execute(null);return vm.DiagnosticsStatus.Contains("another program",StringComparison.Ordinal);});
        }
        // The next successful write clears the failure text, so the header falls back to the skip count.
        await vm.SaveAccountAsync(AccountProfile.Create("Saved after the log was released"));
        await UntilAsync(()=>{vm.RefreshDiagnostics.Execute(null);return !vm.DiagnosticsStatus.Contains("another program",StringComparison.Ordinal);});
        using var report=JsonDocument.Parse(vm.BuildDiagnosticReport());
        long lost=report.RootElement.GetProperty("DroppedEvents").GetInt64();Assert.True(lost>=1,"No entry was lost to the held log.");
        Assert.DoesNotContain("busy",vm.DiagnosticsStatus,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("routine",vm.DiagnosticsStatus,StringComparison.OrdinalIgnoreCase);
        string unwritten=$"{(lost==1?"1 event":$"{lost} events")} not saved to disk";
        Assert.Equal(monitoringStopped?$"Monitoring stopped · {unwritten} · diagnostics still recording · view updates when refreshed"
            :$"Diagnostics active · {unwritten}",vm.DiagnosticsStatus);
    });
    [Theory]
    [InlineData(true,false,false,0L,"Diagnostics active")]
    [InlineData(true,false,false,1L,"Diagnostics active")]
    [InlineData(true,false,false,123456L,"Diagnostics active")]
    [InlineData(false,false,false,0L,"Diagnostics starting")]
    [InlineData(false,true,false,0L,"monitoring stopped")]
    [InlineData(false,true,false,1L,"monitoring stopped")]
    [InlineData(false,true,false,123456L,"monitoring stopped")]
    [InlineData(false,false,true,123456L,"startup needs attention")]
    public Task TheDiagnosticsHeaderShowsTheStateAndTheSkipCountBeforeItIsTrimmed(bool live,bool monitorStopped,bool startupNeedsAttention,long dropped,string state)=>OnUi(()=>
    {
        // The page header is one trimmed caption line and only its tooltip holds the rest, so the state (a stopped
        // monitor above all) and the count of events missing from the disk log must both be readable without hovering.
        string status=ShellViewModel.DiagnosticsStatusText(null,live,monitorStopped,startupNeedsAttention,dropped,restoreListenerStopped:false);
        string visible=VisibleDiagnosticsHeaderText(status);
        string[] parts=status.Split(" · ");
        string Fact(string text)=>parts.Single(part=>part.Contains(text,StringComparison.OrdinalIgnoreCase)).TrimEnd('.');
        Assert.Contains(Fact(state),visible,StringComparison.Ordinal);
        if(dropped>0) Assert.Contains(Fact($"{dropped} event"),visible,StringComparison.Ordinal);
        return Task.CompletedTask;
    });
    // What the Diagnostics header draws before its ellipsis: the shipped CaptionLine style within MainWindow's MaxWidth.
    private static string VisibleDiagnosticsHeaderText(string status)
    {
        var window=System.Xml.Linq.XDocument.Load(Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"src","Zenith.Sessions.App","MainWindow.xaml"));
        var header=window.Descendants().Single(e=>e.Name.LocalName=="TextBlock" && (string?)e.Attribute("Text")=="{Binding DiagnosticsStatus}"
            && (string?)e.Attribute("Style")=="{StaticResource CaptionLine}");
        double maxWidth=double.Parse((string)header.Attribute("MaxWidth")!,System.Globalization.CultureInfo.InvariantCulture);
        var resources=(System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml",UriKind.Relative));
        var style=(System.Windows.Style)resources["CaptionLine"];
        double Width(string text)
        {
            var block=new System.Windows.Controls.TextBlock {Style=style,Text=text};
            block.Measure(new System.Windows.Size(double.PositiveInfinity,double.PositiveInfinity));
            return block.DesiredSize.Width;
        }
        if(Width(status)<=maxWidth) return status;
        string ellipsis=((char)0x2026).ToString();int fit=0;
        while(fit<status.Length && Width(status[..(fit+1)]+ellipsis)<=maxWidth) fit++;
        return status[..fit];
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        // Bounded wait for the background writer; the disk has no completion event to observe.
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(!condition())
        {
            if(DateTime.UtcNow>deadline) throw new TimeoutException("The diagnostics writer did not reach the expected state.");
            await Task.Delay(10);
        }
    }
    private static FileStream? TryOpenDenyingWriters(string path)
    {
        try {return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);}
        catch(IOException) {return null;}
    }
    // The end-to-end check that share-link and private-server codes never reach diagnostics is
    // ClientWindowIntegrationTests.ShareLinkAndSavedPrivateServerFlowsLeaveNoCodeInTheDataFolderOrDiagnostics.
    [Fact]
    public Task LockThenUnlockRecordsBothTransitionsAndAnUnlockWhileUnlockedRecordsNothing()=>OnUi(async ()=>
    {
        // A reader of the Diagnostics page, a copied report or manager.jsonl must see when the lock ended,
        // so events after it are not read as happening while the dashboard was still locked.
        await using var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        vm.Unlock.Execute(null);
        vm.LockDashboard();Assert.True(vm.IsLocked);
        vm.Unlock.Execute(null);Assert.False(vm.IsLocked);
        vm.Unlock.Execute(null);
        vm.RefreshDiagnostics.Execute(null);
        var transitions=vm.DiagnosticRows.Where(r=>r.Event.Kind.ToString().StartsWith("Controls",StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[]{"ControlsLocked","ControlsUnlocked"},transitions.Select(r=>r.Event.Kind.ToString()));
        Assert.True(transitions[1].Sequence>transitions[0].Sequence);
        Assert.Equal("Manager controls unlocked.",transitions[1].Description);Assert.Equal("Info",transitions[1].Level);
        Assert.Contains("\"ControlsUnlocked\"",vm.BuildDiagnosticReport());
    });
    [Fact]
    public Task ShutdownAndFaultAreJournaledWhenAnOwnerFailsDuringDispose()=>OnUi(async ()=>
    {
        var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor(),signIn:new FailingDisposeSignIn());
        await vm.InitializeAsync();
        // An owner whose cleanup cannot be confirmed no longer ends exit early: its stop is recorded by type, the owners after it
        // still stop, and Shutdown is still the last record on disk.
        await vm.DisposeAsync();
        string[] lines=(await File.ReadAllLinesAsync(Path.Combine(Data,"diagnostics","manager.jsonl"))).Where(l=>l.Length>0).ToArray();
        Assert.True(lines.Length>=2,"The journal has no shutdown entries.");
        var records=lines.Select(l=>JsonDocument.Parse(l)).ToArray();
        try
        {
            Assert.Contains(records,r=>r.RootElement.GetProperty("Kind").GetString()=="OperationFailed"
                && r.RootElement.GetProperty("ErrorType").GetString()=="InvalidOperationException"); // The disk format keeps four properties; the subsystem stays in memory.
            Assert.Equal("Shutdown",records[^1].RootElement.GetProperty("Kind").GetString());
        }
        finally { foreach(var record in records) record.Dispose(); }
    });
    private sealed class FailingDisposeSignIn : IRobloxSessionSignIn,IAsyncDisposable
    {
        public Task<RobloxSessionCredential> SignInAsync(Guid profileId,CancellationToken ct)=>throw new NotSupportedException("No sign-in expected.");
        public ValueTask DisposeAsync()=>ValueTask.FromException(new InvalidOperationException("synthetic owner cleanup could not be confirmed"));
    }
    private sealed class FaultingMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)=>Task.FromException<DesktopSnapshot>(new InvalidOperationException("synthetic sampler fault"));
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
    }
    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)=>Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,20),[]));
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
    }
    private static Task OnUi(Func<Task> work)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;Task operation=Task.CompletedTask;
            dispatcher.BeginInvoke(()=>operation=RunAsync());
            async Task RunAsync(){try{await work();done.TrySetResult();}catch(Exception ex){done.TrySetException(ex);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}}
            Dispatcher.Run();operation.GetAwaiter().GetResult();
        }) {IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-diagnostic-ui-tests-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
