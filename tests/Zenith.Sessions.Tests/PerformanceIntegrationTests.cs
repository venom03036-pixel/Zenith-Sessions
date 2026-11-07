using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class PerformanceIntegrationTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-performance-ui-tests-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task OldSettingsWithoutPerformanceFieldsRetainBalancedDefaultsAndCapacity()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root,"settings.json"),"""{"Version":1,"Theme":"ocean-blue","DenseAccounts":true,"Capacity":{"SparePhysicalGiB":7}}""");
        using var store=new SettingsStore(_root);var loaded=await store.LoadAsync(default);
        Assert.Equal(ManagerPerformanceProfile.Balanced,loaded.Settings.Performance!.Profile);
        Assert.Equal(7,loaded.Settings.Capacity!.SparePhysicalGiB);Assert.Equal("ocean-blue",loaded.Settings.Theme);Assert.True(loaded.Settings.DenseAccounts);
        Assert.DoesNotContain("Performance",await File.ReadAllTextAsync(store.Path));
    }
    [Fact]
    public Task DraftProfileAndCustomFieldsAreNotAppliedByAnUnrelatedThemeSave()=>OnUi(async ()=>
    {
        var monitor=new FixedMonitor();await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,monitor);
        await vm.InitializeAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        vm.SelectedPerformanceProfile=vm.PerformanceProfiles.Single(p=>p.Profile==ManagerPerformanceProfile.HighPerformance);
        Assert.Contains("Balanced",vm.ActivePerformanceName);Assert.Equal("Unsaved settings",vm.SaveStatus);
        vm.ApplyTheme.Execute("amber-night");await vm.ApplyTheme.Execution;
        Assert.Equal(1000,monitor.Applied!.ProcessSampleMs);Assert.Equal("Unsaved settings",vm.SaveStatus);
        vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;
        Assert.Equal(500,monitor.Applied.ProcessSampleMs);Assert.Equal("Settings saved",vm.SaveStatus);
        using var store=new SettingsStore(_root);Assert.Equal(ManagerPerformanceProfile.HighPerformance,(await store.LoadAsync(default)).Settings.Performance!.Profile);
        vm.SelectedPerformanceProfile=vm.PerformanceProfiles.Single(p=>p.Profile==ManagerPerformanceProfile.Custom);
        vm.CustomProcessSample="2500";vm.CustomPresentation="1500";vm.CustomStagger="6000";vm.CustomStabilization="40000";
        vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;
        Assert.Equal(2500,monitor.Applied.ProcessSampleMs);Assert.Equal(1500,monitor.Applied.PresentationMs);
        Assert.Equal(6000,monitor.Applied.StaggerMs);Assert.Equal(40000,monitor.Applied.StabilizationMs);
        Assert.Equal(1000,monitor.Applied.EssentialResourceSampleMs);Assert.Equal(3000,monitor.Applied.ResourceSampleExpiryMs);
    });
    [Fact]
    public Task MissingMainAndInvalidOrUnwrittenDraftCannotMasqueradeAsAppliedPolicy()=>OnUi(async ()=>
    {
        var monitor=new FixedMonitor();await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,monitor);await vm.InitializeAsync();
        vm.SelectedPerformanceProfile=vm.PerformanceProfiles.Single(p=>p.Profile==ManagerPerformanceProfile.MainAccountPriority);
        vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;
        Assert.Contains("choose a main account",vm.ActivePerformanceName);Assert.Equal(0,monitor.Applied!.MainAccountPriorityBoost);
        vm.SelectedPerformanceProfile=vm.PerformanceProfiles.Single(p=>p.Profile==ManagerPerformanceProfile.Custom);
        vm.CustomProcessSample="invalid";vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;
        Assert.Equal(ManagerPerformanceProfile.MainAccountPriority,monitor.Applied.Profile);
        Assert.Contains("whole number",vm.ErrorNotice);Assert.NotEqual("Settings saved",vm.SaveStatus);
        vm.ResetCustomPerformance.Execute(null);vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;
        Assert.Equal(ManagerPerformanceProfile.Custom,monitor.Applied.Profile);
        string file=Path.Combine(_root,"settings.json");File.SetAttributes(file,FileAttributes.ReadOnly);
        try
        {
            vm.SelectedPerformanceProfile=vm.PerformanceProfiles.Single(p=>p.Profile==ManagerPerformanceProfile.HighPerformance);
            vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;
            Assert.Equal("Save failed — retry",vm.SaveStatus);Assert.Equal(ManagerPerformanceProfile.Custom,monitor.Applied.Profile);
        }
        finally {File.SetAttributes(file,FileAttributes.Normal);}
    });
    [Fact]
    public Task ExplicitMainProfilePersistsWithoutBecomingAuthenticationAndDeletedMainRequiresReview()=>OnUi(async ()=>
    {
        Guid main;
        await using(var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new FixedMonitor()))
        {
            await vm.InitializeAsync();await vm.SaveAccountAsync(AccountProfile.Create("Main alias"));main=vm.SelectedAccount!.Profile.Id;
            vm.SetSelectedAsMain.Execute(null);vm.SelectedPerformanceProfile=vm.PerformanceProfiles.Single(p=>p.Profile==ManagerPerformanceProfile.MainAccountPriority);
            vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;
            Assert.Equal(main,vm.MainAccountProfileId);Assert.Null(vm.SelectedAccount.KnownUserId);Assert.Equal("Not linked",vm.SelectedAccount.Identity);
        }
        await using(var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new FixedMonitor()))
        {
            await vm.InitializeAsync();Assert.Equal(main,vm.MainAccountProfileId);Assert.Equal("Main alias",vm.MainAccountDisplay);
            await vm.DeleteAccountsAsync(vm.Accounts.ToArray());Assert.Contains("unavailable",vm.MainAccountDisplay);
            vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;Assert.Contains("no longer exists",vm.ErrorNotice);
            vm.ClearMainAccount.Execute(null);vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;
            Assert.Equal("Settings saved",vm.SaveStatus);Assert.Null(vm.MainAccountProfileId);
        }
    });
    [Fact]
    public Task FailedExplicitSaveCannotLeakRevertedFieldsIntoAnAppearanceSave()=>OnUi(async ()=>
    {
        var monitor=new FixedMonitor();await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,monitor);
        await vm.InitializeAsync();
        string file=Path.Combine(_root,"settings.json");File.SetAttributes(file,FileAttributes.ReadOnly);
        try
        {
            vm.SelectedPerformanceProfile=vm.PerformanceProfiles.Single(p=>p.Profile==ManagerPerformanceProfile.HighPerformance);
            vm.SpareRam="5";vm.OAuthClientId="1234";vm.CallbackPort="47636";
            vm.SaveSettings.Execute(null);await vm.SaveSettings.Execution;
            Assert.Equal("Save failed — retry",vm.SaveStatus);
            Assert.Equal(ManagerPerformanceProfile.Balanced,monitor.Applied!.Profile);
            vm.SelectedPerformanceProfile=vm.PerformanceProfiles.Single(p=>p.Profile==ManagerPerformanceProfile.Balanced);
            vm.SpareRam="3";vm.OAuthClientId="";vm.CallbackPort="47635";
        }
        finally {File.SetAttributes(file,FileAttributes.Normal);}
        vm.ApplyTheme.Execute("amber-night");await vm.ApplyTheme.Execution;
        using var store=new SettingsStore(_root);var saved=(await store.LoadAsync(default)).Settings;
        Assert.Equal(ManagerPerformanceProfile.Balanced,saved.Performance!.Profile);
        Assert.Equal(3,saved.Capacity!.SparePhysicalGiB);Assert.Empty(saved.OAuthClientId);Assert.Equal(47635,saved.OAuthCallbackPort);
        Assert.Equal("amber-night",saved.Theme);Assert.Equal(ManagerPerformanceProfile.Balanced,monitor.Applied!.Profile);
        Assert.Equal("Settings saved",vm.SaveStatus);
    });
    [Fact]
    public Task AppearanceSaveQueuedBehindExplicitSaveRetainsNewlyCommittedFields()=>OnUi(async ()=>
    {
        var monitor=new FixedMonitor();await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,monitor);
        await vm.InitializeAsync();
        vm.SelectedPerformanceProfile=vm.PerformanceProfiles.Single(p=>p.Profile==ManagerPerformanceProfile.HighPerformance);
        vm.SpareRam="5";vm.SaveSettings.Execute(null);vm.ApplyTheme.Execute("ocean-blue");
        await Task.WhenAll(vm.SaveSettings.Execution,vm.ApplyTheme.Execution);
        using var store=new SettingsStore(_root);var saved=(await store.LoadAsync(default)).Settings;
        Assert.Equal(ManagerPerformanceProfile.HighPerformance,saved.Performance!.Profile);
        Assert.Equal(5,saved.Capacity!.SparePhysicalGiB);Assert.Equal("ocean-blue",saved.Theme);
        Assert.Equal("Settings saved",vm.SaveStatus);Assert.Equal(ManagerPerformanceProfile.HighPerformance,monitor.Applied!.Profile);
    });
    [Fact]
    public Task DistinctResourceFrameWithTheSameTimestampCannotReuseHealthyAdmission()=>OnUi(async ()=>
    {
        var monitor=new FixedMonitor();await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,monitor);
        await vm.InitializeAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Contains("healthy",vm.PressureNotice,StringComparison.OrdinalIgnoreCase);
        monitor.Snapshot=monitor.Snapshot with {Resources=monitor.Snapshot.Resources with {CpuPercent=null}};
        vm.Refresh.Execute(null);await vm.Refresh.Execution;
        Assert.Contains("held",vm.PressureNotice,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("coherent",vm.PressureNotice,StringComparison.OrdinalIgnoreCase);
    });
    [Fact]
    public Task DuplicateEssentialSnapshotDoesNotResetPressureRecoveryOrCreateAFalseHold()=>OnUi(async ()=>
    {
        var monitor=new FixedMonitor();await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,monitor);
        await vm.InitializeAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Contains("healthy",vm.PressureNotice,StringComparison.OrdinalIgnoreCase);
        vm.Refresh.Execute(null);await vm.Refresh.Execution;
        vm.Refresh.Execute(null);await vm.Refresh.Execution;
        Assert.True(monitor.Calls>=3);Assert.Contains("healthy",vm.PressureNotice,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("waiting for coherent",vm.PressureNotice,StringComparison.OrdinalIgnoreCase);
    });
    private sealed class FixedMonitor : IConfigurableDesktopMonitor
    {
        public DesktopSnapshot Snapshot=new(new(DateTimeOffset.UtcNow,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,20),[]);
        public ManagerPerformancePolicy? Applied;public int Calls;
        public void ConfigurePerformance(ManagerPerformancePolicy policy)=>Applied=policy.Validated();
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) {ct.ThrowIfCancellationRequested();Calls++;return Task.FromResult(Snapshot);}
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
            async Task RunAsync() {try {await work();done.TrySetResult();}catch(Exception ex) {done.TrySetException(ex);}finally {dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}}
            Dispatcher.Run();operation.GetAwaiter().GetResult();
        }) {IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-performance-ui-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
