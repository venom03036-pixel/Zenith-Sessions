using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    // These fixtures use only our owned temporary metadata/DPAPI stores and an injected
    // in-memory native child. No executable, WebView, actual process or host log is opened.
    private const string EvidenceAlias = "Synthetic private alias excluded from report";
    private const string EvidenceNote = "Synthetic private note excluded from report";
    private const string EvidenceJob = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";

    [Fact]
    public Task TrialEvidencePinsExpectedPlayerPlaceAndServerAcrossAccountAndDestinationSelectionChanges() => OnUi(async () =>
    {
        var monitor = new NativeEvidenceMonitor(); var child = new NativeEvidenceChild { PreparationGate = EvidenceGate() };
        await using var shell = await NativeEvidenceShellAsync(child, monitor);
        var selected = shell.SelectedAccount!;
        shell.ServerBrowser.LinkInput = "https://www.roblox.com/games/start?placeId=42&gameInstanceId=" + EvidenceJob;
        shell.ServerBrowser.Resolve.Execute(null); Assert.Equal(EvidenceJob, shell.ServerBrowser.Destination!.JobId);
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview())); await shell.StartNativeTrial.Execution;
        var context = await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal(selected.Profile.Id, context.OriginalIntent.ProfileId); Assert.Equal(456, context.OriginalIntent.UserId);
            shell.SelectedAccount = shell.Accounts.Single(row => row.Profile.Id != selected.Profile.Id);
            shell.ServerBrowser.UsePreferredPlace(999);
        }
        finally { child.PreparationGate.TrySetResult(); }
        var evidence = await NativeEvidenceStartedAsync(shell);
        monitor.Observe(child.Receipt, NativeEvidenceMode.Fresh);
        shell.Refresh.Execute(null); await shell.Refresh.Execution;
        evidence = Assert.IsType<LaunchEvidenceRow>(shell.SelectedLaunchEvidence);
        Assert.Equal(context.OriginalIntent.OperationId, evidence.Id);
        Assert.Equal(456, evidence.View!.ExpectedUserId); Assert.Equal(42, evidence.View.RequestedPlaceId);
        Assert.Equal(EvidenceJob, evidence.View.RequestedJobId); Assert.Equal(NativeTrialProcessPresence.Observed, evidence.View.Presence);
        Assert.Equal(child.Receipt.ProcessId, evidence.View.ProcessReceipt!.ProcessId);
        Assert.Equal(child.Receipt.ProcessStartUtcTicks, evidence.View.ProcessReceipt.ProcessStartUtcTicks);
        Assert.Equal(999, shell.ServerBrowser.Destination!.PlaceId);
        Assert.NotEqual(selected.Profile.Id, shell.SelectedAccount!.Profile.Id);
        Assert.Null(evidence.View.ReportedUser.UserId); Assert.Null(evidence.View.ReportedDestination.PlaceId);
        Assert.False(evidence.View.ArrivalVerified); Assert.False(evidence.View.CanAuthorizeAccountAction); Assert.False(evidence.View.CanRouteInput);
        Assert.True(shell.CanShowEvidenceProcess); shell.ShowEvidenceProcess.Execute(null);
        Assert.True(shell.IsSessions); Assert.Equal(child.Receipt.ProcessId, shell.SelectedSession!.Observation.ProcessId);
        var retainedAt = evidence.View.ProcessReceipt.RecordedUtc;
        string report = shell.BuildNativeTrialEvidenceReport(); AssertNativeEvidenceReport(report, context.OriginalIntent.OperationId);
        Assert.Equal(retainedAt, shell.SelectedLaunchEvidence!.View!.ProcessReceipt!.RecordedUtc);
        Assert.Contains(EvidenceJob, report, StringComparison.Ordinal);
        Assert.Contains("unverified", shell.SelectedLaunchEvidence.Comparison, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(shell.LaunchPacingStatus)); Assert.Equal(1, child.Starts);
    });

    [Theory]
    [InlineData(NativeEvidenceMode.Stale, NativeTrialProcessPresence.Stale)]
    [InlineData(NativeEvidenceMode.PidReused, NativeTrialProcessPresence.PidReused)]
    [InlineData(NativeEvidenceMode.Incomplete, NativeTrialProcessPresence.Incomplete)]
    public Task StaleReusedOrIncompleteProcessEvidenceNeverBecomesAccountOrArrivalAuthority(NativeEvidenceMode mode, NativeTrialProcessPresence expected) => OnUi(async () =>
    {
        var monitor = new NativeEvidenceMonitor(); var child = new NativeEvidenceChild();
        await using var shell = await NativeEvidenceShellAsync(child, monitor);
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview())); await shell.StartNativeTrial.Execution;
        await NativeEvidenceStartedAsync(shell);
        monitor.Observe(child.Receipt, mode); shell.Refresh.Execute(null); await shell.Refresh.Execution;
        var evidence = Assert.IsType<LaunchEvidenceRow>(shell.SelectedLaunchEvidence); var view = evidence.View!;
        Assert.Equal(expected, view.Presence); Assert.Equal(456, view.ExpectedUserId); Assert.Equal(42, view.RequestedPlaceId);
        Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome);
        Assert.Null(view.ReportedUser.UserId); Assert.Null(view.ReportedDestination.PlaceId); Assert.Null(view.ReportedDestination.JobId);
        Assert.False(view.CanAuthorizeAccountAction); Assert.False(view.ArrivalVerified); Assert.False(view.CanRouteInput);
        Assert.False(shell.CanShowEvidenceProcess);
        Assert.Equal(LaunchState.ClientStartedUnverified, evidence.Operation.State);
        AssertNativeEvidenceReport(shell.BuildNativeTrialEvidenceReport(), evidence.Id);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MissingSelectionAndLockDenyEvidenceReportsAndLockClearsDetailsImmediately(bool locked) => OnUi(async () =>
    {
        var monitor = new NativeEvidenceMonitor(); var child = new NativeEvidenceChild();
        await using var shell = await NativeEvidenceShellAsync(child, monitor);
        if (!locked)
        {
            Assert.Empty(shell.LaunchEvidenceRows); Assert.Null(shell.SelectedLaunchEvidence);
            Assert.Throws<InvalidOperationException>(() => shell.BuildNativeTrialEvidenceReport()); return;
        }
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview())); await shell.StartNativeTrial.Execution;
        await NativeEvidenceStartedAsync(shell); Assert.True(shell.HasLaunchEvidence);
        shell.LockDashboard();
        Assert.True(shell.IsLocked); Assert.Empty(shell.LaunchEvidenceRows); Assert.Null(shell.SelectedLaunchEvidence);
        Assert.False(shell.HasLaunchEvidence); Assert.False(shell.CanShowEvidenceProcess);
        Assert.Throws<InvalidOperationException>(() => shell.BuildNativeTrialEvidenceReport());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(shell.LaunchEvidenceRows); Assert.Null(shell.SelectedLaunchEvidence);
        Assert.DoesNotContain(EvidenceAlias, shell.LaunchPacingStatus, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task InjectedPositiveTrialChildCannotAuthorizeOrdinaryOrCheckedAccountRoutes(bool checkedRoute) => OnUi(async () =>
    {
        var child = new NativeEvidenceChild(); await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor());
        Assert.True(shell.CanRunNativeTrial);
        if (checkedRoute)
        {
            shell.SelectedAccount!.IsChecked = true;
            shell.QueueTargetedAccounts.Execute(null); await shell.QueueTargetedAccounts.Execution;
        }
        else { shell.QueueSelected.Execute(null); await shell.QueueSelected.Execution; }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var operation = Assert.Single(shell.Launches).Operation;
        Assert.Equal(LaunchState.Blocked, operation.State); Assert.Equal(LaunchProblem.ProviderUnavailable, operation.Problem);
        Assert.Null(operation.ProcessReceipt); Assert.Equal(0, child.Preparations); Assert.Equal(0, child.Starts);
        Assert.DoesNotContain(shell.Capabilities, capability => capability.State == CapabilityState.Supported
            && capability.Name.Contains("native", StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    public Task EvidenceExportPreservesThePriorFileOnBlockedReplacementThenExplicitRetryWritesOnlyAllowlistedJson() => OnUi(async () =>
    {
        var child = new NativeEvidenceChild(); await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor());
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview())); await shell.StartNativeTrial.Execution;
        var evidence = await NativeEvidenceStartedAsync(shell);
        using var output = new NativeEvidenceExportFolder(); string destination = Path.Combine(output.Path, "owned-report.json");
        byte[] original = System.Text.Encoding.UTF8.GetBytes("{\"PriorOwnedFixture\":true}");
        await File.WriteAllBytesAsync(destination, original);
        // Deny replacement of this owned file without blocking reads or opening any real user file.
        using (var heldReader = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            shell.ExportLaunchEvidence.Execute(destination); await shell.ExportLaunchEvidence.Execution;
            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            Assert.DoesNotContain("Launch evidence saved", shell.Notice, StringComparison.OrdinalIgnoreCase);
            // The held file is in the folder the user chose, so the notice points there, not at the data folder.
            Assert.Contains("chosen folder", shell.ErrorNotice, StringComparison.Ordinal);
            Assert.DoesNotContain("data folder", shell.ErrorNotice, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(output.Path, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        shell.ExportLaunchEvidence.Execute(destination); await shell.ExportLaunchEvidence.Execution;
        string report = await File.ReadAllTextAsync(destination);
        AssertNativeEvidenceReport(report, evidence.Id);
        Assert.DoesNotContain("PriorOwnedFixture", report, StringComparison.Ordinal);
        Assert.Contains("Launch evidence saved", shell.Notice, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(output.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    });

    [Fact]
    public Task AnEvidenceExportThatFailsAsLockLandsIsJournaledByItsCauseNotItsWrapper() => OnUi(async () =>
    {
        var child = new NativeEvidenceChild(); await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor());
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview())); await shell.StartNativeTrial.Execution;
        await NativeEvidenceStartedAsync(shell);
        using var output = new NativeEvidenceExportFolder(); string destination = Path.Combine(output.Path, "owned-report.json");
        await File.WriteAllTextAsync(destination, "{\"PriorOwnedFixture\":true}"); File.SetAttributes(destination, FileAttributes.ReadOnly);
        try
        {
            using var removal = new StagedReportRemoval(output.Path);
            // The write has already failed, but its continuation waits for this UI step, so Lock lands first.
            shell.ExportLaunchEvidence.Execute(destination); removal.Wait(); shell.Lock.Execute(null); await shell.ExportLaunchEvidence.Execution;
        }
        finally { File.SetAttributes(destination, FileAttributes.Normal); }
        Assert.DoesNotContain("chosen folder", shell.ErrorNotice, StringComparison.Ordinal);
        shell.Unlock.Execute(null); shell.RefreshDiagnostics.Execute(null);
        var launchFailures = shell.DiagnosticRows.Where(row => row.Event.Kind == ManagerEventKind.OperationFailed && row.Event.Subsystem == ManagerSubsystem.Launch)
            .Select(row => row.Event.ErrorType).ToArray();
        Assert.Contains("UnauthorizedAccessException", launchFailures);
    });

    [Fact]
    public Task EvidenceExportCannotOverwriteTheActualOwnedSettingsFileAndReportsFailure() => OnUi(async () =>
    {
        var child = new NativeEvidenceChild(); await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor());
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview())); await shell.StartNativeTrial.Execution;
        await NativeEvidenceStartedAsync(shell); await shell.SaveSettings.Execution; await shell.ApplyTheme.Execution;
        string settings = Path.Combine(_root, "settings.json"); Assert.True(File.Exists(settings));
        byte[] original = await File.ReadAllBytesAsync(settings);
        shell.ExportLaunchEvidence.Execute(settings); await shell.ExportLaunchEvidence.Execution;
        Assert.Equal(original, await File.ReadAllBytesAsync(settings));
        Assert.Contains("outside the app", shell.ErrorNotice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Launch evidence saved", shell.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(_root, "settings.json.*.tmp", SearchOption.TopDirectoryOnly));
    });

    // Pure presentation-row fixtures: synthetic records only, no shell, process or log.
    private static readonly DateTimeOffset EvidenceRowAt = new(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);
    private static LaunchOperation EvidenceRowOperation() => new(
        new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 42, 0, EvidenceRowAt.AddMinutes(-6), 7, EvidenceJob, RobloxServerKind.Public, true),
        LaunchState.ClientStartedUnverified, LaunchProblem.ClientStartedUnverified, EvidenceRowAt, new(700001, EvidenceRowAt.AddMinutes(-10).UtcTicks, EvidenceRowAt.AddMinutes(-5)));
    private static NativeTrialEvidenceView EvidenceRowView(LaunchOperation operation) => new(operation.Intent.OperationId, 123, 42, EvidenceJob,
        operation.ProcessReceipt, NativeTrialProcessPresence.Observed, EvidenceRowAt, EvidenceRowAt.AddMilliseconds(-100),
        new(123, EvidenceRowAt.AddSeconds(-1), NativeTrialReportedStatus.ReportedMatchUnverified),
        new(42, Guid.Parse(EvidenceJob), EvidenceRowAt.AddSeconds(-1), NativeTrialReportedStatus.ReportedMatchUnverified, NativeTrialReportedStatus.ReportedMatchUnverified),
        new(123, EvidenceRowAt.AddSeconds(-2), NativeTrialReportedStatus.LastKnownOnly),
        new(42, Guid.Parse(EvidenceJob), EvidenceRowAt.AddSeconds(-2), NativeTrialReportedStatus.LastKnownOnly, NativeTrialReportedStatus.LastKnownOnly),
        NativeTrialEvidenceOutcome.ReportedMatchUnverified, "Reported values are unverified diagnostics.");

    [Fact]
    public void LaunchEvidenceRowMeasurementOnlyChangeDoesNotRaiseRequestFields()
    {
        var operation = EvidenceRowOperation(); var view = EvidenceRowView(operation);
        var row = new LaunchEvidenceRow(operation); row.Update(operation, view);
        var raised = new List<string?>(); row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        // A new presentation tick re-measures the same process; nothing about the request changed.
        row.Update(operation, view with { ProcessObservedUtc = EvidenceRowAt.AddSeconds(1), ProcessMeasurementUtc = EvidenceRowAt.AddSeconds(1) });
        Assert.Equal(new[] { nameof(LaunchEvidenceRow.ObservationTime), nameof(LaunchEvidenceRow.View) }, raised.Order(StringComparer.Ordinal));
        raised.Clear(); row.Update(operation, row.View!); Assert.Empty(raised);
    }

    [Fact]
    public void LaunchEvidenceRowRaisesEveryDisplayedTextThatChanged()
    {
        var operation = EvidenceRowOperation(); var baseline = EvidenceRowView(operation);
        var texts = typeof(LaunchEvidenceRow).GetProperties().Where(property => property.PropertyType == typeof(string)).ToArray();
        Assert.True(texts.Length >= 12);
        var changes = new (string Name, LaunchOperation Operation, NativeTrialEvidenceView View)[]
        {
            ("operation-state", operation with { State = LaunchState.Cancelled }, baseline),
            ("expected-player", operation, baseline with { ExpectedUserId = 456 }),
            ("requested-place", operation, baseline with { RequestedPlaceId = 99 }),
            ("requested-server", operation, baseline with { RequestedJobId = null }),
            ("receipt", operation, baseline with { ProcessReceipt = baseline.ProcessReceipt! with { ProcessId = 700002 } }),
            ("presence", operation, baseline with { Presence = NativeTrialProcessPresence.NotObserved }),
            ("measurement", operation, baseline with { ProcessMeasurementUtc = null }),
            ("reported-player", operation, baseline with { ReportedUser = baseline.ReportedUser with { UserId = 456 } }),
            ("reported-player-time", operation, baseline with { ReportedUser = baseline.ReportedUser with { ObservedUtc = EvidenceRowAt } }),
            ("reported-place", operation, baseline with { ReportedDestination = baseline.ReportedDestination with { PlaceId = 99 } }),
            ("reported-server", operation, baseline with { ReportedDestination = baseline.ReportedDestination with { JobId = Guid.NewGuid() } }),
            ("reported-time", operation, baseline with { ReportedDestination = baseline.ReportedDestination with { ObservedUtc = EvidenceRowAt } }),
            ("reported-status", operation, baseline with { ReportedDestination = baseline.ReportedDestination with { JobStatus = NativeTrialReportedStatus.Stale } }),
            ("last-known-player", operation, baseline with { LastKnownUser = baseline.LastKnownUser with { UserId = 456 } }),
            ("last-known-destination", operation, baseline with { LastKnownDestination = baseline.LastKnownDestination with { JobId = Guid.NewGuid() } }),
            ("outcome", operation, baseline with { Outcome = NativeTrialEvidenceOutcome.ReportedMismatchUnverified }),
            ("explanation", operation, baseline with { Explanation = "A different synthetic explanation." })
        };
        foreach (var change in changes)
        {
            var row = new LaunchEvidenceRow(operation); row.Update(operation, baseline);
            AssertChangedTextsRaised(row, change.Name, () => row.Update(change.Operation, change.View));
        }
        // The first projection replaces the no-evidence placeholders.
        var fresh = new LaunchEvidenceRow(operation);
        AssertChangedTextsRaised(fresh, "first-view", () => fresh.Update(operation, baseline));

        void AssertChangedTextsRaised(LaunchEvidenceRow row, string change, Action update)
        {
            var before = texts.ToDictionary(text => text.Name, text => (string?)text.GetValue(row)); var viewBefore = row.View;
            var raised = new HashSet<string?>(); row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            update();
            foreach (var text in texts)
                if (!string.Equals(before[text.Name], (string?)text.GetValue(row), StringComparison.Ordinal))
                    Assert.True(raised.Contains(text.Name), $"{change} changed {text.Name} without a notification.");
            Assert.True(raised.Contains(nameof(LaunchEvidenceRow.View)) == (viewBefore != row.View), $"{change} View notification does not match the change.");
        }
    }

    private async Task<ShellViewModel> NativeEvidenceShellAsync(NativeEvidenceChild child, NativeEvidenceMonitor monitor,IAccountRepository? accounts=null)
    {
        var provider = new WindowsConfiguredNativeLaunchProvider(_ => child, new NativeEvidenceTrustOwner());
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor,
            signIn: new HandoffSignIn(), sessionClient: new HandoffIdentity(), nativeProvider: provider,accounts:accounts);
        try
        {
            await shell.InitializeAsync();
            var profile = AccountProfile.Create(EvidenceAlias) with { PreferredPlaceId = 42, Notes = EvidenceNote };
            await shell.SaveAccountAsync(profile); await shell.SaveAccountAsync(AccountProfile.Create("Other synthetic profile") with { PreferredPlaceId = 999 });
            shell.SelectedAccount = shell.Accounts.Single(row => row.Profile.Id == profile.Id);
            shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution;
            Assert.Equal(456, shell.SelectedAccount!.KnownUserId);
            shell.SelectedInstallation = NativeTrialMetadataOnlyInstallation(); shell.ServerBrowser.UsePreferredPlace(42);
            shell.Refresh.Execute(null); await shell.Refresh.Execution;
            Assert.True(shell.IsReady); Assert.True(shell.QueueReady); Assert.True(shell.CanRunNativeTrial);
            Assert.False(Directory.Exists(Path.Combine(_root, "signin-browser-data")));
            Assert.False(File.Exists(NativeTrialMetadataOnlyInstallation().ExecutablePath));
            return shell;
        }
        catch { await shell.DisposeAsync(); throw; }
    }
    private static async Task<LaunchEvidenceRow> NativeEvidenceStartedAsync(ShellViewModel shell)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!shell.LaunchEvidenceRows.Any(row => row.Operation.State == LaunchState.ClientStartedUnverified)) await Task.Delay(5, stop.Token);
        Assert.Single(shell.LaunchEvidenceRows); Assert.NotNull(shell.SelectedLaunchEvidence);
        return shell.SelectedLaunchEvidence!;
    }
    private void AssertNativeEvidenceReport(string report, Guid operationId)
    {
        using var document = JsonDocument.Parse(report); var root = document.RootElement;
        Assert.Equal(new[] { "AccountVerified", "ArrivalVerified", "CreatedUtc", "Evidence", "FormatVersion", "InputAuthorized", "NativeDiagnostics", "OperationId",
            "Pacing", "Problem", "Product", "ProviderCapability", "Scope", "State", "Version" }, root.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(operationId, root.GetProperty("OperationId").GetGuid());
        Assert.Equal(2,root.GetProperty("FormatVersion").GetInt32());
        var diagnostic=root.GetProperty("NativeDiagnostics");
        Assert.Equal(new[]{"AccountVerified","ArrivalVerified","Available","BrowserObservedUtc","BrowserOutcome","CleanupObservedUtc","CleanupStatus","DispatchObservedUtc","DispatchStatus",
            "ExecutableObservedUtc","InputAuthorized","OperationId","PreparationObservedUtc","PreparationStatus","Revision","ShapeStatus","Stage","StartedUtc","Stopped","TrustReason","TrustStatus","UpdatedUtc"},
            diagnostic.EnumerateObject().Select(item=>item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(operationId,diagnostic.GetProperty("OperationId").GetGuid());Assert.False(diagnostic.GetProperty("AccountVerified").GetBoolean());
        Assert.False(diagnostic.GetProperty("ArrivalVerified").GetBoolean());Assert.False(diagnostic.GetProperty("InputAuthorized").GetBoolean());
        Assert.Equal("Unverified", root.GetProperty("ProviderCapability").GetString());
        Assert.False(root.GetProperty("AccountVerified").GetBoolean()); Assert.False(root.GetProperty("ArrivalVerified").GetBoolean());
        Assert.False(root.GetProperty("InputAuthorized").GetBoolean());
        Assert.Equal(new[] { "ArrivalVerified", "CanAuthorizeAccountAction", "CanRouteInput", "ExpectedUserId", "Explanation", "LastKnownDestination",
            "LastKnownUser", "OperationId", "Outcome", "Presence", "ProcessMeasurementUtc", "ProcessObservedUtc", "ProcessReceipt", "ReportedDestination",
            "ReportedUser", "RequestedJobId", "RequestedPlaceId" }, root.GetProperty("Evidence").EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        foreach (string forbidden in new[] { _root, JsonSerializer.Serialize(_root)[1..^1], EvidenceAlias, EvidenceNote, "probe_player", "synthetic-probe-original", "RobloxPlayerBeta.exe", "private synthetic window title",
            "SessionCookie", "RefreshToken", "gameinfo", "roblox-player:", "WindowTitle", "ExecutablePath", "ProfileId" })
            Assert.DoesNotContain(forbidden, report, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(report.Length, 1, 65536);
    }
    private static TaskCompletionSource EvidenceGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public enum NativeEvidenceMode { Fresh, Stale, PidReused, Incomplete }
    private sealed class NativeEvidenceMonitor : IDesktopMonitor
    {
        private sealed record Input(LaunchProcessReceipt? Receipt, NativeEvidenceMode Mode);
        private Input _input = new(null, NativeEvidenceMode.Fresh); private long _revision;
        public void Observe(LaunchProcessReceipt receipt, NativeEvidenceMode mode) => Volatile.Write(ref _input, new(receipt, mode));
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var input = Volatile.Read(ref _input); var now = DateTimeOffset.UtcNow;
            var at = input.Mode == NativeEvidenceMode.Stale ? now.AddSeconds(-10) : now;
            SessionObservation[] rows = input.Receipt is { } receipt ? [new(receipt.ProcessId,
                receipt.ProcessStartUtcTicks + (input.Mode == NativeEvidenceMode.PidReused ? 1 : 0), "Synthetic", 0,
                "private synthetic window title", false, true, 128 * 1024 * 1024, 256 * 1024 * 1024, 10, at,
                PrivateWorkingSetBytes: 64 * 1024 * 1024)] : [];
            return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB,
                64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), rows, ProcessObservedUtc: at,
                ProcessRevision: Interlocked.Increment(ref _revision), ProcessInventoryComplete: input.Mode != NativeEvidenceMode.Incomplete));
        }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No focus allowed");
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No close allowed");
    }
    private sealed class NativeEvidenceTrustOwner : IAsyncDisposable
    { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class NativeEvidenceExportFolder : IDisposable
    {
        private const string Prefix = "zenith-native-evidence-export-", Marker = ".zenith-evidence-export-fixture", Owner = "Zenith evidence export fixture v1";
        public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        public NativeEvidenceExportFolder()
        {
            if (Directory.Exists(Path)) throw new IOException("An unexpected export fixture already exists.");
            Directory.CreateDirectory(Path); File.WriteAllText(System.IO.Path.Combine(Path, Marker), Owner);
        }
        public void Dispose()
        {
            string full = System.IO.Path.GetFullPath(Path), marker = System.IO.Path.Combine(full, Marker);
            if (!string.Equals(System.IO.Path.GetDirectoryName(full), System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(full).StartsWith(Prefix, StringComparison.Ordinal) || !File.Exists(marker) || File.ReadAllText(marker) != Owner)
                throw new InvalidOperationException("Unexpected evidence export fixture cleanup path.");
            Directory.Delete(full, true);
        }
    }
    private sealed class NativeEvidenceChild : IConfiguredNativeLaunchChild, INativeLaunchDiagnosticSource
    {
        public LaunchProcessReceipt Receipt { get; } = new(730001, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks, DateTimeOffset.UtcNow);
        public TaskCompletionSource<LaunchPreparationContext> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? PreparationGate; public int Preparations, Starts;
        public Func<Guid,NativeLaunchDiagnosticSnapshot?>? DiagnosticFactory;
        private NativeLaunchDiagnosticSnapshot? _diagnostic;
        public INativeLaunchDiagnosticSource? DiagnosticSource=>this;
        public NativeLaunchDiagnosticSnapshot? GetDiagnostic(Guid operationId)=>_diagnostic?.OperationId==operationId?_diagnostic:null;
        public bool CleanupHealthy => true;
        public async Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Preparations); Entered.TrySetResult(context);
            _diagnostic=DiagnosticFactory?.Invoke(context.OriginalIntent.OperationId);
            if (PreparationGate is not null) await PreparationGate.Task;
            return new(LaunchPreparationFailure.None, new NativeEvidencePrepared(this, context));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NativeEvidencePrepared(NativeEvidenceChild child, LaunchPreparationContext context) : IPreparedAccountLaunch
    {
        private bool _closed;
        public LaunchPreparationContext Context => context;
        public LaunchIntent DispatchIntent => context.OriginalIntent; // No fake grant/revision change: actual owned vault validator still runs.
        public bool IsExpired => _closed;
        public async Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!boundary.IsCurrent || !await boundary.ValidateAsync(DispatchIntent, ct) || !boundary.IsCurrent || !boundary.ReadAdmission().Allowed)
                return new(ProviderLaunchStatus.RejectedBeforeStart);
            Interlocked.Increment(ref child.Starts);
            return new(ProviderLaunchStatus.ClientStartedUnverified, ProcessId: child.Receipt.ProcessId, ProcessStartUtcTicks: child.Receipt.ProcessStartUtcTicks);
        }
        public ValueTask DisposeAsync() { _closed = true; return ValueTask.CompletedTask; }
    }
}
