using System.IO;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class RecentClientHistoryViewModelTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 21, 0, 0, TimeSpan.Zero);
    private static SessionObservation Client(int id = 10001) => new(id, At.AddMinutes(-1).UtcTicks, "Synthetic", 1, "Synthetic", false, true, 1, 1, null, At);
    private static DesktopSnapshot Frame(long revision, DateTimeOffset time, params SessionObservation[] clients)
        => new(new(time, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20),
            clients.Select(client => client with { ObservedUtc = time }).ToArray(), ProcessObservedUtc: time, ProcessRevision: revision, ProcessInventoryComplete: true);

    [Fact]
    public async Task MissingMetadataAndPartialInventoriesNeverCreateHistoryOrWrites()
    {
        var store = new Store(); await using var vm = new RecentClientHistoryViewModel(store, () => true);
        await vm.InitializeAsync();
        vm.Observe(Frame(1, At, Client()), [], At);
        vm.Observe(Frame(2, At.AddSeconds(1)) with { ProcessInventoryComplete = false }, [], At.AddSeconds(1));
        vm.Observe(Frame(3, At.AddSeconds(2)) with { ProcessObservedUtc = null }, [], At.AddSeconds(2));
        vm.Observe(Frame(4, At.AddSeconds(3)) with { ProcessRevision = 0 }, [], At.AddSeconds(3));
        Assert.Empty(vm.Rows); Assert.Empty(store.Requests); Assert.False(vm.IsSaving);
        vm.Observe(Frame(5, At.AddSeconds(4)), [], At.AddSeconds(4)); await vm.ActiveSave;
        Assert.Single(vm.Rows); Assert.Single(store.Stored.Entries); Assert.Contains("cause unknown", vm.Rows[0].Outcome);
        Assert.False(vm.Rows[0].Entry.CanAuthorizeAccountAction);
    }

    [Fact]
    public async Task OnlySuccessfulExplicitCloseIntentIsPresentedAndPersistedAsARequest()
    {
        var store = new Store(); await using var vm = new RecentClientHistoryViewModel(store, () => true);
        await vm.InitializeAsync(); var client = Client(); vm.Observe(Frame(1, At, client), [], At);
        vm.RecordCloseRequest(new(null, client.ProcessId, client.ProcessStartUtcTicks + 1), At.AddMilliseconds(100));
        vm.RecordCloseRequest(new(null, client.ProcessId, client.ProcessStartUtcTicks), At.AddMilliseconds(200));
        Assert.Empty(store.Requests);
        vm.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1)); await vm.ActiveSave;
        var row = Assert.Single(vm.Rows); Assert.Equal(At.AddMilliseconds(200), row.Entry.CloseRequestedUtc);
        Assert.Contains("close requested", row.Outcome); Assert.Contains("cause unknown", row.Outcome); Assert.DoesNotContain("crashed", row.Outcome);
        Assert.Equal("Unknown", row.Entry.Cause);
        await using var reopened = new RecentClientHistoryViewModel(new Store { Stored=store.Stored }, () => true);
        await reopened.InitializeAsync(); Assert.Single(reopened.Rows); Assert.Empty(store.Requests.Skip(1));
    }

    [Fact]
    public async Task SaveOwnerCoalescesBoundedLatestStateWithoutStartingAWorkerForEveryClient()
    {
        var store = new Store { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = new RecentClientHistoryViewModel(store, () => true);
        await vm.InitializeAsync();
        vm.Observe(Frame(1, At, Client()), [], At);
        vm.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1));
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(vm.IsSaving);
        for (int index = 1; index <= 300; index++)
        {
            var time = At.AddSeconds(index * 2);
            vm.Observe(Frame(index * 2 + 1, time, Client(10001 + index)), [], time);
            vm.Observe(Frame(index * 2 + 2, time.AddSeconds(1)), [], time.AddSeconds(1));
        }
        Assert.Equal(100, vm.Rows.Count); Assert.Single(store.Requests); Assert.Contains("Saving", vm.Status);
        store.Pending.SetResult(); await vm.ActiveSave;
        Assert.Equal(2, store.Requests.Count); Assert.Equal(1, store.MaximumConcurrent);
        Assert.Equal(100, store.Stored.Entries.Count); Assert.Contains("saved", vm.Status);
    }

    [Fact]
    public async Task ClearDuringAnInflightSaveCannotBeOverwrittenByThatOldSnapshot()
    {
        var store = new Store { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = new RecentClientHistoryViewModel(store, () => true);
        await vm.InitializeAsync(); vm.Observe(Frame(1, At, Client()), [], At);
        vm.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1));
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(vm.CanClear); vm.Clear.Execute(null); Assert.Empty(vm.Rows);
        store.Pending.SetResult(); await vm.ActiveSave;
        Assert.Empty(store.Stored.Entries); Assert.Equal(2, store.Requests.Count);
        // Repeated cached/old frames cannot republish the removed ended record.
        vm.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1)); Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task FailedSaveRetainsMemoryAndPreviousDiskStateUntilExplicitRetry()
    {
        var store = new Store { FailSave = true }; List<Exception> failures = [];
        await using var vm = new RecentClientHistoryViewModel(store, () => true, failures.Add);
        await vm.InitializeAsync(); vm.Observe(Frame(1, At, Client()), [], At);
        vm.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1)); await vm.ActiveSave;
        Assert.Single(vm.Rows); Assert.Empty(store.Stored.Entries); Assert.Single(failures);
        Assert.Contains("save failed", vm.Status); Assert.True(vm.CanRetry);
        vm.Observe(Frame(3, At.AddSeconds(2), Client(10002)), [], At.AddSeconds(2));
        vm.Observe(Frame(4, At.AddSeconds(3)), [], At.AddSeconds(3)); Assert.Single(store.Requests);
        store.FailSave = false; vm.Retry.Execute(null); await vm.Retry.Execution;
        Assert.Equal(2, store.Stored.Entries.Count); Assert.False(vm.CanRetry); Assert.Contains("saved", vm.Status);
    }

    [Fact]
    public async Task FailedLoadDisablesTrackingAndCannotOverwriteStoredHistory()
    {
        var store = new Store { FailLoad = true };
        await using var vm = new RecentClientHistoryViewModel(store, () => true);
        await vm.InitializeAsync(); Assert.Contains("unavailable", vm.Status); Assert.True(vm.CanRetry);
        vm.Observe(Frame(1, At, Client()), [], At); vm.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1));
        Assert.Empty(vm.Rows); Assert.Empty(store.Requests);
        store.FailLoad = false; vm.Retry.Execute(null); await vm.Retry.Execution;
        vm.Observe(Frame(3, At.AddSeconds(2), Client()), [], At.AddSeconds(2));
        vm.Observe(Frame(4, At.AddSeconds(3)), [], At.AddSeconds(3)); await vm.ActiveSave;
        Assert.Single(vm.Rows); Assert.Single(store.Requests);
    }

    [Fact]
    public async Task LockedControlsCannotClearOrRetryButMonitoringCanFinishAnObservation()
    {
        bool allowed = true; var store = new Store();
        await using var vm = new RecentClientHistoryViewModel(store, () => allowed);
        await vm.InitializeAsync(); vm.Observe(Frame(1, At, Client()), [], At);
        allowed = false; vm.RefreshAvailability();
        vm.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1)); await vm.ActiveSave;
        Assert.Single(vm.Rows); Assert.False(vm.CanClear); vm.Clear.Execute(null); Assert.Single(vm.Rows);
        allowed = true; vm.RefreshAvailability(); Assert.True(vm.CanClear);
    }

    [Fact]
    public async Task ShutdownAwaitsTheActualSaveOwnerBeforeDisposingStorage()
    {
        var store = new Store { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new RecentClientHistoryViewModel(store, () => true);
        await vm.InitializeAsync(); vm.Observe(Frame(1, At, Client()), [], At);
        vm.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1)); await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopping = vm.DisposeAsync().AsTask(); Assert.False(stopping.IsCompleted); Assert.False(store.Disposed);
        Assert.False(vm.CanClear); store.Pending.SetResult(); await stopping;
        Assert.True(store.Disposed); Assert.Single(store.Stored.Entries); await vm.DisposeAsync();
    }

    [Fact]
    public async Task ExhaustedHistoryRevisionPausesOnlyHistoryInsteadOfThrowingIntoTheMonitor()
    {
        var store=new Store {Stored=new(1,long.MaxValue,Array.Empty<RecentClientHistoryEntry>())};
        List<Exception> failures=[];
        await using var vm=new RecentClientHistoryViewModel(store,()=>true,failures.Add);
        await vm.InitializeAsync();vm.Observe(Frame(1,At,Client()),[],At);
        var error=Record.Exception(()=>vm.Observe(Frame(2,At.AddSeconds(1)),[],At.AddSeconds(1)));
        Assert.Null(error);Assert.Single(failures);Assert.Contains("paused",vm.Status);
        Assert.Empty(store.Requests);Assert.False(vm.CanClear);
        vm.Observe(Frame(3,At.AddSeconds(2)),[],At.AddSeconds(2));Assert.Single(failures);
    }

    [Fact]
    public async Task EachEndedClientGetsOneSettledCrashRecordCheckAndTheOutcomeNamesWhatWindowsRecorded()
    {
        var store = new Store(); var probe = new Probe();
        await using var vm = new RecentClientHistoryViewModel(store, () => true, null, probe);
        await vm.InitializeAsync();
        vm.Observe(Frame(1, At, Client()), [], At);
        vm.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1)); await vm.ActiveSave;
        Assert.Empty(probe.Checked); Assert.Contains("cause unknown", Assert.Single(vm.Rows).Outcome);
        vm.Observe(Frame(3, At.AddSeconds(7)), [], At.AddSeconds(7)); await vm.ActiveFaultCheck; await vm.ActiveSave;
        var row = Assert.Single(vm.Rows);
        Assert.Equal(ClientEndCause.Crashed, row.Entry.EndCause);
        Assert.Equal("No longer observed · Windows recorded a crash (exception 0xC0000005)", row.Outcome);
        Assert.Contains("Windows recorded an application fault for this exact process", row.Details);
        Assert.Equal(row.Entry, Assert.Single(store.Stored.Entries));
        vm.Observe(Frame(4, At.AddSeconds(8)), [], At.AddSeconds(8)); await vm.ActiveFaultCheck;
        Assert.Single(probe.Checked);
    }

    [Theory]
    [InlineData(273, "No longer observed · Roblox logged that the account launched in another client (reason 273)")]
    [InlineData(285, "No longer observed · Roblox logged the player leaving (reason 285)")]
    [InlineData(277, "No longer observed · Roblox logged a disconnect (reason 277)")]
    public void OutcomeNamesALaunchElsewhereSeparatelyFromThePlayerLeaving(int reason, string expected)
    {
        // Reason 273 is a second launch of the same account ending this client, which the live Sessions label already names.
        var key = new SessionProcessKey(null, 10001, At.AddMinutes(-1).UtcTicks);
        var entry = new RecentClientHistoryEntry(Guid.NewGuid(), key, At, At, At.AddSeconds(1),
            DisconnectedUtc: At, DisconnectReason: reason).Validated(ended: true);
        Assert.Equal(expected, new RecentClientHistoryRow(entry).Outcome);
    }

    [Fact]
    public async Task AFailingCrashRecordCheckIsReportedOnceAndShutdownCancelsAnInflightCheck()
    {
        var store = new Store(); var probe = new Probe { Fail = true }; List<Exception> failures = [];
        await using (var vm = new RecentClientHistoryViewModel(store, () => true, failures.Add, probe))
        {
            await vm.InitializeAsync();
            vm.Observe(Frame(1, At, Client(), Client(10002)), [], At);
            vm.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1));
            vm.Observe(Frame(3, At.AddSeconds(7)), [], At.AddSeconds(7)); await vm.ActiveFaultCheck;
            vm.Observe(Frame(4, At.AddSeconds(8)), [], At.AddSeconds(8)); await vm.ActiveFaultCheck; await vm.ActiveSave;
            Assert.Single(failures); Assert.Single(probe.Checked); Assert.Equal(2, vm.Rows.Count);
            Assert.All(vm.Rows, item => Assert.Equal(ClientEndCause.Unknown, item.Entry.EndCause));
        }
        var blocking = new Probe { Block = true }; var other = new Store();
        var stopping = new RecentClientHistoryViewModel(other, () => true, null, blocking);
        await stopping.InitializeAsync();
        stopping.Observe(Frame(1, At, Client()), [], At); stopping.Observe(Frame(2, At.AddSeconds(1)), [], At.AddSeconds(1));
        stopping.Observe(Frame(3, At.AddSeconds(7)), [], At.AddSeconds(7));
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stopping.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(other.Disposed); Assert.True(blocking.Cancelled);
    }

    [Fact]
    public async Task AnInconsistentCrashRecordResultLeavesOnlyThatClientIncompleteAndLaterClientsAreStillChecked()
    {
        // A fault time after the check time cannot be recorded as found; it must not switch checks off for later clients.
        var store = new Store(); var probe = new Probe { FaultAfterCheckFor = 10001 }; List<Exception> failures = [];
        await using var vm = new RecentClientHistoryViewModel(store, () => true, failures.Add, probe);
        await vm.InitializeAsync();
        vm.Observe(Frame(1, At, Client(), Client(10002)), [], At);
        vm.Observe(Frame(2, At.AddSeconds(1), Client(10002)), [], At.AddSeconds(1));
        vm.Observe(Frame(3, At.AddSeconds(7), Client(10002)), [], At.AddSeconds(7)); await vm.ActiveFaultCheck; await vm.ActiveSave;
        var first = Assert.Single(vm.Rows);
        Assert.Equal(new ClientFaultCheck(At.AddSeconds(7), false), first.Entry.FaultCheck);
        Assert.Contains("Crash records could not be fully read.", first.Details);
        Assert.Equal(ClientEndCause.Unknown, first.Entry.EndCause);
        Assert.Equal(first.Entry, Assert.Single(store.Stored.Entries));
        Assert.IsType<ArgumentException>(Assert.Single(failures));
        vm.Observe(Frame(4, At.AddSeconds(8)), [], At.AddSeconds(8));
        vm.Observe(Frame(5, At.AddSeconds(14)), [], At.AddSeconds(14)); await vm.ActiveFaultCheck; await vm.ActiveSave;
        Assert.Equal(2, probe.Checked.Count); Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(ClientEndCause.Crashed, vm.Rows.Single(item => item.Entry.Process.ProcessId == 10002).Entry.EndCause);
        Assert.Single(failures);
    }

    [Fact]
    public void HistoryHelpTextNamesTheEvidenceForEveryOutcomeARowCanShow()
    {
        using var input = typeof(RecentClientHistoryViewModelTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        string help = Assert.Single(System.Xml.Linq.XDocument.Load(input).Descendants().Select(element => (string?)element.Attribute("Text"))
            .OfType<string>().Where(text => text.StartsWith("A fresh complete scan stopped observing", StringComparison.Ordinal)));
        // A new end cause must name its evidence in the panel's help text before this test passes.
        foreach (var cause in Enum.GetValues<ClientEndCause>())
            Assert.Contains(cause switch
            {
                ClientEndCause.Crashed => "a crash Windows recorded for that exact process",
                ClientEndCause.NotResponding => "a hang at its last observation",
                ClientEndCause.Disconnected or ClientEndCause.LeftGame or ClientEndCause.LaunchedElsewhere => "a disconnect in its log",
                ClientEndCause.PossibleCrash => "a Roblox crash dump written as it ended",
                ClientEndCause.Unknown => "Otherwise the cause is unknown",
                _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Name this end cause's evidence in the history help text.")
            }, help);
    }

    private sealed class Probe : IClientFaultProbe
    {
        public bool Fail, Block, Cancelled;
        public int? FaultAfterCheckFor;
        public List<RecentClientHistoryEntry> Checked { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ClientFaultCheck Check(RecentClientHistoryEntry ended, DateTimeOffset now, CancellationToken ct)
        {
            lock (Checked) Checked.Add(ended);
            Started.TrySetResult();
            if (Block) { ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)); Cancelled = ct.IsCancellationRequested; ct.ThrowIfCancellationRequested(); }
            if (Fail) throw new InvalidOperationException("Synthetic probe failure");
            if (FaultAfterCheckFor == ended.Process.ProcessId) return new(now, true, now.AddMilliseconds(500), 0xC0000005);
            return new(now, true, ended.LastObservedUtc, 0xC0000005);
        }
    }

    private sealed class Store : ISessionHistoryStore, IDisposable
    {
        public bool FailLoad, FailSave, Disposed;
        public RecentClientHistorySnapshot Stored = RecentClientHistorySnapshot.Empty;
        public List<RecentClientHistorySnapshot> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Pending;
        private int _active;
        public int MaximumConcurrent;
        public Task<RecentClientHistorySnapshot> LoadAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (FailLoad) throw new IOException("Synthetic read failure"); return Task.FromResult(Stored); }
        public async Task SaveAsync(RecentClientHistorySnapshot snapshot, CancellationToken ct)
        {
            int active = Interlocked.Increment(ref _active); MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            try
            {
                Requests.Add(snapshot); Started.TrySetResult(); if (Pending is { } pending) await pending.Task;
                ct.ThrowIfCancellationRequested(); if (FailSave) throw new IOException("Synthetic write failure"); Stored = snapshot;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void Dispose() => Disposed = true;
    }
}
