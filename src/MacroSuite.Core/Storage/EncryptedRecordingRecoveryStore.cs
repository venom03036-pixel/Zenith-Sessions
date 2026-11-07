using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Execution;

namespace MacroSuite.Core.Storage;

/// <summary>Bounded, authenticated recording journal. All encryption and I/O belong to its worker.</summary>
public sealed class EncryptedRecordingRecoveryStore : IRecordingRecoveryStore
{
    public const int MaximumChunkBytes = 512 * 1024;
    public const long MaximumSessionBytes = 128L * 1024 * 1024;
    public const long MaximumPendingBytes = 512L * 1024 * 1024;
    private readonly string _root;
    private readonly Func<byte[], byte[]> _encrypt, _decrypt;
    private readonly Func<Guid, MacroModel?> _load;
    private readonly Action<MacroModel> _save;
    private readonly Func<MacroModel, MacroModel> _prepare;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _recoveryGate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Manifest(int Version, Guid SessionId, string Baseline, string Fingerprint,
        long AppendOffsetTicks, string ConverterId, string? SettingsSnapshot);
    private sealed record Chunk(int Version, Guid SessionId, int Index, string PreviousHash,
        RecordingRecoveryPacket[] Packets);
    private sealed record Commit(int Version, Guid SessionId, long Sequence, string ResultFingerprint);

    public EncryptedRecordingRecoveryStore(string root, Func<byte[], byte[]> encrypt, Func<byte[], byte[]> decrypt,
        Func<Guid, MacroModel?> load, Action<MacroModel> save, Func<MacroModel, MacroModel>? prepare = null)
    {
        _root = Path.GetFullPath(root); _encrypt = encrypt; _decrypt = decrypt; _load = load; _save = save;
        _prepare = prepare ?? (model => model);
    }

