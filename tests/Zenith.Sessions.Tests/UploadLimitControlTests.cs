using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-instances-18: the Windows control runs Zenith's QoS script through Windows PowerShell as administrator (the UAC prompt the
// user answers), one operation at a time, and keeps a small record of what it applied in this Windows session. Here the
// administrator run is a fake, so nothing on this PC changes; the production runner is exercised in UploadLimitNativeTests.
public sealed class UploadLimitControlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-upload-limit-" + Guid.NewGuid().ToString("N"));
    private string RecordPath => Path.Combine(_root, WindowsUploadLimitControl.RecordFileName);
    private static readonly DateTimeOffset Boot = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Boot.AddHours(3);

    private WindowsUploadLimitControl Control(FakeRunner runner, DateTimeOffset? now = null, TimeSpan? uptime = null)
        => new(RecordPath, runner, new FixedClock(now ?? Now), () => uptime ?? (Now - Boot));

    [Fact]
    public async Task ApplyRunsTheApplyScriptElevatedAndRecordsTheConfirmedLimitForThisWindowsSession()
    {
        var runner = new FakeRunner { Outcome = new(ElevatedRunKind.Exited, 0) };
        await using var control = Control(runner);
        Assert.Null(await control.LoadRecordAsync(CancellationToken.None));

        var result = await control.ApplyAsync(2500, CancellationToken.None);

        Assert.Equal(UploadLimitStatus.Confirmed, result.Status);
        Assert.Equal(UploadLimitAction.Apply, result.Action);
        Assert.Equal(2500, result.KilobitsPerSecond);
        Assert.True(result.RecordSaved);
        var run = Assert.Single(runner.Runs);
        Assert.Equal(UploadLimitPolicy.ApplyScript(2500), run.Script);
        Assert.Equal(WindowsUploadLimitControl.HelperTimeout, run.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(60), run.Timeout);
        var current = Assert.IsType<UploadLimitRecord>(control.Current);
        Assert.Equal(new UploadLimitRecord(UploadLimitAction.Apply, 2500, Now, true), current);
        Assert.Equal(current, result.Current);
        // The record names the rate, the time, and the Windows session; nothing else.
        using var file = JsonDocument.Parse(await File.ReadAllBytesAsync(RecordPath));
        Assert.Equal(1, file.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(2500, file.RootElement.GetProperty("kilobitsPerSecond").GetInt32());
        Assert.True(file.RootElement.GetProperty("confirmed").GetBoolean());
        Assert.Equal("Apply", file.RootElement.GetProperty("lastAction").GetString());
        Assert.Equal(Now, file.RootElement.GetProperty("atUtc").GetDateTimeOffset());
        Assert.Equal(Boot, file.RootElement.GetProperty("bootUtc").GetDateTimeOffset());
        Assert.False(file.RootElement.GetProperty("helperUnresolved").GetBoolean());
        Assert.Equal(7, file.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task TheRecordIsReadBackByALaterRunInTheSameWindowsSessionAndDroppedAfterARestart()
    {
        var runner = new FakeRunner { Outcome = new(ElevatedRunKind.Exited, 0) };
        await using (var first = Control(runner)) await first.ApplyAsync(1000, CancellationToken.None);
        await using (var sameSession = Control(runner, Now.AddMinutes(20), Now.AddMinutes(20) - Boot.AddSeconds(45)))
        {
            Assert.Equal(new UploadLimitRecord(UploadLimitAction.Apply, 1000, Now, true), await sameSession.LoadRecordAsync(CancellationToken.None));
            Assert.False(sameSession.RecordUnreadable);
        }
        // Windows restarted since: the active store is empty again, so the record is dropped and the file removed.
        var afterRestart = Now.AddDays(1);
        await using (var restarted = Control(runner, afterRestart, TimeSpan.FromMinutes(10)))
        {
            Assert.Null(await restarted.LoadRecordAsync(CancellationToken.None));
            Assert.Null(restarted.Current);
            Assert.False(restarted.RecordUnreadable);
        }
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public async Task ADamagedOwnershipRecordIsPreservedAndPreventsAnotherHelper()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(RecordPath, "{ not json");
        var runner = new FakeRunner { Outcome = new(ElevatedRunKind.Exited, 0) };
        await using var control = Control(runner);
        Assert.Null(await control.LoadRecordAsync(CancellationToken.None));
        Assert.True(control.RecordUnreadable);
        var result = await control.ApplyAsync(100, CancellationToken.None);
        Assert.Equal(UploadLimitStatus.OwnershipUnavailable, result.Status);
        Assert.True(control.RecordUnreadable);
        Assert.Empty(runner.Runs);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(RecordPath));
    }

    [Fact]
    public async Task RemoveRunsTheRemoveScriptAndAConfirmedRemovalClearsTheRecord()
    {
        var runner = new FakeRunner { Outcome = new(ElevatedRunKind.Exited, 0) };
        await using var control = Control(runner);
        await control.ApplyAsync(1000, CancellationToken.None);
        Assert.True(File.Exists(RecordPath));

        var result = await control.RemoveAsync(CancellationToken.None);

        Assert.Equal(UploadLimitStatus.Confirmed, result.Status);
        Assert.Equal(UploadLimitAction.Remove, result.Action);
        Assert.Equal(UploadLimitPolicy.RemoveScript(), runner.Runs[1].Script);
        Assert.Null(control.Current);
        Assert.Null(result.Current);
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public async Task ADeclinedPromptOrAMissingPowerShellChangesNothingAndKeepsTheRecord()
    {
        var runner = new FakeRunner { Outcome = new(ElevatedRunKind.Exited, 0) };
        await using var control = Control(runner);
        await control.ApplyAsync(1000, CancellationToken.None);
        var before = control.Current;

        runner.Outcome = new(ElevatedRunKind.Declined);
        var declined = await control.ApplyAsync(2000, CancellationToken.None);
        Assert.Equal(UploadLimitStatus.Declined, declined.Status);
        Assert.Equal(before, control.Current);

        runner.Outcome = new(ElevatedRunKind.NotStarted);
        var unavailable = await control.RemoveAsync(CancellationToken.None);
        Assert.Equal(UploadLimitStatus.Unavailable, unavailable.Status);
        Assert.Equal(before, control.Current);
        Assert.Equal(before, declined.Current);
    }

    [Theory]
    [InlineData(1, UploadLimitStatus.Refused)]
    [InlineData(3, UploadLimitStatus.NotConfirmed)]
    public async Task AFailedOrUnconfirmedRunLeavesTheRecordUncertainWithTheRequestedRate(int exitCode, UploadLimitStatus expected)
    {
        var runner = new FakeRunner { Outcome = new(ElevatedRunKind.Exited, 0) };
        await using var control = Control(runner);
        await control.ApplyAsync(1000, CancellationToken.None);

        runner.Outcome = new(ElevatedRunKind.Exited, exitCode);
        var result = await control.ApplyAsync(3000, CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Equal(exitCode, result.ExitCode);
        var uncertain = new UploadLimitRecord(UploadLimitAction.Apply, 3000, Now, false);
        Assert.Equal(uncertain, control.Current);
        Assert.Equal(uncertain, result.Current);
        // The uncertainty is kept for the next run too.
        await using var later = Control(new FakeRunner());
        Assert.Equal(uncertain, await later.LoadRecordAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnUnconfirmedRemoveKeepsTheLastKnownRateInTheUncertainRecord()
    {
        var runner = new FakeRunner { Outcome = new(ElevatedRunKind.Exited, 0) };
        await using var control = Control(runner);
        await control.ApplyAsync(1000, CancellationToken.None);
        runner.Outcome = new(ElevatedRunKind.Exited, 3);
        var result = await control.RemoveAsync(CancellationToken.None);
        Assert.Equal(UploadLimitStatus.NotConfirmed, result.Status);
        Assert.Equal(new UploadLimitRecord(UploadLimitAction.Remove, 1000, Now, false), control.Current);
    }

    [Fact]
    public async Task ATimedOutHelperIsReportedAndLeavesTheRecordUncertain()
    {
        var runner = new FakeRunner { Outcome = new(ElevatedRunKind.TimedOut) };
        await using var control = Control(runner);
        var result = await control.ApplyAsync(1000, CancellationToken.None);
        Assert.Equal(UploadLimitStatus.TimedOut, result.Status);
        Assert.Equal(new UploadLimitRecord(UploadLimitAction.Apply, 1000, Now, false, HelperUnresolved: true), control.Current);
    }

    [Fact]
    public async Task AnInvalidRateNeverStartsTheAdministratorRun()
    {
        var runner = new FakeRunner();
        await using var control = Control(runner);
        var result = await control.ApplyAsync(99, CancellationToken.None);
        Assert.Equal(UploadLimitStatus.InvalidRate, result.Status);
        Assert.Empty(runner.Runs);
        Assert.Null(control.Current);
    }

    [Fact]
    public async Task OneOperationAtATimeAndCancelStopsTheWaitAndMarksTheRecordUncertain()
    {
        var runner = new FakeRunner { Hold = true };
        await using var control = Control(runner);
        var first = control.ApplyAsync(1000, CancellationToken.None);
        var run = await runner.NextAsync();
        var busy = await control.RemoveAsync(CancellationToken.None);
        Assert.Equal(UploadLimitStatus.Busy, busy.Status);
        Assert.Single(runner.Runs);
        Assert.False(first.IsCompleted);

        control.CancelOperations();

        var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UploadLimitStatus.Cancelled, result.Status);
        Assert.True(run.Token.IsCancellationRequested);
        Assert.Equal(new UploadLimitRecord(UploadLimitAction.Apply, 1000, Now, false, HelperUnresolved: true), control.Current);
        // Cancellation does not release an external writer. A definite no-start does.
        Assert.Equal(UploadLimitStatus.HelperUnresolved, (await control.RemoveAsync(default)).Status);
        run.AbandonedStartCompleted?.Invoke(new(ElevatedRunKind.Declined));
        runner.Hold = false; runner.Outcome = new(ElevatedRunKind.Exited, 0);
        Assert.Equal(UploadLimitStatus.Confirmed, (await control.RemoveAsync(CancellationToken.None)).Status);
        Assert.Null(control.Current);
    }

    [Fact]
    public async Task TheCallersTokenCancelsTheWaitToo()
    {
        var runner = new FakeRunner { Hold = true };
        await using var control = Control(runner);
        using var caller = new CancellationTokenSource();
        var pending = control.ApplyAsync(1000, caller.Token);
        await runner.NextAsync();
        caller.Cancel();
        Assert.Equal(UploadLimitStatus.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task DisposeCancelsTheWaitAndLaterCallsAreClosed()
    {
        var runner = new FakeRunner { Hold = true };
        var control = Control(runner);
        var pending = control.ApplyAsync(1000, CancellationToken.None);
        await runner.NextAsync();
        await control.DisposeAsync();
        Assert.Equal(UploadLimitStatus.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(UploadLimitStatus.Closed, (await control.RemoveAsync(CancellationToken.None)).Status);
        Assert.Equal(UploadLimitStatus.Closed, (await control.ApplyAsync(1000, CancellationToken.None)).Status);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task AConfirmedChangeWhoseRecordCannotBeSavedIsStillConfirmedAndSaysTheRecordIsMissing()
    {
        // The initial ownership marker succeeds; only the final result write fails.
        var runner = new FakeRunner { Outcome = new(ElevatedRunKind.Exited, 0), BeforeReturn = () => { File.Delete(RecordPath); Directory.CreateDirectory(RecordPath); } };
        await using var control = Control(runner);
        var result = await control.ApplyAsync(1000, CancellationToken.None);
        Assert.Equal(UploadLimitStatus.Confirmed, result.Status);
        Assert.False(result.RecordSaved);
        Assert.Contains("could not save its record", result.Explanation, StringComparison.Ordinal);
        // This run still knows what it applied.
        Assert.Equal(new UploadLimitRecord(UploadLimitAction.Apply, 1000, Now, true), control.Current);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    public async Task ALateDeclineOrFailedStartRestoresTheExactPreOperationRecord(bool existing, bool remove, int lateKind)
    {
        var runner = new FakeRunner();
        await using var control = Control(runner);
        if (existing) await control.ApplyAsync(1500, default);
        var before = control.Current;
        runner.Hold = true;
        var pending = remove ? control.RemoveAsync(default) : control.ApplyAsync(2500, default);
        var abandoned = await runner.WaitForRunsAsync(existing ? 2 : 1);
        control.CancelOperations();
        Assert.Equal(UploadLimitStatus.Cancelled, (await pending).Status);
        Assert.False(control.Current?.Confirmed);
        abandoned.AbandonedStartCompleted?.Invoke(new((ElevatedRunKind)lateKind));
        Assert.Equal(before, control.Current);
        await using var reloaded = Control(new FakeRunner());
        Assert.Equal(before, await reloaded.LoadRecordAsync(default));
    }

    [Fact]
    public async Task ALateDeclineCannotOverwriteANewerConfirmedOperation()
    {
        var runner = new FakeRunner { Hold = true };
        await using var control = Control(runner);
        var pending = control.ApplyAsync(1000, default);
        var abandoned = await runner.WaitForRunsAsync(1);
        control.CancelOperations(); await pending;
        abandoned.AbandonedStartCompleted?.Invoke(new(ElevatedRunKind.Declined));
        runner.Hold = false;
        await control.ApplyAsync(4000, default);
        var latest = control.Current;
        abandoned.AbandonedStartCompleted?.Invoke(new(ElevatedRunKind.Declined));
        Assert.Equal(latest, control.Current);
        await using var reloaded = Control(new FakeRunner());
        Assert.Equal(latest, await reloaded.LoadRecordAsync(default));
    }

    [Fact]
    public async Task ADeclineReportedAtTheCancellationBoundaryDoesNotLeaveUncertainty()
    {
        var runner = new FakeRunner { Hold = true, DeclineDuringCancel = true };
        await using var control = Control(runner);
        var pending = control.ApplyAsync(1000, default);
        await runner.WaitForRunsAsync(1);
        control.CancelOperations();
        Assert.Equal(UploadLimitStatus.Declined, (await pending).Status);
        Assert.Null(control.Current); Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public async Task ADeclineAfterDisposeRestoresPersistenceWithoutPublishingIntoTheClosedShell()
    {
        var runner = new FakeRunner();
        var control = Control(runner);
        await control.ApplyAsync(1700, default); var before = control.Current;
        int published = 0; control.Reconciled += _ => published++;
        runner.Hold = true;
        var pending = control.RemoveAsync(default); var abandoned = await runner.WaitForRunsAsync(2);
        await control.DisposeAsync(); await pending;
        abandoned.AbandonedStartCompleted?.Invoke(new(ElevatedRunKind.Declined));
        Assert.Equal(0, published); Assert.Equal(before, control.Current);
        await using var reloaded = Control(new FakeRunner());
        Assert.Equal(before, await reloaded.LoadRecordAsync(default));
    }

    [Fact]
    public async Task UnreadableOwnershipNeverStartsAHelperOrClaimsAbsence()
    {
        Directory.CreateDirectory(_root); await File.WriteAllTextAsync(RecordPath, "broken");
        var runner = new FakeRunner { Hold = true };
        await using var control = Control(runner);
        await control.LoadRecordAsync(default); Assert.True(control.RecordUnreadable);
        var result = await control.ApplyAsync(1000, default);
        Assert.Equal(UploadLimitStatus.OwnershipUnavailable, result.Status);
        Assert.Empty(runner.Runs); Assert.True(control.RecordUnreadable);
        await using var reloaded = Control(new FakeRunner());
        await reloaded.LoadRecordAsync(default); Assert.True(reloaded.RecordUnreadable);
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-upload-limit-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    internal sealed record FakeRun(string Script, TimeSpan Timeout, CancellationToken Token, Action<ElevatedRunOutcome>? AbandonedStartCompleted);

    internal sealed class FakeRunner : IElevatedPowerShellRunner
    {
        private readonly Channel<FakeRun> _started = Channel.CreateUnbounded<FakeRun>();
        public List<FakeRun> Runs { get; } = [];
        public ElevatedRunOutcome Outcome { get; set; } = new(ElevatedRunKind.Exited, 0);
        public bool Hold { get; set; }
        public bool DeclineDuringCancel { get; set; }
        public Action? BeforeReturn { get; set; }
        public async Task<ElevatedRunOutcome> RunAsync(string script, TimeSpan timeout, CancellationToken ct, Action<ElevatedRunOutcome>? abandonedStartCompleted = null)
        {
            var run = new FakeRun(script, timeout, ct, abandonedStartCompleted);
            lock (Runs) Runs.Add(run);
            _started.Writer.TryWrite(run);
            if (Hold)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    if (DeclineDuringCancel) abandonedStartCompleted?.Invoke(new(ElevatedRunKind.Declined));
                    throw;
                }
            }
            BeforeReturn?.Invoke();
            return Outcome;
        }
        public async Task<FakeRun> WaitForRunsAsync(int count)
        {
            while (true)
            {
                lock (Runs) if (Runs.Count >= count) return Runs[count - 1];
                await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<FakeRun> NextAsync() => _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
