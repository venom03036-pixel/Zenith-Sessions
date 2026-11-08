using System.Runtime.InteropServices;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>Fabricated PDH buffers only. No hardware, Roblox process, account, SDK or performance claim.</summary>
public sealed class GpuTelemetryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private const string Luid = "0x00000000_0x00001234";
    private static string EngineName(int pid, int engine, string type = "3D", int physical = 0, string luid = Luid)
        => $"pid_{pid}_luid_{luid}_phys_{physical}_eng_{engine}_engtype_{type}";
    private static string MemoryName(int physical = 0, string luid = Luid) => $"luid_{luid}_phys_{physical}";
    private static string ProcessMemoryName(int pid, int physical = 0, string luid = Luid) => $"pid_{pid}_luid_{luid}_phys_{physical}";

    [Fact]
    public void FirstRateSampleIsUnknownWhileAdapterMemoryCanBeKnown()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.Add(new(EngineName(10, 0), 75)); api.Dedicated.Add(new(MemoryName(), Bytes: 1024)); api.Shared.Add(new(MemoryName(), Bytes: 0));
        using var reader = new WindowsGpuTelemetryReader(clock, api);
        var sample = reader.Read(); var adapter = Assert.Single(sample.Adapters);
        Assert.Null(adapter.BusiestEnginePercent); Assert.False(adapter.EngineDataComplete);
        Assert.Null(Assert.Single(adapter.Engines).UtilizationPercent);
        Assert.Equal(1024UL, adapter.DedicatedBytes); Assert.Equal(0UL, adapter.SharedBytes);
        Assert.Equal(Start, sample.ObservedUtc); Assert.NotNull(sample.Problem);
        clock.Advance(); var ready = reader.Read();
        Assert.Equal(75d, Assert.Single(ready.Adapters).BusiestEnginePercent); Assert.Null(ready.Problem);
        Assert.True(api.EngineFormat.HasFlag(0x8000)); // Native formatting must not silently cap bad data at100.
        Assert.Equal(1, api.Opens); Assert.Equal(3, api.Adds);
    }

    [Fact]
    public void ProcessesAreSummedOnlyWithinTheSamePhysicalEngineThenBusiestIsSelected()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.AddRange([new(EngineName(10, 0), 60), new(EngineName(20, 0), 20),
            new(EngineName(10, 1, "Copy"), 10), new(EngineName(20, 1, "Copy"), 50)]);
        api.Dedicated.Add(new(MemoryName(), Bytes: 4096)); api.Shared.Add(new(MemoryName(), Bytes: 2048));
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
        var adapter = Assert.Single(reader.Read().Adapters);
        Assert.Equal(80d, adapter.BusiestEnginePercent); // Sum of PID maxima would incorrectly be110.
        Assert.Equal(new double?[] { 80, 60 }, adapter.Engines.Select(e => e.UtilizationPercent));
        Assert.True(adapter.EngineDataComplete); Assert.Equal(Luid, adapter.AdapterLuid); Assert.Equal(0, adapter.PhysicalAdapterIndex);
    }

    [Fact]
    public void LinkedPhysicalAdaptersAndDifferentLuidsStayDistinctWithoutDoubleCountingMemory()
    {
        const string other = "0x00000001_0x00001234";
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.AddRange([new(EngineName(1, 0), 40), new(EngineName(2, 0, physical: 1), 90), new(EngineName(3, 0, luid: other), 25)]);
        api.Dedicated.AddRange([new(MemoryName(), Bytes: 100), new(MemoryName(1), Bytes: 200), new(MemoryName(luid: other), Bytes: 300)]);
        api.Shared.AddRange([new(MemoryName(), Bytes: 0), new(MemoryName(1), Bytes: 10), new(MemoryName(luid: other), Bytes: 20)]);
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance(); var sample = reader.Read();
        Assert.Equal(3, sample.Adapters.Count);
        Assert.Equal(new double?[] { 40, 90, 25 }, sample.Adapters.Select(a => a.BusiestEnginePercent));
        Assert.Equal(new ulong?[] { 100, 200, 300 }, sample.Adapters.Select(a => a.DedicatedBytes));
        Assert.Equal(1, sample.Adapters[1].PhysicalAdapterIndex); Assert.Equal(other, sample.Adapters[2].AdapterLuid);
    }

    [Theory]
    [InlineData(double.NaN, 0u)] [InlineData(double.PositiveInfinity, 0u)]
    [InlineData(-1d, 0u)] [InlineData(101d, 0u)] [InlineData(20d, 0xc0000bbau)]
    public void InvalidValuesOrPerItemStatusRemainUnknown(double percent, uint status)
    {
        var clock = new Clock(); var api = new FakePdh(clock); api.Engines.Add(new(EngineName(1, 0), percent, Status: status));
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
        var adapter = Assert.Single(reader.Read().Adapters);
        Assert.Null(adapter.BusiestEnginePercent); Assert.Null(Assert.Single(adapter.Engines).UtilizationPercent);
        Assert.False(adapter.EngineDataComplete);
    }

    [Fact]
    public void EngineSumOverOneHundredAndConflictingEngineTypesAreUnknownNotClamped()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.AddRange([new(EngineName(1, 0), 60), new(EngineName(2, 0), 60),
            new(EngineName(1, 1, "Copy"), 10), new(EngineName(2, 1, "Compute"), 20)]);
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
        var adapter = Assert.Single(reader.Read().Adapters);
        Assert.Null(adapter.BusiestEnginePercent); Assert.All(adapter.Engines, engine => Assert.Null(engine.UtilizationPercent));
    }

    [Theory]
    [InlineData("pid_1_luid_bad_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x00000000_0x00001234_phys_-1_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x00000000_0x00001234_phys_0_eng_0_engtype_3D#1")]
    [InlineData("_Total")]
    public void UnrecognizedInstanceLayoutsCannotAdvertiseCompleteGpuLoad(string unexpected)
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.AddRange([new(EngineName(1, 0), 25), new(unexpected, 50)]);
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance(); var sample = reader.Read();
        Assert.NotNull(sample.Problem); Assert.Null(Assert.Single(sample.Adapters).BusiestEnginePercent);
    }

    [Fact]
    public void DuplicateInstancesAndDuplicateMemoryDoNotBecomeLargerValidTotals()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.AddRange([new(EngineName(1, 0), 20), new(EngineName(1, 0), 20)]);
        api.Dedicated.AddRange([new(MemoryName(), Bytes: 100), new(MemoryName(), Bytes: 100)]);
        api.Shared.Add(new(MemoryName(), Bytes: -1));
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
        var adapter = Assert.Single(reader.Read().Adapters);
        Assert.Null(adapter.BusiestEnginePercent); Assert.Null(adapter.DedicatedBytes); Assert.Null(adapter.SharedBytes);
    }

    [Fact]
    public void AdapterAndEngineOutputBudgetsCannotGrowWithProviderInventory()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        for (int physical = 0; physical <= WindowsGpuTelemetryReader.MaximumAdapters; physical++)
            api.Engines.Add(new(EngineName(1, 0, physical: physical), 1));
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
        var crowded = reader.Read(); Assert.Equal(WindowsGpuTelemetryReader.MaximumAdapters, crowded.Adapters.Count);
        Assert.All(crowded.Adapters, adapter => Assert.Null(adapter.BusiestEnginePercent));
        api.Engines.Clear();
        for (int engine = 0; engine <= WindowsGpuTelemetryReader.MaximumEnginesPerAdapter; engine++)
            api.Engines.Add(new(EngineName(1, engine), 1));
        clock.Advance(); var manyEngines = Assert.Single(reader.Read().Adapters);
        Assert.Equal(WindowsGpuTelemetryReader.MaximumEnginesPerAdapter, manyEngines.Engines.Count);
        Assert.Null(manyEngines.BusiestEnginePercent);
    }

    [Fact]
    public void PidReuseDoesNotInheritAnOldProcessAttribution()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.Add(new(EngineName(123, 0), 90));
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
        Assert.Equal(90d, Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        api.Engines.Clear(); clock.Advance(); Assert.Empty(reader.Read().Adapters);
        api.Engines.Add(new(EngineName(123, 0), 5)); clock.Advance();
        var adapter = Assert.Single(reader.Read().Adapters); Assert.Equal(5d, adapter.BusiestEnginePercent);
        // Only the newly collected adapter aggregate exists: there is no PID/account keyed cache or model.
        Assert.Single(adapter.Engines);
    }

    [Fact]
    public void MissingCountersDoNotInvalidateRamAdmissionOrInventGpuZero()
    {
        var clock = new Clock(); var api = new FakePdh(clock) { AllCountersMissing = true };
        using var reader = new WindowsGpuTelemetryReader(clock, api); var gpu = reader.Read();
        Assert.Empty(gpu.Adapters); Assert.Null(gpu.ObservedUtc); Assert.False(gpu.IsFresh(Start));
        var memory = new ResourceSnapshot(Start, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB,
            48 * CapacityPolicy.GiB, 16 * CapacityPolicy.GiB, 10, Gpu: gpu);
        Assert.True(memory.IsValid); Assert.True(CapacityPolicy.Evaluate(memory, new(), [], 0, Start).SamplesHealthy);
        Assert.Equal(1, api.Closes); reader.Read(); Assert.Equal(1, api.Opens);
        clock.Advance(31); reader.Read(); Assert.Equal(2, api.Opens);
    }

    [Fact]
    public void InvalidNativeBufferPointersAndBoundsAreRejectedBeforeDereference()
    {
        foreach (var mode in new[] { BufferFault.Oversize, BufferFault.TooManyItems, BufferFault.OutOfRangeName,
            BufferFault.UnterminatedName, BufferFault.ShortItems, BufferFault.AlwaysGrowing })
        {
            var clock = new Clock(); var api = new FakePdh(clock) { Fault = mode };
            api.Engines.Add(new(EngineName(1, 0), 30)); api.Dedicated.Add(new(MemoryName(), Bytes: 1));
            using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
            var sample = reader.Read(); Assert.NotNull(sample.Problem);
            Assert.All(sample.Adapters, a => Assert.Null(a.BusiestEnginePercent));
            Assert.True(api.MaximumRequestedBuffer <= WindowsGpuTelemetryReader.MaximumBufferBytes);
            Assert.True(api.EngineFills <= 4); // At most2 bounded fill attempts per read.
        }
    }

    [Fact]
    public void PreCancelledReadAndCancellationAfterSynchronousCollectionCannotPublish()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        using var reader = new WindowsGpuTelemetryReader(clock, api);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.Read(canceled.Token)); Assert.Equal(0, api.Opens);
        using var during = new CancellationTokenSource(); api.AfterCollect = during.Cancel;
        Assert.Throws<OperationCanceledException>(() => reader.Read(during.Token));
        Assert.Equal(0, api.ArrayCalls); api.AfterCollect = null;
        api.Engines.Add(new(EngineName(1, 0), 10)); clock.Advance();
        Assert.Null(Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
    }

    [Fact]
    public void CancelledCounterInitializationDisposesPartialQueryBeforeRetry()
    {
        var clock = new Clock(); var api = new FakePdh(clock); api.Engines.Add(new(EngineName(1, 0), 0, Status: 1));
        using var reader = new WindowsGpuTelemetryReader(clock, api); using var canceled = new CancellationTokenSource();
        api.AfterAdd = canceled.Cancel;
        Assert.Throws<OperationCanceledException>(() => reader.Read(canceled.Token)); Assert.Equal(1, api.Closes);
        api.AfterAdd = null; clock.Advance(); Assert.Null(Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        clock.Advance(); Assert.Equal(0d, Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        Assert.Equal(2, api.Opens); Assert.Equal(4, api.Adds);
    }

    [Fact]
    public void PartiallyMissingCounterCanRecoverThroughExistingCadenceWithoutAppRestart()
    {
        var clock = new Clock(); var api = new FakePdh(clock) { EngineMissing = true };
        api.Engines.Add(new(EngineName(1, 0), 20)); api.Dedicated.Add(new(MemoryName(), Bytes: 100));
        using var reader = new WindowsGpuTelemetryReader(clock, api);
        Assert.Null(Assert.Single(reader.Read().Adapters).BusiestEnginePercent); api.EngineMissing = false;
        clock.Advance(31); Assert.Null(Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        clock.Advance(); Assert.Equal(20d, Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        // The missing counter is re-added to the open query; it is not closed and rebuilt.
        Assert.Equal(1, api.Opens); Assert.Equal(0, api.Closes);
    }

    [Fact]
    public void MissingMemoryGaugeRetryCannotLatchAnEstablishedGpuHold()
    {
        // Reader and pressure tracker together at the real 1 s system cadence. Only the Dedicated
        // memory gauge is missing, so engine evidence is complete; the 30 s partial-counter retry
        // must not turn into a periodic unknown engine sample that blocks GPU-hold recovery.
        var clock = new Clock(); var api = new FakePdh(clock) { DedicatedMissing = true };
        var engine = new Row(EngineName(1, 0), 90); api.Engines.Add(engine); api.Shared.Add(new(MemoryName(), Bytes: 10));
        using var reader = new WindowsGpuTelemetryReader(clock, api); var tracker = new ResourcePressureTracker();
        var unknownAfterWarmUp = new List<int>(); int step = 0;
        PressureDecision Step()
        {
            var now = clock.GetUtcNow(); var gpu = reader.Read();
            if (step++ > 0 && gpu.Adapters.Any(a => a.BusiestEnginePercent is null)) unknownAfterWarmUp.Add(step - 1);
            var sample = new ResourceSnapshot(now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB,
                64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20, Gpu: gpu);
            var decision = tracker.Observe(sample, CapacityPolicy.Evaluate(sample, new(), [], 0, now), now);
            clock.Advance(); return decision;
        }
        PressureDecision last = null!;
        for (int i = 0; i < 17; i++) last = Step();
        Assert.True(last.Held);
        api.Engines[0] = engine with { Percent = 10 };
        for (int i = 0; i < 70; i++) last = Step();
        Assert.False(last.Held, last.Explanation);
        Assert.Empty(unknownAfterWarmUp);
        Assert.Equal(1, api.Opens); Assert.Equal(0, api.Closes);
    }

    [Fact]
    public void ReAddedEngineCounterWarmsUpAgainBeforeReportingLoadAtNormalCadence()
    {
        var clock = new Clock(); var api = new FakePdh(clock) { EngineMissing = true };
        api.Engines.Add(new(EngineName(1, 0), 20)); api.Dedicated.Add(new(MemoryName(), Bytes: 100)); api.Shared.Add(new(MemoryName(), Bytes: 0));
        using var reader = new WindowsGpuTelemetryReader(clock, api);
        for (int second = 0; second < 30; second++)
        { Assert.Null(Assert.Single(reader.Read().Adapters).BusiestEnginePercent); clock.Advance(); }
        api.EngineMissing = false;
        // The first collection that includes the newly added rate counter is its baseline, not load.
        var reAdded = Assert.Single(reader.Read().Adapters);
        Assert.Null(reAdded.BusiestEnginePercent); Assert.Equal(100UL, reAdded.DedicatedBytes);
        clock.Advance(); Assert.Equal(20d, Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        Assert.Equal(1, api.Opens); Assert.Equal(0, api.Closes);
    }

    [Fact]
    public void CancelledMissingCounterRetryClosesTheQueryItWasAddingTo()
    {
        var clock = new Clock(); var api = new FakePdh(clock) { DedicatedMissing = true };
        api.Engines.Add(new(EngineName(1, 0), 30)); api.Shared.Add(new(MemoryName(), Bytes: 10));
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read();
        clock.Advance(30); using var canceled = new CancellationTokenSource(); api.AfterAdd = canceled.Cancel;
        Assert.Throws<OperationCanceledException>(() => reader.Read(canceled.Token)); Assert.Equal(1, api.Closes);
        api.AfterAdd = null; clock.Advance(); Assert.Null(Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        clock.Advance(); Assert.Equal(30d, Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        Assert.Equal(2, api.Opens);
    }

    [Fact]
    public void CollectionFailureClosesQueryAndCannotRefreshCachedEvidence()
    {
        var clock = new Clock(); var api = new FakePdh(clock); api.Engines.Add(new(EngineName(1, 0), 10));
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
        var known = reader.Read(); Assert.True(known.IsFresh(clock.GetUtcNow()));
        api.CollectStatus = 0xc0000bc6; clock.Advance(); var failed = reader.Read();
        Assert.Null(failed.ObservedUtc); Assert.Empty(failed.Adapters); Assert.Equal(1, api.Closes);
        clock.Advance(3); Assert.False(known.IsFresh(clock.GetUtcNow()));
        api.CollectStatus = 0; clock.Advance(31);
        Assert.Null(Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
    }

    [Fact]
    public void StaleFutureAndRepeatedPdhTimesAreUnknownAndCanRecover()
    {
        foreach (int offset in new[] { -10, 10, 0 })
        {
            var clock = new Clock(); var api = new FakePdh(clock); api.Engines.Add(new(EngineName(1, 0), 10));
            using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
            api.FixedTime = offset == 0 ? Start : clock.GetUtcNow().AddSeconds(offset);
            Assert.Null(reader.Read().ObservedUtc);
            api.FixedTime = null; clock.Advance(); Assert.Null(Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
            clock.Advance(); Assert.Equal(10d, Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        }
    }

    [Fact]
    public void LongCollectionGapRequiresANewUtilizationBaselineWithoutDroppingMemoryGauges()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.Add(new(EngineName(1, 0), 50)); api.Dedicated.Add(new(MemoryName(), Bytes: 100));
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
        Assert.Equal(50d, Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        clock.Advance(10); var afterGap = Assert.Single(reader.Read().Adapters);
        Assert.Null(afterGap.BusiestEnginePercent); Assert.Equal(100UL, afterGap.DedicatedBytes);
        clock.Advance(); Assert.Equal(50d, Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
    }

    [Fact]
    public void ExistingSharedCadenceCachesWithoutAdditionalQueriesOrRenewedGpuTime()
    {
        var clock = new Clock(); var api = new FakePdh(clock); api.Engines.Add(new(EngineName(1, 0), 10));
        using var reader = new WindowsGpuTelemetryReader(clock, api); var cadence = new DesktopSamplingCadence(clock);
        ResourceSnapshot SystemSample(CancellationToken ct) => new(clock.GetUtcNow(), 1, 1, 1, 0, 0, Gpu: reader.Read(ct));
        DesktopProcessInventory Processes(CancellationToken _) => new([], true);
        var first = cadence.SampleInventory(5000, SystemSample, Processes, default);
        var cached = cadence.SampleInventory(5000, SystemSample, Processes, default);
        Assert.Same(first.Resources.Gpu, cached.Resources.Gpu); Assert.Equal(1, api.Collections);
        clock.Advance(); var second = cadence.SampleInventory(5000, SystemSample, Processes, default);
        Assert.Equal(2, api.Collections); Assert.Equal(clock.GetUtcNow(), second.Resources.Gpu!.ObservedUtc);
        Assert.Equal(first.ProcessObservedUtc, second.ProcessObservedUtc);
    }

    [Fact]
    public void QueryAndBufferOwnershipEndsExactlyOnce()
    {
        var clock = new Clock(); var api = new FakePdh(clock); api.Engines.Add(new(EngineName(1, 0), 10));
        var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); reader.Dispose(); reader.Dispose();
        Assert.Equal(1, api.Closes); Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }

    // Per-client numbers (R-instances-15). A PID carries a client's GPU rows only when the same PID with the same start time
    // was seen before the collection (the candidates) and again after it (the check), so a reused PID is never credited.

    [Fact]
    public void ClientNumbersAreAttributedOnlyToAPidConfirmedWithItsStartTimeAfterTheCollection()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.AddRange([new(EngineName(10, 0), 60), new(EngineName(10, 1, "Copy"), 10), new(EngineName(20, 0), 20), new(EngineName(30, 0), 5)]);
        api.ProcessMemory.AddRange([new(ProcessMemoryName(10), Bytes: 1024), new(ProcessMemoryName(20), Bytes: 2048), new(ProcessMemoryName(30), Bytes: 4096)]);
        api.Dedicated.Add(new(MemoryName(), Bytes: 8192)); api.Shared.Add(new(MemoryName(), Bytes: 0));
        using var reader = new WindowsGpuTelemetryReader(clock, api);
        var asked = new List<(int Pid, long Start, int Collection)>();
        // Candidates are the clients an inventory saw before this read; 20 is refused by the check after the collection.
        var clients = new GpuClientAttribution(new Dictionary<int, long> { [10] = 111, [20] = 222, [40] = 444 },
            (pid, start, _) => { asked.Add((pid, start, api.Collections)); return pid == 10; });
        var first = reader.Read(clients, default);
        var warming = Assert.Single(first.Clients);
        Assert.Equal((10, 111L), (warming.ProcessId, warming.ProcessStartUtcTicks));
        Assert.Null(warming.BusiestEnginePercent); Assert.Equal(1024UL, warming.DedicatedBytes); // Rates warm up; the gauge is known.
        clock.Advance(); var ready = reader.Read(clients, default);
        var client = Assert.Single(ready.Clients);
        Assert.Equal((10, 111L), (client.ProcessId, client.ProcessStartUtcTicks));
        Assert.Equal(60d, client.BusiestEnginePercent); Assert.Equal(1024UL, client.DedicatedBytes);
        // Only candidates with GPU rows are checked, each after the collection that produced its rows; 30 is not a client
        // and 40 has no rows, so neither is asked about.
        Assert.Equal(new[] { (10, 111L, 1), (20, 222L, 1), (10, 111L, 2), (20, 222L, 2) }, asked);
        // The adapter view is unchanged: contributors on one physical engine are still summed (60 + 20 + 5).
        Assert.Equal(85d, Assert.Single(ready.Adapters).BusiestEnginePercent);
        Assert.Equal(4, api.Adds); Assert.Contains(WindowsGpuTelemetryReader.ProcessMemoryCounterPath, api.AddedPaths);
    }

    [Fact]
    public void WithoutClientAttributionNoProcessGaugeIsAddedAndNoClientIsPublished()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.Add(new(EngineName(10, 0), 40)); api.ProcessMemory.Add(new(ProcessMemoryName(10), Bytes: 100));
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
        var sample = reader.Read();
        Assert.Empty(sample.Clients); Assert.Equal(3, api.Adds); Assert.Equal(0, api.ProcessMemoryReads);
        Assert.DoesNotContain(WindowsGpuTelemetryReader.ProcessMemoryCounterPath, api.AddedPaths);
    }

    [Fact]
    public void TurningClientNumbersOnAddsTheGaugeToTheOpenQueryWithoutLosingTheEngineBaselineAndOffStopsReadingIt()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.Add(new(EngineName(10, 0), 40)); api.ProcessMemory.Add(new(ProcessMemoryName(10), Bytes: 100));
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(); clock.Advance();
        Assert.Equal(40d, Assert.Single(reader.Read().Adapters).BusiestEnginePercent);
        var clients = new GpuClientAttribution(new Dictionary<int, long> { [10] = 5 }, (_, _, _) => true);
        clock.Advance(); var attributed = reader.Read(clients, default);
        var client = Assert.Single(attributed.Clients);
        Assert.Equal(40d, client.BusiestEnginePercent); Assert.Equal(100UL, client.DedicatedBytes); // Added, not rebuilt: no new warm-up.
        Assert.Equal(1, api.Opens); Assert.Equal(0, api.Closes); Assert.Equal(4, api.Adds); Assert.Equal(1, api.ProcessMemoryReads);
        clock.Advance(); var off = reader.Read();
        Assert.Empty(off.Clients); Assert.Equal(1, api.ProcessMemoryReads);
        Assert.Equal(40d, Assert.Single(off.Adapters).BusiestEnginePercent);
    }

    [Fact]
    public void InvalidOrOverflowingEngineRowsAndUnrecognizedDuplicateOrMissingMemoryLeaveClientValuesUnknownNotZero()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        // 10: invalid engine status; 20: no memory instance; 30 and 40 share an engine whose sum exceeds 100; 40 has memory on two adapters.
        api.Engines.AddRange([new(EngineName(10, 0), 20, Status: 0xc0000bba), new(EngineName(20, 1, "Copy"), 15),
            new(EngineName(30, 2, "Compute"), 60), new(EngineName(40, 2, "Compute"), 60)]);
        api.ProcessMemory.AddRange([new(ProcessMemoryName(10), Bytes: 100), new(ProcessMemoryName(30), Bytes: 300),
            new(ProcessMemoryName(40), Bytes: 400), new(ProcessMemoryName(40, physical: 1), Bytes: 50)]);
        var clients = new GpuClientAttribution(new Dictionary<int, long> { [10] = 1, [20] = 2, [30] = 3, [40] = 4 }, (_, _, _) => true);
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(clients, default); clock.Advance();
        var rows = reader.Read(clients, default).Clients.ToDictionary(c => c.ProcessId);
        Assert.Equal(4, rows.Count);
        Assert.Null(rows[10].BusiestEnginePercent); Assert.Equal(100UL, rows[10].DedicatedBytes);
        Assert.Equal(15d, rows[20].BusiestEnginePercent); Assert.Null(rows[20].DedicatedBytes);
        Assert.Null(rows[30].BusiestEnginePercent); Assert.Null(rows[40].BusiestEnginePercent);
        Assert.Equal(300UL, rows[30].DedicatedBytes); Assert.Equal(450UL, rows[40].DedicatedBytes); // Bytes on two adapters add up.
        // One instance outside the recognized convention makes every client's memory unknown; engines are untouched.
        api.ProcessMemory.Add(new("pid_20_luid_0x00000000_0x00001234_phys_0#1", Bytes: 7)); clock.Advance();
        rows = reader.Read(clients, default).Clients.ToDictionary(c => c.ProcessId);
        Assert.All(rows.Values, c => Assert.Null(c.DedicatedBytes)); Assert.Equal(15d, rows[20].BusiestEnginePercent);
        // A duplicate memory instance makes that client's memory unknown, never doubled; the others keep theirs.
        api.ProcessMemory.RemoveAt(api.ProcessMemory.Count - 1); api.ProcessMemory.Add(new(ProcessMemoryName(30), Bytes: 300)); clock.Advance();
        rows = reader.Read(clients, default).Clients.ToDictionary(c => c.ProcessId);
        Assert.Null(rows[30].DedicatedBytes); Assert.Equal(450UL, rows[40].DedicatedBytes);
    }

    [Fact]
    public void AMissingProcessGaugeIsRetriedAfterThirtySecondsWhileClientEngineNumbersStayAvailable()
    {
        var clock = new Clock(); var api = new FakePdh(clock) { ProcessMemoryMissing = true };
        api.Engines.Add(new(EngineName(10, 0), 40)); api.ProcessMemory.Add(new(ProcessMemoryName(10), Bytes: 100));
        var clients = new GpuClientAttribution(new Dictionary<int, long> { [10] = 5 }, (_, _, _) => true);
        using var reader = new WindowsGpuTelemetryReader(clock, api); reader.Read(clients, default); clock.Advance();
        var client = Assert.Single(reader.Read(clients, default).Clients);
        Assert.Equal(40d, client.BusiestEnginePercent); Assert.Null(client.DedicatedBytes); Assert.Equal(4, api.Adds);
        api.ProcessMemoryMissing = false; clock.Advance();
        Assert.Null(Assert.Single(reader.Read(clients, default).Clients).DedicatedBytes); Assert.Equal(4, api.Adds); // Not before 30 s.
        clock.Advance(29); Assert.Equal(100UL, Assert.Single(reader.Read(clients, default).Clients).DedicatedBytes);
        Assert.Equal(5, api.Adds); Assert.Equal(1, api.Opens); Assert.Equal(0, api.Closes);
    }

    [Fact]
    public void CancellationDuringTheClientCheckPublishesNothingAndTheReaderRecovers()
    {
        var clock = new Clock(); var api = new FakePdh(clock);
        api.Engines.Add(new(EngineName(10, 0), 40)); api.ProcessMemory.Add(new(ProcessMemoryName(10), Bytes: 100));
        using var reader = new WindowsGpuTelemetryReader(clock, api); using var canceled = new CancellationTokenSource();
        var clients = new GpuClientAttribution(new Dictionary<int, long> { [10] = 5 },
            (_, _, ct) => { canceled.Cancel(); ct.ThrowIfCancellationRequested(); return true; });
        Assert.Throws<OperationCanceledException>(() => reader.Read(clients, canceled.Token)); Assert.Equal(1, api.Closes);
        var quiet = new GpuClientAttribution(clients.Candidates, (_, _, _) => true);
        clock.Advance(); Assert.Null(Assert.Single(reader.Read(quiet, default).Clients).BusiestEnginePercent);
        clock.Advance(); Assert.Equal(40d, Assert.Single(reader.Read(quiet, default).Clients).BusiestEnginePercent);
    }

    [Fact]
    public async Task TheMonitorAttributesGpuOnlyToClientsItsInventorySawThatStillCarryTheSameStartAfterTheCollection()
    {
        var clock = new Clock(); var api = new FakePdh(clock); var source = new FakeClientProcessSource();
        source.Clients.AddRange([new(1000, 0, 1, 1, 1), new(1001, 0, 1, 1, 1), new(1002, 0, 1, 1, 1)]);
        int reads1001 = 0, reads1002 = 0;
        // In one sample a client is read once by the inventory and once by the check after the GPU collection. 1001's number
        // belongs to a process with another start time by the time of the check; 1002 has ended by then.
        source.Reader = (pid, _) => pid switch
        {
            1001 => Reading(++reads1001 % 2 == 0 ? FakeClientProcessSource.Start + 1 : FakeClientProcessSource.Start),
            1002 => ++reads1002 % 2 == 0 ? new(ClientProcessReadOutcome.Exited, FakeClientProcessSource.Start) : Reading(FakeClientProcessSource.Start),
            _ => Reading(FakeClientProcessSource.Start),
        };
        // All four share one physical engine (10 + 20 + 25 + 30 = 85, a valid engine total); 2000 is not a Roblox client.
        api.Engines.AddRange([new(EngineName(1000, 0), 10), new(EngineName(1001, 0), 20), new(EngineName(1002, 0), 25), new(EngineName(2000, 0), 30)]);
        api.ProcessMemory.AddRange([new(ProcessMemoryName(1000), Bytes: 100), new(ProcessMemoryName(1001), Bytes: 200),
            new(ProcessMemoryName(1002), Bytes: 250), new(ProcessMemoryName(2000), Bytes: 300)]);
        using var reader = new WindowsGpuTelemetryReader(clock, api);
        using var monitor = new WindowsDesktopMonitor(clock, reader, source, new NoWindows());
        ((IClientGpuTelemetryControl)monitor).ConfigureClientGpuTelemetry(true);
        await monitor.SampleAsync(default); clock.Advance();
        var sample = await monitor.SampleAsync(default);
        var client = Assert.Single(sample.Resources.Gpu!.Clients);
        Assert.Equal((1000, FakeClientProcessSource.Start), (client.ProcessId, client.ProcessStartUtcTicks));
        Assert.Equal(10d, client.BusiestEnginePercent); Assert.Equal(100UL, client.DedicatedBytes);
        Assert.Equal(85d, Assert.Single(sample.Resources.Gpu.Adapters).BusiestEnginePercent); // The adapter view still sums the engine.
        Assert.Equal(3, sample.Sessions.Count); // The refused clients are still ordinary rows; only their GPU numbers are withheld.
        // Each sample opened every client once for the inventory and once for the check; 2000 is not a client and was never opened.
        Assert.Equal(12, source.Opened.Count); Assert.DoesNotContain(2000, source.Opened); Assert.Equal(2, source.Snapshots);
    }

    [Fact]
    public async Task WithClientNumbersOffTheMonitorOpensNothingExtraAndPublishesNoClient()
    {
        var clock = new Clock(); var api = new FakePdh(clock); var source = new FakeClientProcessSource();
        source.Clients.Add(new(1000, 0, 1, 1, 1));
        api.Engines.Add(new(EngineName(1000, 0), 30)); api.ProcessMemory.Add(new(ProcessMemoryName(1000), Bytes: 100));
        using var reader = new WindowsGpuTelemetryReader(clock, api);
        using var monitor = new WindowsDesktopMonitor(clock, reader, source, new NoWindows());
        await monitor.SampleAsync(default); clock.Advance(); var sample = await monitor.SampleAsync(default);
        Assert.Empty(sample.Resources.Gpu!.Clients); Assert.Equal(2, source.Opened.Count); Assert.Equal(3, api.Adds);
        Assert.Equal(30d, Assert.Single(sample.Resources.Gpu.Adapters).BusiestEnginePercent);
    }

    [Fact]
    public async Task AFailedInventoryLeavesNoCandidatesSoTheNextCollectionAttributesNothingAndChecksNothing()
    {
        var clock = new Clock(); var api = new FakePdh(clock); var source = new FakeClientProcessSource();
        source.Clients.Add(new(1000, 0, 1, 1, 1));
        api.Engines.Add(new(EngineName(1000, 0), 30)); api.ProcessMemory.Add(new(ProcessMemoryName(1000), Bytes: 100));
        using var reader = new WindowsGpuTelemetryReader(clock, api);
        using var monitor = new WindowsDesktopMonitor(clock, reader, source, new NoWindows());
        ((IClientGpuTelemetryControl)monitor).ConfigureClientGpuTelemetry(true);
        await monitor.SampleAsync(default); clock.Advance();
        Assert.Single((await monitor.SampleAsync(default)).Resources.Gpu!.Clients);
        source.SnapshotFailure = new InvalidOperationException("Synthetic: the process list was unavailable.");
        int opened = source.Opened.Count; clock.Advance();
        var failed = await monitor.SampleAsync(default);
        Assert.NotNull(failed.Problem); Assert.Empty(failed.Resources.Gpu!.Clients); Assert.Equal(opened, source.Opened.Count);
    }

    private static ClientProcessReading Reading(long start) => new(ClientProcessReadOutcome.Read, start, TimeSpan.FromSeconds(1),
        new(WorkingSetBytes: 700, PrivateCommitBytes: 800, PrivateWorkingSetBytes: 600, PageFaultCount: 5), new(1, 2, 3, 4, 5, 6));
    private sealed class NoWindows : ITopLevelWindows
    {
        public void Enumerate(ITopLevelWindowVisitor visitor) { }
        public bool IsUnownedAndVisible(nint window) => false;
        public bool IsVisible(nint window) => false;
        public bool IsHung(nint window) => false;
    }

    private sealed class Clock : TimeProvider
    {
        private long _seconds;
        public override DateTimeOffset GetUtcNow() => Start.AddSeconds(_seconds);
        public override long GetTimestamp() => _seconds * TimestampFrequency;
        public void Advance(int seconds = 1) => _seconds += seconds;
    }
    private sealed record Row(string Name, double Percent = 0, long Bytes = 0, uint Status = 0);
    private enum BufferFault { None, Oversize, TooManyItems, OutOfRangeName, UnterminatedName, ShortItems, AlwaysGrowing }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct Value
    { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Percent; [FieldOffset(8)] public long Bytes; }
    [StructLayout(LayoutKind.Sequential)] private struct Item { public nint Name; public Value Value; }
    private sealed class FakePdh(Clock clock) : IGpuPdhApi
    {
        public readonly List<Row> Engines = [], Dedicated = [], Shared = [], ProcessMemory = [];
        public readonly List<string> AddedPaths = [];
        public int Opens, Adds, Closes, Collections, ArrayCalls, EngineFills, ProcessMemoryReads;
        public uint EngineFormat, MaximumRequestedBuffer;
        public bool AllCountersMissing, EngineMissing, DedicatedMissing, ProcessMemoryMissing;
        public uint CollectStatus;
        public DateTimeOffset? FixedTime;
        public Action? AfterCollect, AfterAdd;
        public BufferFault Fault;
        public uint OpenQuery(out nint query) { Opens++; query = 100; return 0; }
        public uint AddEnglishCounter(nint query, string path, out nint counter)
        {
            Adds++; AddedPaths.Add(path);
            counter = path == WindowsGpuTelemetryReader.EngineCounterPath ? 1 : path == WindowsGpuTelemetryReader.DedicatedCounterPath ? 2
                : path == WindowsGpuTelemetryReader.ProcessMemoryCounterPath ? 4 : 3;
            AfterAdd?.Invoke();
            if (AllCountersMissing || (EngineMissing && counter == 1) || (DedicatedMissing && counter == 2) || (ProcessMemoryMissing && counter == 4))
            { counter = 0; return 0xc0000bb8; }
            return 0;
        }
        public uint Collect(nint query, out long fileTime)
        { Collections++; fileTime = (FixedTime ?? clock.GetUtcNow()).UtcDateTime.ToFileTimeUtc(); AfterCollect?.Invoke(); return CollectStatus; }
        public uint CloseQuery(nint query) { Closes++; return 0; }
        public uint GetFormattedArray(nint counter, uint format, ref uint bufferBytes, out uint itemCount, nint buffer)
        {
            ArrayCalls++; var rows = counter == 1 ? Engines : counter == 2 ? Dedicated : counter == 4 ? ProcessMemory : Shared;
            if (counter == 1) EngineFormat = format;
            if (counter == 4 && buffer != 0) ProcessMemoryReads++; // Fills only; the size probe is not a read.
            int stride = Marshal.SizeOf<Item>(), required = rows.Count * stride;
            foreach (var row in rows) required += (row.Name.Length + 1) * 2;
            itemCount = (uint)rows.Count;
            if (buffer == 0)
            {
                bufferBytes = (uint)required;
                if (counter == 1 && Fault == BufferFault.Oversize) bufferBytes = WindowsGpuTelemetryReader.MaximumBufferBytes + 1;
                if (counter == 1 && Fault == BufferFault.TooManyItems) itemCount = WindowsGpuTelemetryReader.MaximumCounterItems + 1;
                return required == 0 ? 0u : 0x800007d2;
            }
            MaximumRequestedBuffer = Math.Max(MaximumRequestedBuffer, bufferBytes);
            if (counter == 1) { EngineFills++; if (Fault == BufferFault.AlwaysGrowing) return 0x800007d2; }
            if (bufferBytes < required) return 0x800007d2;
            bufferBytes = (uint)required;
            int offset = rows.Count * stride;
            for (int index = 0; index < rows.Count; index++)
            {
                var row = rows[index]; nint name = buffer + offset;
                for (int character = 0; character < row.Name.Length; character++) Marshal.WriteInt16(name, character * 2, unchecked((short)row.Name[character]));
                Marshal.WriteInt16(name, row.Name.Length * 2, 0);
                if (counter == 1 && Fault == BufferFault.OutOfRangeName) name = buffer + required + 2;
                if (counter == 1 && Fault == BufferFault.UnterminatedName) Marshal.WriteInt16(name, row.Name.Length * 2, (short)'x');
                var value = new Value { Status = row.Status };
                if (counter == 1) value.Percent = row.Percent; else value.Bytes = row.Bytes;
                Marshal.StructureToPtr(new Item { Name = name, Value = value }, buffer + index * stride, false);
                offset += (row.Name.Length + 1) * 2;
            }
            if (counter == 1 && Fault == BufferFault.ShortItems) bufferBytes = 1;
            return 0;
        }
    }
}

internal static class GpuTestFormatExtensions
{
    internal static bool HasFlag(this uint value, uint flag) => (value & flag) == flag;
}
