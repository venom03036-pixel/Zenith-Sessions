using MacroSuite.Core.Models;

namespace MacroSuite.Core.Storage;

public interface IRecordingRecoveryProvider
{
    IRecordingRecoveryStore RecordingRecovery { get; }
}

public sealed record RecordingRecoveryStart(Guid SessionId, MacroModel Baseline, long AppendOffsetTicks,
    string ConverterId = "input-v1", string? SettingsSnapshot = null);

/// <summary>Accepted, filtered input. TimelineTicks uses TimeSpan ticks, independently of the host clock.</summary>
public sealed record RecordingRecoveryPacket(long Sequence, long TimelineTicks, InputEventType Type,
    int X, int Y, int Data, uint ExtraInfo = 0, long Epoch = 0,
    bool IsClientRelative = false, int ClientWidth = 0, int ClientHeight = 0);

public sealed record RecordingRecoveryCheckpoint(long Sequence, DateTimeOffset? SavedAt, string? Error);
public sealed record RecordingRecoveryCandidate(Guid SessionId, RecordingRecoveryStart? Start,
    IReadOnlyList<RecordingRecoveryPacket> Packets, string? ExpectedResultFingerprint,
    string? Problem, bool IsCompletePrefix);

public interface IRecordingRecoverySession : IAsyncDisposable
{
    Guid SessionId { get; }
    RecordingRecoveryCheckpoint Checkpoint { get; }
    bool TryAppend(RecordingRecoveryPacket packet);
    RecordingCommitMetadata CreateCommitMetadata(long throughSequence)
        => throw new NotSupportedException("This recovery provider cannot create authenticated commit metadata.");
    /// <summary>Call only after capture admission is closed and conversion drained; includes a RAM tail beyond a failed checkpoint.</summary>
    RecordingCommitMetadata CreateCapturedCommitMetadata(long capturedThroughSequence)
        => CreateCommitMetadata(capturedThroughSequence);
    Task<bool> AbortIfEmptyAsync(CancellationToken ct = default) => Task.FromResult(false);
    Task CheckpointAsync(CancellationToken ct = default);
    Task PrepareCommitAsync(MacroModel result, CancellationToken ct = default);
    Task CompleteCommitAsync(CancellationToken ct = default);
}

public interface IRecordingRecoveryStore
{
    Task<IRecordingRecoverySession> BeginAsync(RecordingRecoveryStart start, CancellationToken ct = default);
    Task<IReadOnlyList<RecordingRecoveryCandidate>> ReadPendingAsync(CancellationToken ct = default);
    Task<MacroModel?> RecoverAsync(RecordingRecoveryCandidate candidate, CancellationToken ct = default);
}
