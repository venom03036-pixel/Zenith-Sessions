using System.IO;
using System.Reflection;
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
        Assert.Equal(1,parsed.RootElement.GetProperty("FormatVersion").GetInt32());
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
        Assert.Equal(originalSettings,await File.ReadAllBytesAsync(settings));Assert.Contains("outside the app",vm.Notice);
        vm.ExportDiagnostics.Execute("\\\\?\\"+settings);await vm.ExportDiagnostics.Execution;
        Assert.Equal(originalSettings,await File.ReadAllBytesAsync(settings));Assert.Contains("normal, nonredirected",vm.Notice);
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
        Assert.DoesNotContain("data folder",vm.Notice);Assert.Contains("chosen folder",vm.Notice);Assert.DoesNotContain(folder,vm.Notice,StringComparison.OrdinalIgnoreCase);
        // A chosen folder that no longer exists is named as such, still without its path, and is not recreated.
        string gone=Path.Combine(_root,"gone");
        vm.ExportDiagnostics.Execute(Path.Combine(gone,"report.json"));await vm.ExportDiagnostics.Execution;
        Assert.DoesNotContain("data folder",vm.Notice);Assert.Contains("chosen folder",vm.Notice);Assert.DoesNotContain(gone,vm.Notice,StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(gone));
    });
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
        Assert.NotEqual("Diagnostics starting",vm.DiagnosticsStatus);Assert.Contains("startup needs attention",vm.DiagnosticsStatus);
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
        Assert.NotEqual("Diagnostics starting",vm.DiagnosticsStatus);Assert.Contains("monitoring stopped",vm.DiagnosticsStatus);
        vm.Navigate.Execute("Diagnostics");
        Assert.DoesNotContain("Live view",vm.DiagnosticViewStatus);Assert.StartsWith("Updates when refreshed",vm.DiagnosticViewStatus);
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
    [Fact]
    public Task ShareLinkCheckSaveFailureAndRemoveNeverWriteCodesToAnyDiagnosticsOutput()=>OnUi(async ()=>
    {
        // Synthetic values only. Every directory, client and store is a local fake; no account signs in and nothing reaches the network.
        const string Code="Q7wE9rT2yU4iO6pA8sD1fG3hJ5kL0zXc",ShareCode="5d3f8a1b7c9e2f4a6b8c0d1e3f5a7b9c";
        const string ShareUrl="https://www.roblox.com/share?code="+ShareCode+"&type=Server";
        var client=new ShareClient(Code,ShareUrl);var store=new FailingReadStore(Code);var shown=new System.Text.StringBuilder();
        var vm=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor(),new NoPublicDirectory(),new NoServers(),
            shareLinkClient:client,privateServerStore:store);
        void Capture()=>shown.AppendLine(vm.Notice).AppendLine(vm.ShareLink.Status).AppendLine(vm.ShareLink.ResultTitle)
            .AppendLine(vm.ShareLink.ResultDetail).AppendLine(vm.DestinationSearch.Status);
        try
        {
            await vm.InitializeAsync();await vm.SaveAccountAsync(AccountProfile.Create("Synthetic share tester"));
            Guid profile=vm.SelectedAccount!.Profile.Id;
            using(var vault=new ProtectedCredentialVault(Data))
                await vault.SaveAsync(new(profile,RobloxSessionIdentity.Provider,123,1,"","",DateTimeOffset.MaxValue,"synthetic_player",
                    AuthorizationHealth.Linked,RobloxSessionIdentity.Issuer,"123","synthetic-session-value"),default);
            // Opening Join a game reads the saved list once; this store's read fails with a message that quotes the code.
            vm.Navigate.Execute("JoinGame");vm.SetPreviewWindowVisible(true);await vm.ShareLink.ActiveOperation;Capture();
            vm.ShareLink.LinkInput=ShareUrl;await vm.ShareLink.CheckAsync();Capture();Assert.True(vm.ShareLink.HasPrivateResult);
            await vm.ShareLink.SaveAsFavoriteAsync();Capture();Assert.Single(vm.ShareLink.Saved);
            vm.ShareLink.LinkInput=ShareUrl;await vm.ShareLink.CheckAsync();Capture(); // The client now throws, quoting the code and the link.
            await vm.ShareLink.LoadSavedAsync();Capture();
            vm.ShareLink.SelectedSaved=vm.ShareLink.Saved[0];await vm.ShareLink.RemoveSelectedAsync();Capture();Assert.Empty(vm.ShareLink.Saved);
            vm.ServerBrowser.LinkInput=ShareUrl;await vm.DestinationSearch.SearchOrUseAsync();Capture(); // The Find box route.
            Assert.Equal(3,client.Calls);Assert.Equal(1,store.Adds);Assert.Equal(1,store.Deletes);Assert.Equal(2,store.Reads);
            // One synthetic launch request gives the evidence report something to export.
            typeof(ShellViewModel).GetField("_latestQueue",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm,new LaunchQueueSnapshot(true,
                [new(new(Guid.NewGuid(),profile,1,123,42,0,DateTimeOffset.UtcNow,1,HasExplicitDestination:true),LaunchState.Blocked,LaunchProblem.PreparationRejected,
                    DateTimeOffset.UtcNow,ResourceBudget:new(1,1))]));
            typeof(ShellViewModel).GetMethod("RefreshLaunchEvidence",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,null);
            vm.Navigate.Execute("Diagnostics");vm.RefreshDiagnostics.Execute(null);
            shown.AppendLine(vm.BuildDiagnosticReport()).AppendLine(vm.BuildVisibleDiagnosticText()).AppendLine(vm.BuildNativeTrialEvidenceReport());
            foreach(string line in vm.Events) shown.AppendLine(line);
            Capture();
            // Without recorded failures an empty log would pass, so the failures must be visible as typed events.
            Assert.Contains(vm.DiagnosticRows,r=>r.Event.Kind==ManagerEventKind.OperationFailed);
        }
        finally {await vm.DisposeAsync();} // Drains the diagnostics writer before its files are read.
        string folder=Path.Combine(Data,"diagnostics");string[] files=Directory.GetFiles(folder);
        Assert.Contains(Path.Combine(folder,"manager.jsonl"),files);
        string written=string.Concat(files.Select(File.ReadAllText));Assert.Contains("\"OperationFailed\"",written,StringComparison.Ordinal);
        // Only the last four characters of a private server code may ever be shown; the share code, link and query never.
        foreach(string forbidden in new[]{Code[..^4],ShareCode,ShareUrl,"?code="})
        {Assert.DoesNotContain(forbidden,shown.ToString(),StringComparison.Ordinal);Assert.DoesNotContain(forbidden,written,StringComparison.Ordinal);}
    });
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
        // An owner whose cleanup cannot be confirmed fails exit the same way the launch queue's stop does.
        await Assert.ThrowsAsync<InvalidOperationException>(()=>vm.DisposeAsync().AsTask());
        string[] lines=(await File.ReadAllLinesAsync(Path.Combine(Data,"diagnostics","manager.jsonl"))).Where(l=>l.Length>0).ToArray();
        Assert.True(lines.Length>=2,"The journal has no shutdown entries.");
        using var fault=JsonDocument.Parse(lines[^2]);using var shutdown=JsonDocument.Parse(lines[^1]);
        Assert.Equal("OperationFailed",fault.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("InvalidOperationException",fault.RootElement.GetProperty("ErrorType").GetString());
        Assert.Equal("Shutdown",shutdown.RootElement.GetProperty("Kind").GetString());
    });
    private sealed class FailingDisposeSignIn : IRobloxSessionSignIn,IAsyncDisposable
    {
        public Task<RobloxSessionCredential> SignInAsync(Guid profileId,CancellationToken ct)=>throw new NotSupportedException("No sign-in expected.");
        public ValueTask DisposeAsync()=>ValueTask.FromException(new InvalidOperationException("synthetic owner cleanup could not be confirmed"));
    }
    private sealed class ShareClient(string code,string link) : IRobloxShareLinkClient
    {
        public int Calls;
        public void ForgetTokens(Guid profileId) { }
        public void ForgetAllTokens() { }
        // The first check resolves a private server; every later one fails with transport text that quotes the code and the link.
        public Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink shareLink,RobloxSessionCredential credential,RobloxShareLinkTokenScope scope,CancellationToken ct)
            =>++Calls==1?Task.FromResult(new RobloxShareLinkResolution(RobloxShareLinkOutcome.PrivateServer,42,43,44,new ProtectedPrivateServerCode(code)))
                :Task.FromException<RobloxShareLinkResolution>(new IOException("synthetic transport failure for "+link+" code "+code));
    }
    private sealed class FailingReadStore(string code) : IPrivateServerStore
    {
        private readonly List<SavedPrivateServer> _items=[];
        public int Reads,Adds,Deletes;
        // InvalidDataException is a type ShellViewModel.Fail would show verbatim, so forwarding it there would put the code in Notice.
        public Task<IReadOnlyList<SavedPrivateServer>> ReadAsync(CancellationToken ct)
        {Reads++;return Task.FromException<IReadOnlyList<SavedPrivateServer>>(new InvalidDataException("synthetic damaged list near code "+code));}
        public Task<SavedPrivateServer> AddAsync(string name,long placeId,long universeId,long? ownerUserId,ProtectedPrivateServerCode protectedCode,CancellationToken ct)
        {
            Adds++;var saved=new SavedPrivateServer(Guid.NewGuid(),SavedPrivateServer.NormalizeName(name),placeId,universeId,ownerUserId,protectedCode.Last4,1).Validated();
            _items.Add(saved);return Task.FromResult(saved);
        }
        public Task DeleteAsync(Guid id,long revision,CancellationToken ct)
        {Deletes++;if(_items.RemoveAll(item=>item.Id==id && item.Revision==revision)==0) throw new PrivateServerConflictException();return Task.CompletedTask;}
        public Task<ProtectedPrivateServerCode> ReadCodeAsync(Guid id,CancellationToken ct)=>throw new PrivateServerConflictException();
    }
    private sealed class NoPublicDirectory : IPublicRobloxDirectory
    {
        public Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long id,CancellationToken ct)=>throw new InvalidOperationException("No network expected.");
        public Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long id,CancellationToken ct)=>throw new InvalidOperationException("No network expected.");
        public Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar,CancellationToken ct)=>throw new InvalidOperationException("No network expected.");
        public void ClearMemoryCache() { }
    }
    private sealed class NoServers : IRobloxPublicServerDirectory
    {public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query,CancellationToken ct)=>throw new InvalidOperationException("No network expected.");}
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
