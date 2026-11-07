using MacroSuite.Core.Storage;

namespace MacroSuite.Core.Models;

/// <summary>Authenticated with its containing document; retained when an already-saved recording is later edited.</summary>
public sealed record RecordingCommitMetadata(Guid SessionId, string BaselineFingerprint, long ThroughSequence,
    RecordingCommitMetadata? Previous = null)
{
    public const int MaximumHistory = 32;
    public static RecordingCommitMetadata Create(Guid sessionId, MacroModel baseline, long sequence)
    {
        baseline.RecordingCommit?.Validate();
        int count = 0; for (var item = baseline.RecordingCommit; item != null; item = item.Previous) count++;
        if (count >= MaximumHistory) throw new IOException("Clean up verified recording recovery journals before starting another recording.");
        var result = new RecordingCommitMetadata(sessionId, EncryptedRecordingRecoveryStore.Fingerprint(baseline), sequence, baseline.RecordingCommit);
        result.Validate(); return result;
    }
    public bool Contains(Guid sessionId, string fingerprint, long sequence)
    {
        for (var item = this; item != null; item = item.Previous)
            if (item.SessionId == sessionId && item.BaselineFingerprint == fingerprint && item.ThroughSequence >= sequence) return true;
        return false;
    }
    public void Validate()
    {
        var seen = new HashSet<Guid>(); int count = 0;
        for (var item = this; item != null; item = item.Previous)
        {
            if (++count > MaximumHistory || item.SessionId == Guid.Empty || !seen.Add(item.SessionId) || item.ThroughSequence < 0
                || item.BaselineFingerprint is not { Length: 64 } || !item.BaselineFingerprint.All(Uri.IsHexDigit))
                throw new InvalidDataException("The recording commit history is invalid.");
        }
    }
}
