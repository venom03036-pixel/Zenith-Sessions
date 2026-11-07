using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Core.Tests.Storage;

public sealed class RecordingRecoveryTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "zenith-recovery-test-" + Guid.NewGuid().ToString("N"));
        public readonly Dictionary<Guid, MacroModel> Documents = new();
        public readonly HashSet<Guid> Unreadable = new();
        public readonly List<Guid> Writes = new();
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public bool FailWrites;
        public bool FailAfterSave;
        public EncryptedRecordingRecoveryStore Store { get; }
        public Fixture()
        {
            Store = new(Root, Encrypt, Decrypt,
                id => Unreadable.Contains(id) ? throw new InvalidDataException("Corrupt original retained") : Documents.GetValueOrDefault(id),
                model => { Documents[model.Id] = new MacroJsonSerializer().Deserialize(new MacroJsonSerializer().Serialize(model)); Writes.Add(model.Id);
                    if (FailAfterSave) throw new IOException("Synthetic crash after durable save"); });
        }
        public byte[] Encrypt(byte[] plain)
        {
            if (FailWrites) throw new IOException("Synthetic interrupted write");
            byte[] result = new byte[plain.Length + 28]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(result.AsSpan(0, 12), plain, result.AsSpan(28), result.AsSpan(12, 16)); return result;
        }
        public byte[] Decrypt(byte[] bytes)
        {
            byte[] plain = new byte[bytes.Length - 28]; using var aes = new AesGcm(_key, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain); return plain;
        }
        public void Edit(Guid session, string file, Action<JsonObject> edit)
        {
            string path = Path.Combine(Root, session.ToString("N"), file);
            byte[] plain = Decrypt(File.ReadAllBytes(path));
            try { var value = JsonNode.Parse(plain)!.AsObject(); edit(value); File.WriteAllBytes(path, Encrypt(JsonSerializer.SerializeToUtf8Bytes(value))); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_key);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private static MacroModel Baseline() => new() { Name = "existing", Steps = [new DelayStep(TimeSpan.FromMilliseconds(10))] };
    private static RecordingRecoveryPacket Packet(long sequence, InputEventType type, int data = 65, long epoch = 0)
        => new(sequence, sequence * 10000, type, 20, 30, data, Epoch: epoch);
    private static MacroModel SavedResult(MacroModel baseline, IRecordingRecoverySession session, long through)
        => new() { Id = baseline.Id, Name = baseline.Name, CreatedAt = baseline.CreatedAt, Settings = baseline.Settings,
            Steps = baseline.Steps.Concat(new MacroStep[] { new KeyPressStep(65, 0) { Stroke = "Down" }, new KeyPressStep(65, 0) { Stroke = "Up" } }).ToArray(),
            RecordingCommit = session.CreateCommitMetadata(through) };

    [Fact]
    public async Task FailedChunkCanBeRetriedWithoutDroppingItsAcceptedBatch()
    {
        using var fixture = new Fixture(); var baseline = Baseline();
        await using var session = await fixture.Store.BeginAsync(new(Guid.NewGuid(), baseline, 0));
        session.TryAppend(Packet(1, InputEventType.KeyDown)).Should().BeTrue();
        session.TryAppend(Packet(2, InputEventType.KeyUp)).Should().BeTrue(); fixture.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => session.CheckpointAsync());
        session.Checkpoint.Sequence.Should().Be(0);
        fixture.FailWrites = false; await session.CheckpointAsync();
        session.Checkpoint.Sequence.Should().Be(2);
        (await fixture.Store.ReadPendingAsync()).Single().Packets.Select(p => p.Sequence).Should().Equal(1, 2);
    }

    [Fact]
    public async Task MalformedChunkDoesNotAdvanceVerifiedPrefixCommitSequence()
    {
        using var fixture = new Fixture(); Guid id = Guid.NewGuid();
        await using (var session = await fixture.Store.BeginAsync(new(id, Baseline(), 0)))
        {
            session.TryAppend(Packet(1, InputEventType.KeyDown)); session.TryAppend(Packet(2, InputEventType.KeyUp)); await session.CheckpointAsync();
            session.TryAppend(Packet(3, InputEventType.KeyDown)); session.TryAppend(Packet(4, InputEventType.KeyUp)); await session.CheckpointAsync();
        }
        fixture.Edit(id, "00000001.chunk", value => value["packets"]![1]!["sequence"] = 99);
        string directory = Path.Combine(fixture.Root, id.ToString("N"));
        File.WriteAllBytes(Path.Combine(directory, "commit.zrec"), fixture.Encrypt(JsonSerializer.SerializeToUtf8Bytes(new
            { version = 1, sessionId = id, sequence = 3, resultFingerprint = new string('A', 64) })));
        var candidate = (await fixture.Store.ReadPendingAsync()).Single();
        candidate.Packets.Select(p => p.Sequence).Should().Equal(1, 2);
        candidate.ExpectedResultFingerprint.Should().BeNull(); candidate.IsCompletePrefix.Should().BeFalse();
        File.Exists(Path.Combine(directory, "00000001.chunk")).Should().BeTrue();
    }

    [Fact]
    public async Task CrashAfterSaveFollowedByEditingDoesNotAppendRecordingAgain()
    {
        using var fixture = new Fixture(); var baseline = Baseline(); fixture.Documents[baseline.Id] = baseline;
        await using (var session = await fixture.Store.BeginAsync(new(Guid.NewGuid(), baseline, 0)))
        {
            session.TryAppend(Packet(1, InputEventType.KeyDown)); session.TryAppend(Packet(2, InputEventType.KeyUp));
            var saved = SavedResult(baseline, session, 2); await session.PrepareCommitAsync(saved);
            // Simulate durable document save followed by a crash before journal cleanup, then an ordinary edit.
            fixture.Documents[baseline.Id] = new() { Id = saved.Id, Name = "edited later", Steps = saved.Steps.Concat([new DelayStep(TimeSpan.FromMilliseconds(30))]).ToArray(),
                Settings = saved.Settings, RecordingCommit = saved.RecordingCommit };
        }
        var pending = (await fixture.Store.ReadPendingAsync()).Single();
        (await fixture.Store.RecoverAsync(pending)).Should().BeNull(); fixture.Writes.Should().BeEmpty();
        fixture.Documents[baseline.Id].Name.Should().Be("edited later");
        (await fixture.Store.ReadPendingAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CorruptOriginalIsPreservedAndRecoveryUsesSeparateCopy()
    {
        using var fixture = new Fixture(); var baseline = Baseline(); baseline.Settings.Hotkey = "Ctrl+9"; fixture.Documents[baseline.Id] = baseline;
        Guid id = Guid.NewGuid();
        await using (var session = await fixture.Store.BeginAsync(new(id, baseline, 0)))
        { session.TryAppend(Packet(1, InputEventType.KeyDown)); session.TryAppend(Packet(2, InputEventType.KeyUp)); await session.CheckpointAsync(); }
        fixture.Unreadable.Add(baseline.Id);
        var recovered = await fixture.Store.RecoverAsync((await fixture.Store.ReadPendingAsync()).Single());
        recovered!.Id.Should().Be(id); fixture.Writes.Should().Equal(id);
        recovered.Settings.Hotkey.Should().BeEmpty(); baseline.Settings.Hotkey.Should().Be("Ctrl+9");
        fixture.Documents[baseline.Id].Should().BeSameAs(baseline);
    }

    [Fact]
    public async Task EpochBoundariesBalanceHoldsAndPreserveUnicode()
    {
        using var fixture = new Fixture(); var baseline = Baseline();
        await using (var session = await fixture.Store.BeginAsync(new(Guid.NewGuid(), baseline, 0)))
        {
            session.TryAppend(Packet(1, InputEventType.KeyDown)); session.TryAppend(Packet(2, InputEventType.Unicode, 'é'));
            session.TryAppend(Packet(3, InputEventType.MouseMove, 0, 1));
            session.TryAppend(Packet(4, InputEventType.KeyUp, 65, 1)); session.TryAppend(Packet(5, InputEventType.UnicodeUp, 'é', 1));
            await session.CheckpointAsync();
        }
        var model = await fixture.Store.RecoverAsync((await fixture.Store.ReadPendingAsync()).Single());
        var keys = model!.Steps.OfType<KeyPressStep>().ToArray();
        keys.Where(k => !k.IsUnicode).Select(k => k.Stroke).Should().Equal("Down", "Up");
        keys.Where(k => k.IsUnicode).Select(k => k.Stroke).Should().Equal("Down", "Up");
        keys.Where(k => k.IsUnicode).Should().OnlyContain(k => k.KeyCode == 'é');
        var roundTrip = new MacroJsonSerializer().Deserialize(new MacroJsonSerializer().Serialize(model));
        roundTrip.Steps.OfType<KeyPressStep>().Count(k => k.IsUnicode).Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentDisposalIsOneDrainAndDoesNotRaceWorkerResources()
    {
        using var fixture = new Fixture(); var session = await fixture.Store.BeginAsync(new(Guid.NewGuid(), Baseline(), 0));
        for (int i = 1; i <= 50; i++) session.TryAppend(Packet(i, i % 2 == 1 ? InputEventType.KeyDown : InputEventType.KeyUp));
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
        (await fixture.Store.ReadPendingAsync()).Single().Packets.Count.Should().Be(50);
    }

    [Fact]
    public async Task VerifiedOldJournalsAreCleanedBeforeHistoryLimitWithoutLosingUnmatchedJournals()
    {
        using var fixture = new Fixture(); var baseline = Baseline(); fixture.Documents[baseline.Id] = baseline;
        for (int i = 0; i < 103; i++)
        {
            await using var session = await fixture.Store.BeginAsync(new(Guid.NewGuid(), baseline, 0));
            session.TryAppend(Packet(1, InputEventType.KeyDown)); session.TryAppend(Packet(2, InputEventType.KeyUp));
            var saved = SavedResult(baseline, session, 2); await session.PrepareCommitAsync(saved);
            fixture.Documents[saved.Id] = saved; baseline = saved;
        }
        // Each new Begin verified and removed the preceding committed journal; metadata stayed bounded.
        baseline.RecordingCommit!.Previous.Should().BeNull();
        (await fixture.Store.ReadPendingAsync()).Count.Should().Be(1);
    }

    [Fact]
    public async Task RelativeCameraRecoveryPreservesPrecisePacketOrderAndAuthenticatedGains()
    {
        using var fixture = new Fixture(); var baseline = Baseline();
        string settings = JsonSerializer.Serialize(new { CameraPlaybackHz = 250, CameraLookGainX = 2.5, CameraLookGainY = 3.5,
            CameraPlaybackMode = TrajectoryPlaybackMode.PacketReference });
        await using (var session = await fixture.Store.BeginAsync(new(Guid.NewGuid(), baseline, 0, SettingsSnapshot: settings)))
        {
            session.TryAppend(new(1, 10000, InputEventType.MouseMoveRelative, 3, -2, 0));
            session.TryAppend(new(2, 10001, InputEventType.MouseMoveRelative, -3, 2, 0));
            await session.CheckpointAsync();
        }
        var recovered = await fixture.Store.RecoverAsync((await fixture.Store.ReadPendingAsync()).Single());
        var path = recovered!.Steps.OfType<MouseTrajectoryStep>().Single();
        path.PlaybackHz.Should().Be(250); path.Sensitivity.Should().Be(2.5); path.SensitivityY.Should().Be(3.5);
        path.Mode.Should().Be(TrajectoryPlaybackMode.PacketReference); path.TimestampIsStart.Should().BeTrue();
        path.RelativeTimestamp.Ticks.Should().Be(10000);
        path.Packets!.Select(p => p.OffsetTicks).Should().Equal(0, 1);
        path.Packets!.Select(p => (p.Dx, p.Dy)).Should().Equal((3, -2), (-3, 2));
    }
    [Fact]
    public async Task SessionBaselineIsDetachedFromLaterMutableSettingsEdits()
    {
        using var fixture = new Fixture(); var baseline = Baseline();
        await using var session = await fixture.Store.BeginAsync(new(Guid.NewGuid(), baseline, 0));
        baseline.Settings.RepeatCount = 99;
        session.TryAppend(Packet(1, InputEventType.KeyDown)); session.TryAppend(Packet(2, InputEventType.KeyUp));
        await session.CheckpointAsync();
        var pending = (await fixture.Store.ReadPendingAsync()).Single();
        pending.Start!.Baseline.Settings.RepeatCount.Should().Be(1);
        session.CreateCommitMetadata(2).BaselineFingerprint.Should().Be(EncryptedRecordingRecoveryStore.Fingerprint(pending.Start.Baseline));
    }
    [Fact]
    public async Task ConcurrentRecoverSavesOneCopyExactlyOnce()
    {
        using var fixture = new Fixture();
        await using (var session = await fixture.Store.BeginAsync(new(Guid.NewGuid(), Baseline(), 0)))
        { session.TryAppend(Packet(1, InputEventType.KeyDown)); session.TryAppend(Packet(2, InputEventType.KeyUp)); await session.CheckpointAsync(); }
        var pending = (await fixture.Store.ReadPendingAsync()).Single();
        await Task.WhenAll(fixture.Store.RecoverAsync(pending), fixture.Store.RecoverAsync(pending));
        fixture.Writes.Count.Should().Be(1);
    }
    [Fact]
    public async Task CancelledEmptyStartupCanAbortOnlyItsOwnVerifiedJournal()
    {
        using var fixture = new Fixture(); var baseline = Baseline();
        var empty = await fixture.Store.BeginAsync(new(Guid.NewGuid(), baseline, 0));
        (await empty.AbortIfEmptyAsync()).Should().BeTrue();
        await empty.DisposeAsync();
        (await fixture.Store.ReadPendingAsync()).Should().BeEmpty();
        await using var accepted = await fixture.Store.BeginAsync(new(Guid.NewGuid(), baseline, 0));
        accepted.TryAppend(Packet(1, InputEventType.KeyDown));
        (await accepted.AbortIfEmptyAsync()).Should().BeFalse(); await accepted.CheckpointAsync();
        (await fixture.Store.ReadPendingAsync()).Single().Packets.Count.Should().Be(1);
    }
    [Fact]
    public async Task CapturedCommitProtectsSavedRamTailWhenCheckpointNeverSucceeded()
    {
        using var fixture = new Fixture(); var baseline = Baseline(); fixture.Documents[baseline.Id] = baseline;
        var session = await fixture.Store.BeginAsync(new(Guid.NewGuid(), baseline, 0));
        session.TryAppend(Packet(1, InputEventType.KeyDown)); session.TryAppend(Packet(2, InputEventType.KeyUp));
        fixture.FailWrites = true; await Assert.ThrowsAsync<IOException>(() => session.CheckpointAsync());
        var result = new MacroModel { Id = baseline.Id, Name = baseline.Name, Settings = baseline.Settings,
            Steps = baseline.Steps.Concat(Enumerable.Range(0, 4).Select(i => new KeyPressStep(65 + i, 0))).ToArray(),
            RecordingCommit = session.CreateCapturedCommitMetadata(4) };
        await Assert.ThrowsAsync<IOException>(() => session.PrepareCommitAsync(result));
        fixture.Documents[result.Id] = result; // Normal protected document save succeeds independently of the journal.
        await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask()); fixture.FailWrites = false;
        var pending = (await fixture.Store.ReadPendingAsync()).Single();
        (await fixture.Store.RecoverAsync(pending)).Should().BeNull();
        fixture.Documents[result.Id].Steps.Count.Should().Be(5); fixture.Writes.Should().BeEmpty();
    }
    [Fact]
    public async Task SavedConflictCopyIsRecognizedEvenAfterOriginalIsRestoredToBaseline()
    {
        using var fixture = new Fixture(); var baseline = Baseline(); Guid id = Guid.NewGuid();
        await using (var session = await fixture.Store.BeginAsync(new(id, baseline, 0)))
        { session.TryAppend(Packet(1, InputEventType.KeyDown)); session.TryAppend(Packet(2, InputEventType.KeyUp)); await session.CheckpointAsync(); }
        var pending = (await fixture.Store.ReadPendingAsync()).Single(); fixture.FailAfterSave = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.RecoverAsync(pending)); fixture.FailAfterSave = false;
        var copy = fixture.Documents[id];
        fixture.Documents[id] = new() { Id = copy.Id, Name = "copy edited later", Settings = copy.Settings, Steps = copy.Steps, RecordingCommit = copy.RecordingCommit };
        fixture.Documents[baseline.Id] = baseline;
        var recognized = await fixture.Store.RecoverAsync(pending);
        recognized!.Name.Should().Be("copy edited later"); fixture.Writes.Should().Equal(id);
        fixture.Documents[baseline.Id].Steps.Count.Should().Be(1);
    }
}
