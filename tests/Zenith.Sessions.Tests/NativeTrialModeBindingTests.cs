using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SavedModeChangeInvalidatesAnOpenTrialReviewIncludingAba(bool restore) => OnUi(async () =>
    {
        var child = new NativeEvidenceChild();
        await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor());
        var review = Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        await ChangeMode(shell, NativeLaunchMode.DirectTicket);
        if (restore) await ChangeMode(shell, NativeLaunchMode.OfficialPage);
        shell.StartNativeTrial.Execute(review); await shell.StartNativeTrial.Execution;
        Assert.Empty(shell.Launches); Assert.Equal(0, child.Preparations);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SavedModeChangeDuringReleaseCheckCannotQueueTheOldReview(bool restore) => OnUi(async () =>
    {
        var releases = new HeldReleaseRead(); var child = new NativeEvidenceChild();
        await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor(), releases: releases);
        var review = Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        shell.StartNativeTrial.Execute(review);
        try
        {
            await releases.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ChangeMode(shell, NativeLaunchMode.DirectTicket);
            if (restore) await ChangeMode(shell, NativeLaunchMode.OfficialPage);
        }
        finally { releases.Finish.TrySetResult(); await shell.StartNativeTrial.Execution.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Empty(shell.Launches); Assert.Equal(0, child.Preparations);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SavedModeChangeDuringPreparationFencesTheOldTrialEvenAfterResume(bool restore) => OnUi(async () =>
    {
        var child = new NativeEvidenceChild { PreparationGate = EvidenceGate() };
        await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor());
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview()));
        await shell.StartNativeTrial.Execution;
        try
        {
            await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ChangeMode(shell, NativeLaunchMode.DirectTicket);
            if (restore) await ChangeMode(shell, NativeLaunchMode.OfficialPage);
        }
        finally { child.PreparationGate.TrySetResult(); }
        await WaitUntil(() => shell.Launches.Count == 1 && shell.Launches[0].Operation.State is LaunchState.Failed or LaunchState.Cancelled or LaunchState.ClientStartedUnverified);
        Assert.Equal(0, child.Starts);
        Assert.Equal("Resume queue", shell.QueuePauseLabel);
        shell.ToggleQueuePause.Execute(null); await shell.ToggleQueuePause.Execution;
        shell.Refresh.Execute(null); await shell.Refresh.Execution;
        Assert.Equal(0, child.Starts); Assert.False(shell.CanRecordNativeLaunchVerification);
    });

    [Fact]
    public Task AStartedCaptureOfAnotherModeCannotQualifyTheReviewedMode() => OnUi(async () =>
    {
        var monitor = new NativeEvidenceMonitor();
        var child = new NativeEvidenceChild { PreparedMode = NativeLaunchMode.DirectTicket };
        await using var shell = await NativeEvidenceShellAsync(child, monitor);
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview()));
        await shell.StartNativeTrial.Execution; await NativeEvidenceStartedAsync(shell);
        monitor.Observe(child.Receipt, NativeEvidenceMode.Fresh); shell.Refresh.Execute(null); await shell.Refresh.Execution;
        var now = DateTimeOffset.UtcNow;
        shell.RefreshLaunchEvidenceWithLogsForTest([new(Guid.NewGuid(), new(null, child.Receipt.ProcessId, child.Receipt.ProcessStartUtcTicks), (nint)100,
            ObservedSessionLifecycle.Connected, 456, new(42, Guid.NewGuid(), null, now.AddMilliseconds(-200)), null,
            new(Guid.NewGuid(), 1), SessionEvidenceConfidence.UnverifiedDiagnostic, now.AddMilliseconds(-200), false,
            now.AddMilliseconds(-300), SessionLogCorrelationKind.ResourceHolderDiagnostic, now.AddSeconds(5))]);
        Assert.Equal(NativeTrialEvidenceOutcome.ReportedMatchUnverified, shell.SelectedLaunchEvidence!.View!.Outcome);
        Assert.False(shell.CanRecordNativeLaunchVerification);
        shell.RecordNativeLaunchVerification.Execute(null); await shell.RecordNativeLaunchVerification.Execution;
        Assert.False(shell.IsNativeLaunchVerified);
    });

    private static async Task ChangeMode(ShellViewModel shell, NativeLaunchMode mode)
    {
        shell.SelectedNativeLaunchMode = ShellViewModel.NativeLaunchModeOptions.Single(option => option.Mode == mode);
        shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.Equal("Settings saved", shell.SaveStatus);
    }

    [Fact]
    public Task SuccessfulDirectTrialRecordsItsCapturedMode() => OnUi(async () =>
    {
        var monitor = new NativeEvidenceMonitor(); var child = new NativeEvidenceChild { PreparedMode = NativeLaunchMode.DirectTicket };
        await using var shell = await NativeEvidenceShellAsync(child, monitor);
        await ChangeMode(shell, NativeLaunchMode.DirectTicket);
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview()));
        await shell.StartNativeTrial.Execution; await NativeEvidenceStartedAsync(shell);
        child.PreparedMode = NativeLaunchMode.OfficialPage; // Later child observations cannot relabel the receipt.
        monitor.Observe(child.Receipt, NativeEvidenceMode.Fresh); shell.Refresh.Execute(null); await shell.Refresh.Execution;
        var now = DateTimeOffset.UtcNow;
        shell.RefreshLaunchEvidenceWithLogsForTest([new(Guid.NewGuid(), new(null, child.Receipt.ProcessId, child.Receipt.ProcessStartUtcTicks), (nint)100,
            ObservedSessionLifecycle.Connected, 456, new(42, Guid.NewGuid(), null, now.AddMilliseconds(-200)), null,
            new(Guid.NewGuid(), 1), SessionEvidenceConfidence.UnverifiedDiagnostic, now.AddMilliseconds(-200), false,
            now.AddMilliseconds(-300), SessionLogCorrelationKind.ResourceHolderDiagnostic, now.AddSeconds(5))]);
        Assert.True(shell.CanRecordNativeLaunchVerification);
        shell.RecordNativeLaunchVerification.Execute(null); await shell.RecordNativeLaunchVerification.Execution;
        Assert.True(shell.IsNativeLaunchVerified);
        Assert.Contains("\"NativeLaunchVerifications\":[{\"Mode\":\"direct-ticket\"", await ReadSettingsAsync(), StringComparison.Ordinal);
    });

    private static async Task WaitUntil(Func<bool> completed)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!completed()) await Task.Delay(5, stop.Token);
    }
}
