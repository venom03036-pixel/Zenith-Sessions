using System.IO;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Diagnostics > Roblox client logs through the shell, over a fake cleanup: the check comes first and shows count and
/// size, nothing can be deleted without a check that found something, the view's confirmation names the same numbers,
/// the report says what was deleted and kept, and Panic, Lock and sample mode keep the tool off.
/// </summary>
public sealed class ClientLogCleanupPresentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-logcleanup-ui-tests-" + Guid.NewGuid().ToString("N"));
    private const long MiB = 1024 * 1024;

    private static ClientLogCleanupSurvey Survey(int removable, long bytesEach, int keptRecent = 0, int keptInUse = 0, int keptUnchecked = 0, int days = 7)
        => new(TimeSpan.FromDays(days), DateTimeOffset.UtcNow,
            Enumerable.Range(1, removable).Select(index => new ClientLogCleanupFile($"synthetic_Player_{index}_last.log", bytesEach, DateTimeOffset.UtcNow.AddDays(-10))).ToArray(),
            keptRecent, keptInUse, keptUnchecked, FoldersChecked: 1, BudgetLimited: false);

    [Fact]
    public Task CheckShowsCountAndSizeAndDeleteWaitsForACheckThatFoundSomething() => OnUi(async () =>
    {
        var probe = new CleanupProbe { Survey = Survey(3, 16 * MiB, keptRecent: 2, keptInUse: 1) };
        await using var shell = await Ready(probe);
        Assert.True(shell.CanSurveyClientLogs);
        Assert.False(shell.CanDeleteClientLogs);
        Assert.Equal("7", shell.ClientLogCleanupDays);

        shell.SurveyClientLogs.Execute(null); await shell.SurveyClientLogs.Execution;

        Assert.Equal(TimeSpan.FromDays(7), probe.LastOlderThan);
        Assert.Equal("3 Roblox Player logs older than 7 days can be removed (48.0 MB). Kept: 2 newer, 1 in use or watched by Zenith. Nothing is deleted until you confirm.", shell.ClientLogCleanupStatus);
        Assert.True(shell.CanDeleteClientLogs);
        Assert.StartsWith("Delete 3 Roblox Player logs older than 7 days (48.0 MB)?", shell.ClientLogCleanupConfirmation, StringComparison.Ordinal);
        Assert.Contains("not sent to the Recycle Bin", shell.ClientLogCleanupConfirmation);
        Assert.False(shell.IsCleaningClientLogs);

        // Changing the days makes the shown numbers stale, so a new check is needed before anything can be deleted.
        shell.ClientLogCleanupDays = "30";
        Assert.False(shell.CanDeleteClientLogs);
        Assert.Equal("", shell.ClientLogCleanupConfirmation);
    });

    [Fact]
    public Task ACheckThatFindsNothingSaysSoAndAnEmptyFolderIsNamed() => OnUi(async () =>
    {
        var probe = new CleanupProbe { Survey = Survey(0, 0, keptRecent: 4) };
        await using var shell = await Ready(probe);
        shell.SurveyClientLogs.Execute(null); await shell.SurveyClientLogs.Execution;
        Assert.Equal("No Roblox Player logs older than 7 days were found. Kept: 4 newer.", shell.ClientLogCleanupStatus);
        Assert.False(shell.CanDeleteClientLogs);

        probe.Survey = Survey(0, 0) with { FoldersChecked = 0 };
        shell.SurveyClientLogs.Execute(null); await shell.SurveyClientLogs.Execution;
        Assert.Equal("No Roblox logs folder was found for this Windows user, so there is nothing to clean.", shell.ClientLogCleanupStatus);
    });

    [Fact]
    public Task DeleteSendsTheCheckedSurveyReportsTheOutcomeAndNeedsANewCheckAfterwards() => OnUi(async () =>
    {
        var probe = new CleanupProbe { Survey = Survey(3, 16 * MiB), Result = new(Deleted: 2, DeletedBytes: 32 * MiB, KeptInUse: 1, KeptChanged: 0, Failed: 0, NotAttempted: 0, Stopped: false) };
        await using var shell = await Ready(probe);
        shell.SurveyClientLogs.Execute(null); await shell.SurveyClientLogs.Execution;
        var surveyed = probe.Survey;

        shell.DeleteClientLogs.Execute(null); await shell.DeleteClientLogs.Execution;

        Assert.Same(surveyed, probe.Deleted);
        Assert.Equal("2 Roblox Player logs deleted (32.0 MB freed). Kept: 1 in use or watched by Zenith.", shell.ClientLogCleanupStatus);
        Assert.Equal(shell.ClientLogCleanupStatus, shell.Notice);
        Assert.False(shell.CanDeleteClientLogs); // The survey was used up; check again before deleting more.
        Assert.True(shell.CanSurveyClientLogs);
        Assert.Equal("", shell.ErrorNotice);
    });

    [Fact]
    public Task AStoppedDeleteSaysWhatWasDeletedBeforeTheStopAndWhatWasNotTouched() => OnUi(async () =>
    {
        var probe = new CleanupProbe { Survey = Survey(5, MiB), Result = new(Deleted: 2, DeletedBytes: 2 * MiB, KeptInUse: 0, KeptChanged: 0, Failed: 1, NotAttempted: 2, Stopped: true) };
        await using var shell = await Ready(probe);
        shell.SurveyClientLogs.Execute(null); await shell.SurveyClientLogs.Execution;
        shell.DeleteClientLogs.Execute(null); await shell.DeleteClientLogs.Execution;
        Assert.Equal("Cleanup stopped. 2 Roblox Player logs deleted (2.0 MB freed) before the stop; 1 could not be deleted; 2 were not touched.", shell.ClientLogCleanupStatus);
    });

    [Fact]
    public Task PanicStopsACheckInProgressAndLeavesNothingToDelete() => OnUi(async () =>
    {
        var probe = new CleanupProbe { Survey = Survey(3, MiB), Block = true };
        await using var shell = await Ready(probe);
        shell.SurveyClientLogs.Execute(null);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(shell.IsCleaningClientLogs);
        Assert.False(shell.CanSurveyClientLogs);

        shell.Panic.Execute(null);
        await shell.SurveyClientLogs.Execution;

        Assert.True(probe.LastToken.IsCancellationRequested);
        Assert.Equal("Check stopped. Nothing was deleted.", shell.ClientLogCleanupStatus);
        Assert.False(shell.IsCleaningClientLogs);
        Assert.False(shell.CanDeleteClientLogs);
        Assert.StartsWith("Panic stop", shell.Notice, StringComparison.Ordinal);
    });

    [Fact]
    public Task LockDropsTheCheckedSurveyAndHidesTheToolUntilUnlock() => OnUi(async () =>
    {
        var probe = new CleanupProbe { Survey = Survey(2, MiB) };
        await using var shell = await Ready(probe);
        shell.SurveyClientLogs.Execute(null); await shell.SurveyClientLogs.Execution;
        Assert.True(shell.CanDeleteClientLogs);

        shell.Lock.Execute(null);
        Assert.False(shell.CanSurveyClientLogs); Assert.False(shell.CanDeleteClientLogs);
        shell.Unlock.Execute(null); await shell.Unlock.Execution;
        Assert.True(shell.CanSurveyClientLogs);
        Assert.False(shell.CanDeleteClientLogs); // A stop drops the survey: check again before deleting.
        Assert.Equal(0, probe.Deletes); // The lock itself deletes nothing.
    });

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    [InlineData("seven")]
    [InlineData("")]
    public Task InvalidDaysAreRefusedBeforeAnyFileIsRead(string days) => OnUi(async () =>
    {
        var probe = new CleanupProbe { Survey = Survey(1, MiB) };
        await using var shell = await Ready(probe);
        shell.ClientLogCleanupDays = days;
        shell.SurveyClientLogs.Execute(null); await shell.SurveyClientLogs.Execution;
        Assert.Equal("Enter a whole number of days from 1 to 365.", shell.ErrorNotice);
        Assert.Equal(0, probe.Surveys);
        Assert.False(shell.CanDeleteClientLogs);
    });

    [Fact]
    public Task ACleanupFailureIsShownAndRecordedAgainstTheClientLogsSubsystem() => OnUi(async () =>
    {
        var probe = new CleanupProbe { SurveyFailure = new IOException("synthetic folder failure C:\\secret\\path") };
        await using var shell = await Ready(probe);
        shell.SurveyClientLogs.Execute(null); await shell.SurveyClientLogs.Execution;
        Assert.Equal("The Roblox log check could not finish. No log was deleted; check the data folder permissions and try again.", shell.ErrorNotice);
        Assert.DoesNotContain("secret", shell.ClientLogCleanupStatus + shell.ErrorNotice + shell.Notice);
        Assert.False(shell.IsCleaningClientLogs);
        Assert.True(shell.CanSurveyClientLogs);
        shell.Navigate.Execute("Diagnostics"); shell.RefreshDiagnostics.Execute(null);
        Assert.Contains(shell.DiagnosticRows, row => row.Subsystem == "Roblox client logs" && row.ErrorType == "IOException");
    });

    [Fact]
    public Task SampleModeWithoutAnInjectedCleanupKeepsTheToolOff() => OnUi(async () =>
    {
        await using var shell = await Ready(null, sample: true);
        Assert.False(shell.CanSurveyClientLogs);
        Assert.False(shell.CanDeleteClientLogs);
    });

    private async Task<ShellViewModel> Ready(IClientLogCleanup? cleanup, bool sample = false)
    {
        var shell = new ShellViewModel(_root, sample, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), clientLogCleanup: cleanup);
        try { await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice); return shell; }
        catch { await shell.DisposeAsync(); throw; }
    }

    private sealed class CleanupProbe : IClientLogCleanup
    {
        public ClientLogCleanupSurvey? Survey, Deleted;
        public ClientLogCleanupResult? Result;
        public Exception? SurveyFailure;
        public int Surveys, Deletes;
        public bool Block;
        public TimeSpan? LastOlderThan;
        public CancellationToken LastToken;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ClientLogCleanupSurvey> SurveyAsync(TimeSpan olderThan, CancellationToken ct)
        {
            Surveys++; LastOlderThan = olderThan; LastToken = ct;
            if (SurveyFailure is { } failure) throw failure;
            if (Block) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            return Survey ?? throw new InvalidOperationException("No synthetic survey was prepared.");
        }
        public Task<ClientLogCleanupResult> DeleteAsync(ClientLogCleanupSurvey survey, CancellationToken ct)
        {
            Deletes++; Deleted = survey; LastToken = ct;
            return Task.FromResult(Result ?? throw new InvalidOperationException("No synthetic result was prepared."));
        }
    }

    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) => Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20), []));
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnUi(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => operation = RunAsync());
            async Task RunAsync()
            {
                try { await work(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-logcleanup-ui-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