    public Task<IRecordingRecoverySession> BeginAsync(RecordingRecoveryStart start, CancellationToken ct = default)
        => Task.Run<IRecordingRecoverySession>(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (start.SessionId == Guid.Empty || start.AppendOffsetTicks < 0 || start.ConverterId != "input-v1")
                throw new InvalidDataException("The recording recovery session is invalid.");
            _ = ParseCameraSettings(start.SettingsSnapshot);
            MacroModel baseline = _prepare(start.Baseline);
            CleanupCommittedSessions(baseline);
            if (CountMarkers(PruneAbsentMarkers(baseline.RecordingCommit)) >= RecordingCommitMetadata.MaximumHistory)
                throw new IOException("The recording recovery history is full. Review its preserved journals before starting another recovery session.");
            string json = new MacroJsonSerializer().Serialize(baseline);
            // UI edits after Begin must not alter the authenticated baseline held by this session.
            var snapshot = start with { Baseline = new MacroJsonSerializer().Deserialize(json) };
            string directory = SessionPath(start.SessionId);
            if (Directory.Exists(directory)) throw new IOException("This recording recovery session already exists.");
            Write(directory, "manifest.zrec", new Manifest(1, start.SessionId, json, Fingerprint(snapshot.Baseline),
                start.AppendOffsetTicks, start.ConverterId, start.SettingsSnapshot), 100 * 1024 * 1024);
            return new Session(this, snapshot, directory);
        }, ct);

    public Task<IReadOnlyList<RecordingRecoveryCandidate>> ReadPendingAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<RecordingRecoveryCandidate>>(() =>
        {
            var result = new List<RecordingRecoveryCandidate>();
            if (!Directory.Exists(_root)) return result;
            foreach (string directory in Directory.EnumerateDirectories(_root).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id)) continue;
                try { result.Add(Read(id, ct)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or JsonException or InvalidOperationException)
                { result.Add(new(id, null, Array.Empty<RecordingRecoveryPacket>(), null, ex.Message, false)); }
                if (result.Count >= 1024) break;
            }
            return result;
        }, ct);

    private RecordingRecoveryCandidate Read(Guid id, CancellationToken ct)
    {
        string directory = SessionPath(id);
        Manifest manifest = ReadFile<Manifest>(Path.Combine(directory, "manifest.zrec"), 100 * 1024 * 1024);
        if (manifest.Version != 1 || manifest.SessionId != id || manifest.ConverterId != "input-v1")
            throw new InvalidDataException("This recording recovery format is unsupported; its files were preserved.");
        MacroModel baseline = new MacroJsonSerializer().Deserialize(manifest.Baseline);
        if (manifest.Fingerprint != Fingerprint(baseline)) throw new InvalidDataException("The recovery baseline does not verify.");
        var packets = new List<RecordingRecoveryPacket>(); string previous = ""; int index = 0;
        long last = 0, previousTicks = manifest.AppendOffsetTicks, decodedTotal = 0;
        string? problem = null;
        foreach (string file in Directory.EnumerateFiles(directory, "*.chunk").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Path.GetFileName(file) != $"{index:D8}.chunk") throw new InvalidDataException("A recording recovery chunk is missing.");
                Chunk chunk = ReadFile<Chunk>(file, MaximumChunkBytes);
                if (chunk.Version != 1 || chunk.SessionId != id || chunk.Index != index || chunk.PreviousHash != previous
                    || chunk.Packets is null || chunk.Packets.Length is < 1 or > 2048)
                    throw new InvalidDataException("The recording recovery chain does not verify.");
                long chunkLast = last, chunkTicks = previousTicks;
                foreach (var packet in chunk.Packets)
                {
                    if (packet.Sequence != chunkLast + 1 || packet.TimelineTicks < chunkTicks || !Enum.IsDefined(packet.Type)
                        || packet.ClientWidth is < 0 or > 1_000_000 || packet.ClientHeight is < 0 or > 1_000_000 || packet.Epoch < 0)
                        throw new InvalidDataException("The recording recovery event sequence is invalid.");
                    chunkLast = packet.Sequence; chunkTicks = packet.TimelineTicks;
                }
                long nextTotal = decodedTotal + chunk.Packets.Length * 256L;
                if (nextTotal > MaximumSessionBytes || index >= 4096) throw new InvalidDataException("Recording recovery exceeds its supported size.");
                string hash = HashFile(file);
                packets.AddRange(chunk.Packets); previous = hash; index++;
                last = chunkLast; previousTicks = chunkTicks; decodedTotal = nextTotal;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or JsonException)
            { problem = "Only the verified recording prefix can be recovered. " + ex.Message; break; }
        }
        string? expected = null;
        string commitPath = Path.Combine(directory, "commit.zrec");
        if (File.Exists(commitPath))
        {
            try
            {
                Commit commit = ReadFile<Commit>(commitPath, MaximumChunkBytes);
                if (commit.Version != 1 || commit.SessionId != id || commit.Sequence != last)
                    throw new InvalidDataException("The recording commit does not match its verified prefix.");
                expected = commit.ResultFingerprint;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or JsonException)
            { problem ??= ex.Message; }
        }
        return new(id, new(id, baseline, manifest.AppendOffsetTicks, manifest.ConverterId, manifest.SettingsSnapshot),
            packets, expected, problem, problem is null);
    }

    public Task<MacroModel?> RecoverAsync(RecordingRecoveryCandidate candidate, CancellationToken ct = default)
        => Task.Run(() =>
        {
            lock (_recoveryGate)
            {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(SessionPath(candidate.SessionId))) return null;
            // Read again: callers cannot substitute unauthenticated packet data.
            candidate = Read(candidate.SessionId, ct);
            var start = candidate.Start ?? throw new InvalidDataException("The recording baseline is unavailable.");
            // A conflict copy may already have been saved before a crash, even if somebody
            // subsequently restores the original document to its old baseline revision.
            MacroModel? savedCopy = candidate.SessionId == start.Baseline.Id ? null : _load(candidate.SessionId);
            long verifiedSequence = candidate.Packets.LastOrDefault()?.Sequence ?? 0;
            if (savedCopy != null && (savedCopy.RecordingCommit?.Contains(candidate.SessionId, Fingerprint(start.Baseline), verifiedSequence) == true
                || candidate.ExpectedResultFingerprint is { } savedCopyFingerprint && Fingerprint(savedCopy) == savedCopyFingerprint))
            { if (candidate.IsCompletePrefix) Cleanup(candidate.SessionId); return savedCopy; }
            MacroModel? current;
            try { current = _load(start.Baseline.Id); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            { current = null; } // Preserve the unreadable original; recover into a separate deterministic copy.
            if (current?.RecordingCommit?.Contains(candidate.SessionId, Fingerprint(start.Baseline), candidate.Packets.LastOrDefault()?.Sequence ?? 0) == true)
            { if (candidate.IsCompletePrefix) Cleanup(candidate.SessionId); return null; }
            if (candidate.ExpectedResultFingerprint is { } expected && current != null && Fingerprint(current) == expected)
            { Cleanup(candidate.SessionId); return null; }
            if (candidate.Packets.Count == 0)
            { if (candidate.IsCompletePrefix && candidate.ExpectedResultFingerprint == null) Cleanup(candidate.SessionId); return null; }
            bool same = current != null && Fingerprint(current) == Fingerprint(start.Baseline);
            // A deterministic recovered-copy ID makes a crash after its save idempotent too.
            Guid destination = same ? start.Baseline.Id : candidate.SessionId;
            var steps = start.Baseline.Steps.Concat(ConvertPackets(candidate.Packets, start.SettingsSnapshot)).ToArray();
            MacroSettings settings = same ? start.Baseline.Settings
                : new MacroJsonSerializer().Deserialize(new MacroJsonSerializer().Serialize(start.Baseline)).Settings;
            if (!same) settings.Hotkey = string.Empty;
            var result = new MacroModel
            {
                Id = destination, Name = same ? start.Baseline.Name : start.Baseline.Name + " (recovered recording)",
                Description = start.Baseline.Description, CreatedAt = start.Baseline.CreatedAt,
                IsFavorite = start.Baseline.IsFavorite, Settings = settings, Steps = steps,
                RecordingCommit = new RecordingCommitMetadata(candidate.SessionId, Fingerprint(start.Baseline), candidate.Packets[^1].Sequence,
                    PruneAbsentMarkers(start.Baseline.RecordingCommit))
            };
            string fingerprint = Fingerprint(result);
            MacroModel? existing = _load(destination);
            if (existing?.RecordingCommit?.Contains(candidate.SessionId, Fingerprint(start.Baseline), candidate.Packets[^1].Sequence) == true)
            { if (candidate.IsCompletePrefix) Cleanup(candidate.SessionId); return existing; }
            if (existing != null && Fingerprint(existing) == fingerprint)
            { if (candidate.IsCompletePrefix) Cleanup(candidate.SessionId); return existing; }
            if (same && (existing == null || Fingerprint(existing) != Fingerprint(start.Baseline)))
                throw new IOException("The original macro changed during recovery. It was preserved; retry recovery to create a separate copy.");
            if (!same && existing != null)
                throw new IOException("A different macro already occupies the recovery copy identity. Both it and the recording journal were preserved.");
            Write(SessionPath(candidate.SessionId), "commit.zrec", new Commit(1, candidate.SessionId,
                candidate.Packets[^1].Sequence, fingerprint), MaximumChunkBytes);
            _save(result);
            if (_load(destination) is not { } saved || Fingerprint(saved) != fingerprint)
                throw new IOException("The recovered recording could not be verified. Its journal was preserved.");
            if (candidate.IsCompletePrefix) Cleanup(candidate.SessionId);
            return result;
            }
        }, ct);

    public static string Fingerprint(MacroModel model)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new MacroJsonSerializer().Serialize(model));
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed class RecoveryCameraSettings
    {
        public int CameraPlaybackHz { get; set; } = MouseTrajectoryPlayback.DefaultHz;
        public double CameraLookGainX { get; set; } = 1;
        public double CameraLookGainY { get; set; } = 1;
        public TrajectoryPlaybackMode CameraPlaybackMode { get; set; } = TrajectoryPlaybackMode.PacketReference;
    }
    private static RecoveryCameraSettings ParseCameraSettings(string? json)
    {
        if (json == null) return new();
        if (json.Length > 65536) throw new InvalidDataException("The recording settings snapshot exceeds its size limit.");
        var settings = JsonSerializer.Deserialize<RecoveryCameraSettings>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("The recording settings snapshot is empty.");
        if (!Enum.IsDefined(settings.CameraPlaybackMode) || settings.CameraPlaybackHz is < 1 or > 1000
            || !double.IsFinite(settings.CameraLookGainX) || !double.IsFinite(settings.CameraLookGainY)
            || settings.CameraLookGainX is < .25 or > 10 || settings.CameraLookGainY is < .25 or > 10)
            throw new InvalidDataException("The recording camera settings are invalid.");
        return settings;
    }
    private static IEnumerable<MacroStep> ConvertPackets(IEnumerable<RecordingRecoveryPacket> packets, string? settingsSnapshot)
    {
        var camera = ParseCameraSettings(settingsSnapshot);
        var held = new Dictionary<(InputEventType, int), RecordingRecoveryPacket>();
        var relative = new List<RecordingRecoveryPacket>();
        long? epoch = null; long lastTime = 0;
        foreach (var p in packets)
        {
            if (epoch != null && epoch != p.Epoch)
            {
                if (relative.Count > 0) { yield return BuildPath(relative, camera); relative.Clear(); }
                foreach (var hold in held.Values) if (ConvertPacket(Release(hold, lastTime)) is { } release) yield return release;
                held.Clear();
            }
            epoch = p.Epoch; lastTime = p.TimelineTicks;
            if (p.Type == InputEventType.MouseMoveRelative)
            {
                relative.Add(p);
                if (relative.Count >= 4096) { yield return BuildPath(relative, camera); relative.Clear(); }
                continue;
            }
            if (relative.Count > 0) { yield return BuildPath(relative, camera); relative.Clear(); }
            var kind = p.Type switch { InputEventType.KeyUp => InputEventType.KeyDown, InputEventType.MouseUp => InputEventType.MouseDown,
                InputEventType.UnicodeUp => InputEventType.Unicode, _ => p.Type };
            if (kind is InputEventType.KeyDown or InputEventType.MouseDown or InputEventType.Unicode)
            {
                var identity = (kind, p.Data); bool down = p.Type == kind;
                if (down) held[identity] = p;
                else if (!held.Remove(identity)) continue;
            }
            if (ConvertPacket(p) is { } step) yield return step;
        }
        if (relative.Count > 0) yield return BuildPath(relative, camera);
        foreach (var hold in held.Values) if (ConvertPacket(Release(hold, lastTime)) is { } release) yield return release;

        static RecordingRecoveryPacket Release(RecordingRecoveryPacket p, long ticks) => p with { TimelineTicks = ticks,
            Type = p.Type switch { InputEventType.KeyDown => InputEventType.KeyUp, InputEventType.MouseDown => InputEventType.MouseUp, _ => InputEventType.UnicodeUp } };
        static MacroStep? ConvertPacket(RecordingRecoveryPacket p)
        {
            TimeSpan time = TimeSpan.FromTicks(p.TimelineTicks);
            MacroStep? step = p.Type switch
            {
                InputEventType.KeyDown or InputEventType.KeyUp or InputEventType.Unicode or InputEventType.UnicodeUp => new KeyPressStep(p.Data, 0)
                    { Stroke = p.Type is InputEventType.KeyDown or InputEventType.Unicode ? "Down" : "Up", RelativeTimestamp = time,
                        IsUnicode = p.Type is InputEventType.Unicode or InputEventType.UnicodeUp },
                InputEventType.MouseDown or InputEventType.MouseUp => new ClickStep(new(p.X, p.Y), (MouseButton)p.Data, 1, 0)
                    { Stroke = p.Type == InputEventType.MouseDown ? "Down" : "Up", RelativeTimestamp = time,
                        IsClientRelative = p.IsClientRelative, RecordedClientWidth = p.ClientWidth, RecordedClientHeight = p.ClientHeight },
                InputEventType.MouseMove or InputEventType.MouseMoveRelative => new MouseMoveStep(new(p.X, p.Y), p.IsClientRelative, time)
                    { IsRelative = p.Type == InputEventType.MouseMoveRelative, RecordedClientWidth = p.ClientWidth, RecordedClientHeight = p.ClientHeight },
                InputEventType.MouseWheel => new MouseWheelStep(p.Data, 0, time),
                InputEventType.MouseHWheel => new MouseWheelStep(0, p.Data, time),
                _ => null
            };
            return step;
        }
    }

    private static MouseTrajectoryStep BuildPath(List<RecordingRecoveryPacket> source, RecoveryCameraSettings settings)
    {
        long start = source[0].TimelineTicks; int x = 0, y = 0;
        var samples = new List<MouseTrajectorySample>(source.Count + 1) { new(0, 0, 0) };
        var packets = new MouseTrajectoryPacket[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            var packet = source[i]; long offset = packet.TimelineTicks - start;
            if (offset is < 0 or > TimeSpan.TicksPerDay) throw new InvalidDataException("The recording camera path duration is invalid.");
            int milliseconds = checked((int)(offset / TimeSpan.TicksPerMillisecond));
            x = checked(x + packet.X); y = checked(y + packet.Y);
            samples.Add(new(milliseconds, x, y));
            packets[i] = new(milliseconds, packet.X, packet.Y, i) { OffsetTicks = offset };
        }
        var normalized = MouseTrajectoryPlayback.Normalize(samples);
        if (normalized.Length < 2) normalized = [new(0, 0, 0), normalized.Length == 0 ? new(0, 0, 0) : normalized[0]];
        return new(normalized, TimeSpan.FromTicks(start)) { TimestampIsStart = true, Packets = packets,
            PlaybackHz = MouseTrajectoryPlayback.ClampHz(settings.CameraPlaybackHz), Sensitivity = settings.CameraLookGainX,
            SensitivityY = settings.CameraLookGainY, Mode = settings.CameraPlaybackMode, GapPolicy = TrajectoryGapPolicy.PreservePause };
    }

    private string SessionPath(Guid id) => Path.Combine(_root, id.ToString("N"));
    private static int CountMarkers(RecordingCommitMetadata? value)
    { int count = 0; for (; value != null; value = value.Previous) count++; return count; }
    private RecordingCommitMetadata? PruneAbsentMarkers(RecordingCommitMetadata? value)
    {
        var retained = new List<RecordingCommitMetadata>();
        for (var item = value; item != null; item = item.Previous)
            if (Directory.Exists(SessionPath(item.SessionId))) retained.Add(item);
        RecordingCommitMetadata? result = null;
        for (int i = retained.Count - 1; i >= 0; i--) result = retained[i] with { Previous = result };
        return result;
    }
    private void CleanupCommittedSessions(MacroModel baseline)
    {
        MacroModel? saved = _load(baseline.Id);
        if (saved?.RecordingCommit == null) return;
        for (var marker = saved.RecordingCommit; marker != null; marker = marker.Previous)
        {
            if (!Directory.Exists(SessionPath(marker.SessionId))) continue;
            try
            {
                var candidate = Read(marker.SessionId, CancellationToken.None);
                if (candidate.Start != null && candidate.IsCompletePrefix
                    && saved.RecordingCommit.Contains(marker.SessionId, Fingerprint(candidate.Start.Baseline), candidate.Packets.LastOrDefault()?.Sequence ?? 0))
                    Cleanup(marker.SessionId);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or JsonException or InvalidOperationException)
            { /* An unverified journal is retained together with its commit marker. */ }
        }
    }
    private static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private T ReadFile<T>(string path, int max)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 1 || file.Length > max + 65536L) throw new InvalidDataException("The recovery file size is invalid.");
        byte[] encrypted = new byte[checked((int)file.Length)]; file.ReadExactly(encrypted);
        byte[] plain = _decrypt(encrypted);
        try
        {
            if (plain.Length > max) throw new InvalidDataException("The decoded recovery file exceeds its size limit.");
            return JsonSerializer.Deserialize<T>(plain, Json) ?? throw new InvalidDataException("The recovery file is empty.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void Write<T>(string directory, string name, T value, int maximum)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            if (plain.Length > maximum) throw new IOException("Recording recovery reached its decoded size limit.");
            byte[] encrypted = _encrypt(plain);
            _io.Wait();
            try
            {
                long sessionBytes = Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Sum(p => new FileInfo(p).Length) : 0;
                long total = Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) : 0;
                if (sessionBytes + encrypted.Length > MaximumSessionBytes || total + encrypted.Length > MaximumPendingBytes)
                    throw new IOException("Recording recovery storage is full. Recover pending recordings before continuing.");
                Directory.CreateDirectory(directory);
                string destination = Path.Combine(directory, name), temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { stream.Write(encrypted); stream.Flush(true); }
                    File.Move(temporary, destination, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            finally { _io.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void Cleanup(Guid id)
    {
        string directory = SessionPath(id);
        // Only our exact session directory; callers cannot supply paths. No recursive deletion of unknown material.
        if (!Directory.Exists(directory)) return;
        foreach (string file in Directory.EnumerateFiles(directory))
            if (Path.GetFileName(file) is "manifest.zrec" or "commit.zrec" || Path.GetExtension(file) == ".chunk") File.Delete(file);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private sealed class Session : IRecordingRecoverySession
    {
        private readonly EncryptedRecordingRecoveryStore _owner;
        private readonly RecordingRecoveryStart _start;
        private readonly string _directory;
        private readonly object _sync = new();
        private readonly Queue<RecordingRecoveryPacket[]> _queue = new();
        private List<RecordingRecoveryPacket> _current = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _worker;
        private TaskCompletionSource _changed = NewSignal();
        private RecordingRecoveryCheckpoint _checkpoint = new(0, null, null);
        private long _accepted, _lastTicks;
        private string _previous = "";
        private int _index;
        private bool _closing, _disposed;
        private Task? _disposeTask;
        private RecordingRecoveryPacket[]? _failedBatch;
        private MacroModel? _expected;
        public Guid SessionId => _start.SessionId;
        public RecordingCommitMetadata CreateCommitMetadata(long throughSequence)
        {
            if (throughSequence < 0 || throughSequence > Interlocked.Read(ref _accepted)) throw new ArgumentOutOfRangeException(nameof(throughSequence));
            var result = new RecordingCommitMetadata(SessionId, Fingerprint(_start.Baseline), throughSequence,
                _owner.PruneAbsentMarkers(_start.Baseline.RecordingCommit));
            result.Validate(); return result;
        }
        public RecordingCommitMetadata CreateCapturedCommitMetadata(long capturedThroughSequence)
        {
            lock (_sync)
            {
                if (capturedThroughSequence < _accepted || capturedThroughSequence is < 0 or > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(capturedThroughSequence));
                _closing = true; Seal(); _signal.Release();
                var result = new RecordingCommitMetadata(SessionId, Fingerprint(_start.Baseline), capturedThroughSequence,
                    _owner.PruneAbsentMarkers(_start.Baseline.RecordingCommit));
                result.Validate(); return result;
            }
        }
        public async Task<bool> AbortIfEmptyAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync) { if (_accepted != 0) return false; _closing = true; }
            await DisposeAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                lock (_owner._recoveryGate)
                {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(_directory)) return true;
                var candidate = _owner.Read(SessionId, ct);
                if (!candidate.IsCompletePrefix || candidate.Packets.Count != 0 || candidate.ExpectedResultFingerprint != null) return false;
                _owner.Cleanup(SessionId); return true;
                }
            }, ct).ConfigureAwait(false);
        }
        public RecordingRecoveryCheckpoint Checkpoint { get { lock (_sync) return _checkpoint; } }
        public Session(EncryptedRecordingRecoveryStore owner, RecordingRecoveryStart start, string directory)
        { _owner = owner; _start = start; _directory = directory; _lastTicks = start.AppendOffsetTicks; _worker = Task.Run(WriteLoopAsync); }
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Pulse() { var changed = _changed; _changed = NewSignal(); changed.TrySetResult(); }
        public bool TryAppend(RecordingRecoveryPacket packet)
        {
            lock (_sync)
            {
                if (_closing || _disposed || _checkpoint.Error != null) return false;
                if (packet.Sequence != _accepted + 1 || packet.TimelineTicks < _lastTicks)
                    throw new InvalidOperationException("Recovery input must have a contiguous sequence and monotonic timeline.");
                if (_current.Count >= 1024 && _queue.Count >= 4) return false;
                _current.Add(packet); _accepted = packet.Sequence; _lastTicks = packet.TimelineTicks;
                if (_current.Count >= 1024 && _queue.Count < 4) Seal();
                return true;
            }
        }
        private void Seal()
        {
            if (_current.Count == 0 || _queue.Count >= 4) return;
            _queue.Enqueue(_current.ToArray()); _current = new(); _signal.Release();
        }
        public async Task CheckpointAsync(CancellationToken ct = default)
        {
            long until;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                until = _accepted; _checkpoint = _checkpoint with { Error = null }; Seal(); _signal.Release();
            }
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_checkpoint.Error != null) throw new IOException(_checkpoint.Error);
                    if (_checkpoint.Sequence >= until) return;
                    Seal(); wait = _changed.Task;
                }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        private async Task WriteLoopAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(1), _shutdown.Token).ConfigureAwait(false);
                    RecordingRecoveryPacket[]? batch;
                    lock (_sync)
                    {
                        if (_checkpoint.Error != null) continue;
                        Seal(); batch = _failedBatch ?? (_queue.Count > 0 ? _queue.Dequeue() : null);
                        if (batch == null && _closing) return;
                    }
                    if (batch == null) continue;
                    try
                    {
                        string filename = $"{_index:D8}.chunk";
                        _owner.Write(_directory, filename, new Chunk(1, SessionId, _index, _previous, batch), MaximumChunkBytes);
                        _previous = HashFile(Path.Combine(_directory, filename)); _index++;
                        lock (_sync) { _failedBatch = null; _checkpoint = new(batch[^1].Sequence, DateTimeOffset.UtcNow, null); Pulse(); Seal(); }
                    }
                    catch (Exception ex)
                    {
                        lock (_sync) { _failedBatch = batch; _checkpoint = _checkpoint with { Error = "Recording recovery could not be saved: " + ex.Message }; Pulse(); }
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_sync) { _checkpoint = _checkpoint with { Error = "Recording recovery could not be saved: " + ex.Message }; Pulse(); }
            }
        }
        public async Task PrepareCommitAsync(MacroModel result, CancellationToken ct = default)
        {
            lock (_sync) _closing = true;
            await CheckpointAsync(ct).ConfigureAwait(false);
            _expected = new MacroJsonSerializer().Deserialize(new MacroJsonSerializer().Serialize(_owner._prepare(result)));
            await Task.Run(() => _owner.Write(_directory, "commit.zrec",
                new Commit(1, SessionId, Checkpoint.Sequence, Fingerprint(_expected)), MaximumChunkBytes), ct).ConfigureAwait(false);
        }
        public async Task CompleteCommitAsync(CancellationToken ct = default)
        {
            if (_expected is null) throw new InvalidOperationException("Prepare the recording commit before saving its macro.");
            await CheckpointAsync(ct).ConfigureAwait(false);
            await Task.Run(() =>
            {
                MacroModel? saved = _owner._load(_expected.Id);
                if (saved == null || Fingerprint(saved) != Fingerprint(_expected))
                    throw new IOException("The saved recording did not verify; its recovery journal was kept.");
                _owner.Cleanup(SessionId);
            }, ct).ConfigureAwait(false);
        }
        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposeTask == null) { _closing = true; Seal(); _signal.Release(); _disposeTask = DisposeCoreAsync(); }
                return new ValueTask(_disposeTask);
            }
        }
        private async Task DisposeCoreAsync()
        {
            try { await CheckpointAsync().ConfigureAwait(false); }
            finally
            {
                _shutdown.Cancel(); await _worker.ConfigureAwait(false);
                lock (_sync) _disposed = true;
                _shutdown.Dispose(); _signal.Dispose();
            }
        }
    }
}
