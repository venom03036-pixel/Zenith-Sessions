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
        public readonly List<Row> Engines = [], Dedicated = [], Shared = [];
        public int Opens, Adds, Closes, Collections, ArrayCalls, EngineFills;
        public uint EngineFormat, MaximumRequestedBuffer;
        public bool AllCountersMissing, EngineMissing, DedicatedMissing;
        public uint CollectStatus;
        public DateTimeOffset? FixedTime;
        public Action? AfterCollect, AfterAdd;
        public BufferFault Fault;
        public uint OpenQuery(out nint query) { Opens++; query = 100; return 0; }
        public uint AddEnglishCounter(nint query, string path, out nint counter)
        {
            Adds++;
            counter = path == WindowsGpuTelemetryReader.EngineCounterPath ? 1 : path == WindowsGpuTelemetryReader.DedicatedCounterPath ? 2 : 3;
            AfterAdd?.Invoke();
            if (AllCountersMissing || (EngineMissing && counter == 1) || (DedicatedMissing && counter == 2)) { counter = 0; return 0xc0000bb8; } return 0;
        }
        public uint Collect(nint query, out long fileTime)
        { Collections++; fileTime = (FixedTime ?? clock.GetUtcNow()).UtcDateTime.ToFileTimeUtc(); AfterCollect?.Invoke(); return CollectStatus; }
        public uint CloseQuery(nint query) { Closes++; return 0; }
        public uint GetFormattedArray(nint counter, uint format, ref uint bufferBytes, out uint itemCount, nint buffer)
        {
            ArrayCalls++; var rows = counter == 1 ? Engines : counter == 2 ? Dedicated : Shared;
            if (counter == 1) EngineFormat = format;
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
