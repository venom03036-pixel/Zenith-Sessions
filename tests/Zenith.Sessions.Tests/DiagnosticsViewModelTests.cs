using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
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
        await vm.SaveAccountAsync(AccountProfile.Create(secret));vm.Fail(new IOException(secret));
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
