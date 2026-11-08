using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class PerformanceIntegrationTests
{
    [Fact]
    public Task ImportedReviewShowsEveryValueAndAppliesOnlyToDraftWithTheCurrentLocalMain() => OnUi(async () =>
    {
        var monitor = new FixedMonitor(); await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor);
        await vm.InitializeAsync();
        await vm.SaveAccountAsync(AccountProfile.Create("First local main")); vm.SetSelectedAsMain.Execute(null);
        var portable = new PortablePerformanceSettings(ManagerPerformanceProfile.Balanced, 5000, 2000, 30000, 120000);
        vm.PerformanceShareInput = PerformanceSettingsTransfer.EncodeShareCode(portable);
        vm.PreviewPerformanceCode.Execute(null);
        Assert.True(vm.HasPerformanceReview); Assert.Contains("Balanced", vm.PerformanceReviewSummary);
        foreach (var timing in new[] { "5000", "2000", "30000", "120000" }) Assert.Contains(timing, vm.PerformanceReviewSummary);
        Assert.Contains("dormant", vm.PerformanceReviewSummary);
        Assert.Equal("1000", vm.CustomProcessSample); Assert.Equal(1000, monitor.Applied!.ProcessSampleMs);
        await vm.SaveAccountAsync(AccountProfile.Create("Second local main")); vm.SetSelectedAsMain.Execute(null);
        Guid currentMain = vm.SelectedAccount!.Profile.Id;
        vm.SpareRam = "7"; vm.UsePerformanceReview.Execute(null);
        Assert.False(vm.HasPerformanceReview); Assert.Equal(currentMain, vm.MainAccountProfileId);
        Assert.Equal("5000", vm.CustomProcessSample); Assert.Equal("7", vm.SpareRam);
        Assert.Equal(1000, monitor.Applied.ProcessSampleMs); Assert.Equal("Unsaved settings", vm.SaveStatus);
        vm.ApplyTheme.Execute("ocean-blue"); await vm.ApplyTheme.Execution;
        Assert.Equal("5000", vm.CustomProcessSample); Assert.Equal(1000, monitor.Applied.ProcessSampleMs);
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved", vm.SaveStatus);
        vm.SelectedPerformanceProfile = vm.PerformanceProfiles.Single(item => item.Profile == ManagerPerformanceProfile.Custom);
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal(5000, monitor.Applied.ProcessSampleMs); Assert.Equal(120000, monitor.Applied.StabilizationMs);
    });

    [Fact]
    public Task InvalidCodeDoesNotThrowOrReplaceThePriorReviewOrSettings() => OnUi(async () =>
    {
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new FixedMonitor()); await vm.InitializeAsync();
        vm.PerformanceShareInput = PerformanceSettingsTransfer.EncodeShareCode(new(ManagerPerformanceProfile.HighPerformance));
        vm.PreviewPerformanceCode.Execute(null); string validReview = vm.PerformanceReviewSummary;
        vm.PerformanceShareInput = "not a performance code"; vm.PreviewPerformanceCode.Execute(null);
        Assert.True(vm.HasPerformanceReview); Assert.Equal(validReview, vm.PerformanceReviewSummary);
        Assert.Contains("failed", vm.PerformanceTransferStatus); Assert.Equal("Settings saved", vm.SaveStatus);
        vm.DismissPerformanceReview.Execute(null); Assert.False(vm.HasPerformanceReview);
        Assert.Contains("Balanced", vm.ActivePerformanceName);
    });

    [Fact]
    public Task LateFileImportCannotRestoreAReviewAfterPanicOrLockAndOnlyOneTransferRuns() => OnUi(async () =>
    {
        var store = new TransferStore { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new FixedMonitor(), performanceTransferStore: store);
        await vm.InitializeAsync(); vm.ImportPerformanceSettings.Execute("fixture.json");
        Assert.True(vm.IsPerformanceTransferBusy); Assert.Equal(1, store.Reads);
        try
        {
            vm.ExportPerformanceSettings.Execute("another.json"); await vm.ExportPerformanceSettings.Execution;
            Assert.Equal(0, store.Writes); vm.LockDashboard(); vm.Unlock.Execute(null); await vm.Unlock.Execution;
            Assert.True(store.Token.IsCancellationRequested); Assert.False(vm.HasPerformanceReview);
            vm.Panic.Execute(null);
        }
        finally { store.Gate.TrySetResult(new(ManagerPerformanceProfile.HighPerformance)); }
        await vm.ImportPerformanceSettings.Execution;
        Assert.False(vm.IsPerformanceTransferBusy); Assert.False(vm.HasPerformanceReview);
        Assert.Contains("Panic stop", vm.Notice); Assert.Contains("stopped", vm.PerformanceTransferStatus);
        Assert.Contains("Balanced", vm.ActivePerformanceName);
    });

    [Fact]
    public Task FileExportPinsOnlyTheDraftPerformanceAndFailureNeverClaimsASave() => OnUi(async () =>
    {
        var store = new TransferStore(); await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new FixedMonitor(), performanceTransferStore: store);
        await vm.InitializeAsync(); vm.SpareRam = "7";
        vm.SelectedPerformanceProfile = vm.PerformanceProfiles.Single(item => item.Profile == ManagerPerformanceProfile.Custom);
        vm.CustomProcessSample = "3500"; vm.ExportPerformanceSettings.Execute("fixture.json"); await vm.ExportPerformanceSettings.Execution;
        Assert.Equal(3500, store.Outgoing!.CustomProcessSampleMs); Assert.Equal(1, store.Writes);
        Assert.Equal("Unsaved settings", vm.SaveStatus); Assert.Contains("file saved", vm.PerformanceTransferStatus);
        store.FailExport = true; vm.ExportPerformanceSettings.Execute("fixture.json"); await vm.ExportPerformanceSettings.Execution;
        Assert.Contains("failed", vm.PerformanceTransferStatus); Assert.Equal("Unsaved settings", vm.SaveStatus);
    });

    private sealed class TransferStore : IPerformanceSettingsTransferStore
    {
        public int Reads, Writes; public bool FailExport; public CancellationToken Token;
        public PortablePerformanceSettings? Outgoing;
        public TaskCompletionSource<PortablePerformanceSettings>? Gate;
        public Task<PortablePerformanceSettings> ImportAsync(string path, CancellationToken ct)
        { Reads++; Token = ct; return Gate?.Task ?? Task.FromResult(new PortablePerformanceSettings()); }
        public Task ExportAsync(string path, PortablePerformanceSettings settings, CancellationToken ct)
        { Writes++; Outgoing = settings; return FailExport ? Task.FromException(new System.IO.IOException("synthetic")) : Task.CompletedTask; }
    }
}
