using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed partial class SessionLogCorrelatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZenithSyntheticCorrelation-" + Guid.NewGuid().ToString("N"));
    private string Log => Path.Combine(_root, "synthetic_Player_test_last.log");
    public SessionLogCorrelatorTests() => Directory.CreateDirectory(_root);
    private sealed class Clock : TimeProvider
    {
        private long _milliseconds;
        private DateTimeOffset _utc = SyntheticClientLogFixtures.Time;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(int milliseconds) { _utc = _utc.AddMilliseconds(milliseconds); Interlocked.Add(ref _milliseconds, milliseconds); }
    }
    private sealed class Probe(string path, SessionProcessKey process) : IClientLogProbe
    {
        public int Discoveries, Queries, Validations;
        public bool Disposed, Current = true, Block;
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public Action? DuringQuery;
        public Func<string, ResourceHolderResult>? HolderFor;
        public Func<SessionProcessKey, bool>? CurrentClient;
        public readonly List<string> QueriedPaths = [];
        public ResourceHolderResult Result = new(SessionLogCorrelationStatus.ResourceHolderObserved, new[] { new ResourceHolder(process.ProcessId, process.ProcessStartUtcTicks) });
        public IReadOnlyList<ClientLogCandidate> Files = new[] { new ClientLogCandidate(path, ClientLogVariant.Win32, SyntheticClientLogFixtures.Time) };
        public Exception? DiscoverFailure, EveryDiscoverFailure; // One-shot and persistent synthetic probe faults.
        public ClientLogCandidates Discover(int maximum, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Discoveries);
            if (Interlocked.Exchange(ref DiscoverFailure, null) is { } failure) throw failure;
            if (EveryDiscoverFailure is { } persistent) throw persistent;
            return new(Files, false);
        }
        public bool IsCurrentClient(SessionProcessKey key, ClientLogVariant variant)
        {
            Interlocked.Increment(ref Validations);
            return Current && (CurrentClient?.Invoke(key) ?? key == process);
        }
        public ResourceHolderResult QueryHolders(string candidate, CancellationToken ct)
        {
            Interlocked.Increment(ref Queries); QueriedPaths.Add(candidate); Entered.Set();
            if (Block && !Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic query release was not signalled.");
            DuringQuery?.Invoke(); return HolderFor?.Invoke(candidate) ?? Result;
        }
        public void Dispose() { Disposed = true; Entered.Dispose(); Release.Dispose(); }
    }
    private static SessionProcessKey Key(int pid = 42) => new("synthetic-local", pid, SyntheticClientLogFixtures.Time.AddMinutes(-1).UtcTicks);
    private static DesktopSnapshot Desktop(params SessionProcessKey[] keys) => new(new(SyntheticClientLogFixtures.Time, 100, 50, 200, 50, 1),
        keys.Select(key => new SessionObservation(key.ProcessId, key.ProcessStartUtcTicks, "Synthetic", 0, "Synthetic", false, false, 0, 0, null, SyntheticClientLogFixtures.Time)).ToArray());
    private static async Task<SessionLogDiscoverySnapshot> After(WindowsSessionLogCorrelator worker, long revision = 0)
    {
        long began = Stopwatch.GetTimestamp();
        while (worker.Snapshot.Revision <= revision && Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(worker.Snapshot.Revision > revision, "The bounded diagnostic owner did not publish a result.");
        return worker.Snapshot;
    }
    private void Write(string contents) => File.WriteAllText(Log, contents, new UTF8Encoding(false));
    private ClientLogCandidate Candidate(string name, string contents = "")
    {
        string path = Path.Combine(_root, $"synthetic_Player_{name}_last.log");
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        return new(path, ClientLogVariant.Win32, SyntheticClientLogFixtures.Time);
    }
    private static ResourceHolderResult HeldBy(SessionProcessKey process) => new(SessionLogCorrelationStatus.ResourceHolderObserved,
        [new(process.ProcessId, process.ProcessStartUtcTicks)]);

    [Fact]
    public async Task NoEligibleProcessesPerformNoDiscoveryOrNativeQueries()
    {
        var probe = new Probe(Log, Key());
        await using var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop());
        Assert.Empty((await After(worker)).Sessions);
        Assert.Equal(0, probe.Discoveries); Assert.Equal(0, probe.Queries);
    }

    [Fact]
    public async Task HolderOutputIsTypedUnverifiedCredentialFreeAndReplaysMissedFactsWithoutRestamping()
    {
        Write(SyntheticClientLogFixtures.Joining.TrimEnd('\n') + " ticket=SYNTHETIC_SECRET_ONLY\n" + SyntheticClientLogFixtures.Identity);
        var clock = new Clock(); var probe = new Probe(Log, Key());
        await using var worker = new WindowsSessionLogCorrelator(probe, timeProvider: clock);
        worker.Offer(Desktop(Key()), Key().HostId);
        var first = await After(worker); var row = Assert.Single(first.Sessions);
        Assert.Equal(SessionLogCorrelationStatus.ResourceHolderObserved, row.Correlation);
        Assert.False(row.CanAuthorizeAccountAction);
        Assert.Equal(2, row.Batch!.Observations.Count);
        Assert.Equal(2, probe.Validations);
        Assert.All(row.Batch.Observations, fact => Assert.Equal(ClientLogFormatSupport.Unverified, fact.FormatSupport));
        var original = row.Batch.Observations[0];
        File.AppendAllText(Log, SyntheticClientLogFixtures.Connected);
        clock.Advance(1000); worker.Offer(Desktop(Key()), Key().HostId);
        var next = Assert.Single((await After(worker, first.Revision)).Sessions);
        Assert.Equal(3, next.Batch!.Observations.Count);
        Assert.Equal(original, next.Batch.Observations[0]);
        Assert.Equal(row.SourceStamp, next.SourceStamp); Assert.Equal(row.LeaseExpiresUtc, next.LeaseExpiresUtc);
        Assert.Equal(1, probe.Queries);
        string json = JsonSerializer.Serialize(next);
        Assert.DoesNotContain(Log, json); Assert.DoesNotContain(_root, json); Assert.DoesNotContain("SYNTHETIC_SECRET_ONLY", json);
    }

    [Fact]
    public async Task SamePidWithDifferentCreationTimeCannotSupplyFacts()
    {
        Write(SyntheticClientLogFixtures.Identity);
        var probe = new Probe(Log, Key()) { Result = new(SessionLogCorrelationStatus.ResourceHolderObserved,
            new[] { new ResourceHolder(Key().ProcessId, Key().ProcessStartUtcTicks + 1) }) };
        await using var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(Key()), Key().HostId);
        var row = Assert.Single((await After(worker)).Sessions);
        Assert.Equal(SessionLogCorrelationStatus.Unknown, row.Correlation); Assert.Null(row.Batch);
    }

    [Fact]
    public async Task MultipleHoldersAreAmbiguousAndNeverChooseTheClosestProcess()
    {
        Write(SyntheticClientLogFixtures.Identity);
        var probe = new Probe(Log, Key()) { Result = new(SessionLogCorrelationStatus.ResourceHolderObserved,
            new[] { new ResourceHolder(42, Key().ProcessStartUtcTicks), new ResourceHolder(99, Key().ProcessStartUtcTicks) }) };
        await using var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(Key()), Key().HostId);
        var row = Assert.Single((await After(worker)).Sessions);
        Assert.Equal(SessionLogCorrelationStatus.Ambiguous, row.Correlation); Assert.Null(row.Batch);
    }

    [Fact]
    public async Task ReplacementDuringHolderQueryDiscardsTheWholeRead()
    {
        Write(SyntheticClientLogFixtures.Joining);
        var probe = new Probe(Log, Key()) { DuringQuery = () => { File.Move(Log, Log + ".old"); Write(SyntheticClientLogFixtures.Identity); } };
        await using var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(Key()), Key().HostId);
        var row = Assert.Single((await After(worker)).Sessions);
        Assert.Equal(SessionLogCorrelationStatus.SourceChanged, row.Correlation); Assert.Null(row.Batch);
    }

    [Fact]
    public async Task RewriteInvalidatesTheOldGenerationAndRebindReadsOnlyNewFacts()
    {
        Write(SyntheticClientLogFixtures.Joining); var clock = new Clock();
        await using var worker = new WindowsSessionLogCorrelator(new Probe(Log, Key()), timeProvider: clock);
        worker.Offer(Desktop(Key()), Key().HostId); var first = await After(worker);
        var oldStream = Assert.Single(first.Sessions).SourceStamp;
        Write(SyntheticClientLogFixtures.Identity); clock.Advance(1000); worker.Offer(Desktop(Key()), Key().HostId);
        var changed = await After(worker, first.Revision);
        Assert.Equal(SessionLogCorrelationStatus.SourceChanged, Assert.Single(changed.Sessions).Correlation);
        clock.Advance(1000); worker.Offer(Desktop(Key()), Key().HostId);
        var rebound = Assert.Single((await After(worker, changed.Revision)).Sessions);
        Assert.NotEqual(oldStream, rebound.SourceStamp);
        Assert.Equal(ClientLogFact.UserIdentityObserved, Assert.Single(rebound.Batch!.Observations).Fact);
    }

    [Fact]
    public async Task LostHolderAndProcessValidationRevokePreviouslyPublishedFacts()
    {
        Write(SyntheticClientLogFixtures.Identity); var clock = new Clock(); var probe = new Probe(Log, Key());
        await using var worker = new WindowsSessionLogCorrelator(probe, timeProvider: clock);
        worker.Offer(Desktop(Key()), Key().HostId); var first = await After(worker);
        probe.Current = false; clock.Advance(1000); worker.Offer(Desktop(Key()), Key().HostId);
        var lost = Assert.Single((await After(worker, first.Revision)).Sessions);
        Assert.NotEqual(SessionLogCorrelationStatus.ResourceHolderObserved, lost.Correlation); Assert.Null(lost.Batch);
    }

    [Fact]
    public async Task LatestOnlyInputReplacesQueuedSnapshotsAndDisposalOwnsAnInFlightCall()
    {
        Write(SyntheticClientLogFixtures.Identity); var probe = new Probe(Log, Key()) { Block = true };
        var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(Key()), Key().HostId);
        Assert.True(await Task.Run(() => probe.Entered.Wait(TimeSpan.FromSeconds(5))));
        for (int i = 0; i < 100; i++) worker.Offer(Desktop(Key(100 + i)), Key().HostId);
        worker.Offer(Desktop());
        probe.Release.Set();
        Assert.Empty((await After(worker)).Sessions);
        Assert.Equal(1, probe.Discoveries); Assert.Equal(1, probe.Queries);
        await worker.DisposeAsync(); Assert.True(probe.Disposed);
    }

    [Fact]
    public async Task CancellationDoesNotAbandonAStartedSynchronousQuery()
    {
        Write(SyntheticClientLogFixtures.Identity); var probe = new Probe(Log, Key()) { Block = true };
        var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(Key()), Key().HostId);
        Assert.True(await Task.Run(() => probe.Entered.Wait(TimeSpan.FromSeconds(5))));
        var disposal = worker.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted); probe.Release.Set();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(probe.Disposed);
    }

    [Fact]
    public async Task AOneOffProbeFailurePublishesUnavailableAndTheNextOfferRecovers()
    {
        // Win32Marshal maps ERROR_OPERATION_ABORTED to OperationCanceledException although shutdown was never requested.
        Write(SyntheticClientLogFixtures.Identity);
        var clock = new Clock(); var probe = new Probe(Log, Key()) { DiscoverFailure = new OperationCanceledException() };
        var worker = new WindowsSessionLogCorrelator(probe, timeProvider: clock);
        worker.Offer(Desktop(Key()), Key().HostId);
        var failed = await After(worker);
        var row = Assert.Single(failed.Sessions);
        Assert.Equal(SessionLogCorrelationStatus.Unavailable, row.Correlation); Assert.Null(row.Batch);

        clock.Advance(1000); worker.Offer(Desktop(Key()), Key().HostId);
        var next = Assert.Single((await After(worker, failed.Revision)).Sessions);
        Assert.Equal(SessionLogCorrelationStatus.ResourceHolderObserved, next.Correlation);
        Assert.NotNull(next.Batch);
        Assert.Equal(2, probe.Discoveries);
        await worker.DisposeAsync(); // A recovered pass failure is not reported again at shutdown.
        Assert.True(probe.Disposed);
    }

    [Fact]
    public async Task FiveConsecutiveProbeFailuresStillEndTheWorkerAndSurfaceAtDisposal()
    {
        Write(SyntheticClientLogFixtures.Identity);
        var clock = new Clock();
        var probe = new Probe(Log, Key()) { EveryDiscoverFailure = new InvalidOperationException("Synthetic persistent probe fault.") };
        var worker = new WindowsSessionLogCorrelator(probe, timeProvider: clock);
        long revision = 0;
        for (int pass = 0; pass < 5; pass++)
        {
            if (pass != 0) clock.Advance(1000);
            worker.Offer(Desktop(Key()), Key().HostId);
            var snapshot = await After(worker, revision); revision = snapshot.Revision;
            Assert.Equal(SessionLogCorrelationStatus.Unavailable, Assert.Single(snapshot.Sessions).Correlation);
        }
        Assert.Equal(5, probe.Discoveries);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.DisposeAsync().AsTask());
        Assert.True(probe.Disposed);
    }

    [Fact]
    public async Task ProcessAndReaderWorkLimitsAreExplicit()
    {
        Write(SyntheticClientLogFixtures.Identity); var probe = new Probe(Log, Key());
        probe.Files = Enumerable.Range(0, 6).Select(i =>
        {
            string path = Path.Combine(_root, $"synthetic_Player_{i}_last.log");
            File.WriteAllText(path, SyntheticClientLogFixtures.Identity, new UTF8Encoding(false));
            return new ClientLogCandidate(path, ClientLogVariant.Win32, SyntheticClientLogFixtures.Time);
        }).ToArray();
        await using var worker = new WindowsSessionLogCorrelator(probe, new(MaximumReaders: 2, MaximumProcesses: 2, ReadsPerPass: 1));
        worker.Offer(Desktop(Key(), Key(43), Key(44)), Key().HostId);
        var result = await After(worker);
        Assert.Equal(2, result.Sessions.Count); Assert.True(result.BudgetLimited); Assert.InRange(probe.Queries, 0, 1);
    }

    [Fact]
    public async Task SlowQueriesAdvanceOnlyVisitedReadersSoEveryCandidateGetsATurn()
    {
        var clock = new Clock(); var probe = new Probe(Log, Key()) { DuringQuery = () => clock.Advance(3000) };
        probe.Files = Enumerable.Range(0, 4).Select(i =>
        {
            string path = Path.Combine(_root, $"synthetic_Player_slow{i}_last.log");
            File.WriteAllText(path, SyntheticClientLogFixtures.Identity, new UTF8Encoding(false));
            return new ClientLogCandidate(path, ClientLogVariant.Win32, SyntheticClientLogFixtures.Time);
        }).ToArray();
        await using var worker = new WindowsSessionLogCorrelator(probe,
            new(MaximumReaders: 4, ReadsPerPass: 4), clock);
        long revision = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            worker.Offer(Desktop(Key()), Key().HostId);
            var result = await After(worker, revision); revision = result.Revision;
            Assert.True(result.BudgetLimited);
            Assert.Equal(pass + 1, probe.Queries);
        }
        Assert.Equal(4, probe.QueriedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task PartialReconciliationCannotOverrideAnUnresolvedMultipleLogAmbiguity()
    {
        var clock = new Clock(); var probe = new Probe(Log, Key());
        probe.Files = Enumerable.Range(0, 3).Select(i =>
        {
            string path = Path.Combine(_root, $"synthetic_Player_ambiguous{i}_last.log");
            File.WriteAllText(path, SyntheticClientLogFixtures.Identity, new UTF8Encoding(false));
            return new ClientLogCandidate(path, ClientLogVariant.Win32, SyntheticClientLogFixtures.Time);
        }).ToArray();
        await using var worker = new WindowsSessionLogCorrelator(probe,
            new(MaximumReaders: 3, ReadsPerPass: 1), clock);
        long revision = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            if (pass != 0) clock.Advance(1000);
            worker.Offer(Desktop(Key()), Key().HostId);
            var snapshot = await After(worker, revision); revision = snapshot.Revision;
            if (pass != 0)
            {
                var row = Assert.Single(snapshot.Sessions);
                Assert.Equal(SessionLogCorrelationStatus.Ambiguous, row.Correlation); Assert.Null(row.Batch);
            }
        }
    }

    // Sixteen running clients, each holding its own log, behind 24 newer history logs that no process holds.
    private (SessionProcessKey[] Keys, Probe Probe) SixteenRunningClients()
    {
        var keys = Enumerable.Range(100, 16).Select(Key).ToArray();
        var expected = new Dictionary<string, SessionProcessKey>();
        var files = Enumerable.Range(0, 24).Select(i => Candidate($"history{i}")).ToList();
        foreach (var key in keys)
        {
            string contents = SyntheticClientLogFixtures.Joining.Replace(
                SyntheticClientLogFixtures.Place.ToString(), (SyntheticClientLogFixtures.Place + key.ProcessId).ToString())
                + SyntheticClientLogFixtures.Identity.Replace(
                    SyntheticClientLogFixtures.User.ToString(), (SyntheticClientLogFixtures.User + key.ProcessId).ToString())
                + SyntheticClientLogFixtures.Connected;
            var file = Candidate($"running{key.ProcessId}", contents);
            expected.Add(file.Path, key);
            files.Add(file);
        }
        var probe = new Probe(Log, keys[0])
        {
            Files = files,
            CurrentClient = key => keys.Contains(key),
            HolderFor = path => expected.TryGetValue(path, out var key) ? HeldBy(key) : new(SessionLogCorrelationStatus.Unknown, [])
        };
        return (keys, probe);
    }

    [Fact]
    public async Task SixteenBoundReadersKeepTheirHolderLeaseWhenPassesRunEveryTwoSeconds()
    {
        // One read slot per pass goes to a history scout, so each of the 16 bound readers is revisited
        // only every 5-6 passes. At one pass per 2 s that is 10-12 s between holder re-queries.
        var clock = new Clock();
        var (keys, probe) = SixteenRunningClients();
        await using var worker = new WindowsSessionLogCorrelator(probe, timeProvider: clock);
        var desktop = Desktop(keys);
        long revision = 0;
        int steady = -1;
        for (int pass = 0; pass < 80 && steady < 30; pass++)
        {
            if (pass != 0) clock.Advance(2000);
            int queriesBefore = probe.Queries;
            worker.Offer(desktop, keys[0].HostId);
            var snapshot = await After(worker, revision); revision = snapshot.Revision;
            Assert.InRange(probe.Queries - queriesBefore, 0, 4);
            if (steady < 0)
            {
                if (snapshot.Sessions.All(row => row.Batch is not null)) steady = 0;
                continue;
            }
            steady++;
            Assert.Equal(16, snapshot.Sessions.Count);
            var lapsed = snapshot.Sessions.Where(row => row.Correlation != SessionLogCorrelationStatus.ResourceHolderObserved
                    || row.Batch is null || row.LeaseExpiresUtc is not { } expiry || expiry <= snapshot.ObservedUtc
                    || snapshot.ObservedUtc - row.ObservedUtc > NativeTrialEvidence.MaximumLogAge)
                .Select(row => $"{row.Process.ProcessId}:{row.Correlation}").ToArray();
            Assert.True(lapsed.Length == 0,
                $"Steady pass {steady} at +{(snapshot.ObservedUtc - SyntheticClientLogFixtures.Time).TotalSeconds:0}s: "
                + $"{lapsed.Length} bound client(s) lost their holder lease or freshness between visits: {string.Join(", ", lapsed)}");
        }
        Assert.Equal(30, steady);
    }

    [Fact]
    public async Task SixteenClientsAreCorrelatedBehindNewerUnownedCandidatesWithinThePassBudget()
    {
        var clock = new Clock();
        var (keys, probe) = SixteenRunningClients();
        await using var worker = new WindowsSessionLogCorrelator(probe, timeProvider: clock);
        long revision = 0;
        SessionLogDiscoverySnapshot? complete = null;
        var desktop = Desktop(keys);
        for (int pass = 0; pass < 50; pass++)
        {
            int queriesBefore = probe.Queries;
            worker.Offer(desktop, keys[0].HostId);
            var snapshot = await After(worker, revision); revision = snapshot.Revision;
            Assert.InRange(probe.Queries - queriesBefore, 0, 4);
            if (snapshot.Sessions.All(row => row.Batch is not null))
            {
                complete = snapshot;
                break;
            }
            clock.Advance(1000);
        }
        Assert.NotNull(complete);
        Assert.InRange(complete.ObservedUtc - SyntheticClientLogFixtures.Time, TimeSpan.Zero, TimeSpan.FromSeconds(24));
        Assert.Equal(40, complete.CandidateCount);
        Assert.Equal(16, complete.Sessions.Select(row => row.SourceStamp).Distinct().Count());
        foreach (var row in complete.Sessions)
        {
            Assert.False(row.CanAuthorizeAccountAction);
            var facts = row.Batch!.Observations;
            Assert.Equal(SyntheticClientLogFixtures.Place + row.Process.ProcessId, facts.Single(f => f.Fact == ClientLogFact.Joining).PlaceId);
            Assert.Equal(SyntheticClientLogFixtures.User + row.Process.ProcessId, facts.Single(f => f.Fact == ClientLogFact.UserIdentityObserved).UserId);
        }
    }

    [Fact]
    public async Task AnAttachedReaderKeepsItsCursorWhenPushedOutOfTheDiscoveryWindow()
    {
        var clock = new Clock();
        var process = Key();
        var active = Candidate("active", SyntheticClientLogFixtures.Identity);
        var probe = new Probe(active.Path, process)
        {
            HolderFor = path => path == active.Path ? HeldBy(process) : new(SessionLogCorrelationStatus.Unknown, [])
        };
        await using var worker = new WindowsSessionLogCorrelator(probe,
            new(MaximumReaders: 1, MaximumCandidates: 2, ReadsPerPass: 2), clock);
        var desktop = Desktop(process);
        worker.Offer(desktop, process.HostId);
        var first = await After(worker);
        var original = Assert.Single(first.Sessions);
        Assert.Single(original.Batch!.Observations);

        probe.Files = [Candidate("newer0"), Candidate("newer1")];
        File.AppendAllText(active.Path, SyntheticClientLogFixtures.Joining);
        clock.Advance(5000);
        worker.Offer(desktop, process.HostId);
        var after = await After(worker, first.Revision);
        var row = Assert.Single(after.Sessions);
        Assert.Equal(2, after.CandidateCount);
        Assert.Equal(original.SourceStamp, row.SourceStamp);
        Assert.Equal(2, row.Batch!.Observations.Count);
        Assert.Equal(original.Batch.Observations[0], row.Batch.Observations[0]);
        Assert.Contains(probe.QueriedPaths, path => path != active.Path);
    }

    [Fact]
    public async Task ACompetingHolderBeyondTheReaderLimitInvalidatesTheEarlierCorrelation()
    {
        var clock = new Clock();
        var process = Key();
        var first = Candidate("first", SyntheticClientLogFixtures.Identity);
        var second = Candidate("second", SyntheticClientLogFixtures.Identity);
        var probe = new Probe(Log, process) { Files = [first, second] };
        await using var worker = new WindowsSessionLogCorrelator(probe,
            new(MaximumReaders: 1, MaximumCandidates: 2, ReadsPerPass: 1), clock);
        var desktop = Desktop(process);
        worker.Offer(desktop, process.HostId);
        var initial = await After(worker);
        Assert.NotNull(Assert.Single(initial.Sessions).Batch);

        clock.Advance(1000);
        worker.Offer(desktop, process.HostId);
        var conflict = Assert.Single((await After(worker, initial.Revision)).Sessions);
        Assert.Equal(SessionLogCorrelationStatus.Ambiguous, conflict.Correlation);
        Assert.Null(conflict.Batch);
        Assert.Equal(new[] { first.Path, second.Path }, probe.QueriedPaths);
    }

    [Fact]
    public async Task ExitedProcessReleasesItsReadSlotForADifferentInstance()
    {
        var clock = new Clock();
        var departed = Key();
        var waiting = Key(43);
        var first = Candidate("exiting", SyntheticClientLogFixtures.Identity);
        var second = Candidate("waiting", SyntheticClientLogFixtures.Joining);
        var probe = new Probe(Log, departed)
        {
            Files = [first, second],
            CurrentClient = _ => true,
            HolderFor = path => HeldBy(path == first.Path ? departed : waiting)
        };
        await using var worker = new WindowsSessionLogCorrelator(probe,
            new(MaximumReaders: 1, MaximumCandidates: 2, ReadsPerPass: 2), clock);
        worker.Offer(Desktop(departed, waiting), departed.HostId);
        var full = await After(worker);
        Assert.True(full.BudgetLimited);
        var old = full.Sessions.Single(row => row.Process == departed);
        Assert.NotNull(old.Batch);
        var queued = full.Sessions.Single(row => row.Process == waiting);
        Assert.Equal(SessionLogCorrelationStatus.BudgetExceeded, queued.Correlation);
        Assert.Null(queued.Batch);
        clock.Advance(5000);
        worker.Offer(Desktop(waiting), waiting.HostId);
        var row = Assert.Single((await After(worker, full.Revision)).Sessions);
        Assert.Equal(waiting, row.Process);
        Assert.NotEqual(old.SourceStamp, row.SourceStamp);
        Assert.Equal(ClientLogFact.Joining, Assert.Single(row.Batch!.Observations).Fact);
    }

    [Fact]
    public void RestartManagerAbiMatchesNativeHeaderWithoutPointerSizedSessionHandles()
    {
        Assert.Equal(12, Marshal.SizeOf<RestartManagerLogQuery.UniqueProcess>());
        Assert.Equal(668, Marshal.SizeOf<RestartManagerLogQuery.ProcessInfo>());
    }

    [Fact]
    public void RestartManagerCanObserveOnlyOurOwnTemporaryFileHolder()
    {
        // Real OS query, deliberately no Roblox process, client log, credential file or shutdown/restart call.
        string path = Path.Combine(_root, "owned-resource.tmp");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        stream.WriteByte(1); stream.Flush();
        using var process = Process.GetCurrentProcess();
        var result = RestartManagerLogQuery.Query(path, default);
        Assert.Equal(SessionLogCorrelationStatus.ResourceHolderObserved, result.Status);
        Assert.Contains(result.Holders, holder => holder.ProcessId == process.Id
            && holder.ProcessStartUtcTicks == process.StartTime.ToUniversalTime().Ticks);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
