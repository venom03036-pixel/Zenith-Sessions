using System.IO;
using System.Text.Json.Serialization;

namespace Zenith.Sessions.Core;

public enum AuthorizationMutationStatus { Applied, Conflict }

/// <summary>A non-secret commit receipt. It contains no credential or current launch authority.</summary>
public sealed record AuthorizationMutationResult(AuthorizationMutationStatus Status, long? CommittedGeneration = null);

public interface IConditionalAuthorizationVault
{
    // Use a fresh random mutationId for each attempt, never reuse one for a different operation.
    // Both templates carry the expected current generation and identical contextual identity.
    // The vault compares the entire current protected record and authoritative generation under
    // one cross-process lease, then owns allocation/reservation/commit of the next generation.
    // Conflict writes no credential, index or generation data. Partial I/O failure may reserve
    // ownership without committing; preserve that exception's outcome and fence by mutationId.
    Task<AuthorizationMutationResult> TryReplaceNextAsync(AuthorizationRecord expected, AuthorizationRecord replacement,
        Guid mutationId, CancellationToken ct);

    // Atomically fences only this mutation's latest reservation, including a partial commit.
    // False means no matching latest owner; another grant must never be invalidated instead.
    // Protected bytes/claims are preserved; normal reads then report SignInRequired.
    Task<bool> TryFenceOwnedMutationAsync(Guid profileId, Guid mutationId, CancellationToken ct);
}

/// <summary>
/// An operation-local comparison snapshot, not authorization to launch. Record is the selected
/// read-policy form and is excluded from serialization; the hash identifies its encrypted bytes.
/// </summary>
public sealed record AuthorizationWriteSnapshot(Guid ProfileId, [property: JsonIgnore] AuthorizationRecord? Record,
    long HighestAllocated, long LastCommitted, Guid HighestMutationId, string? RecordSha256)
{
    [JsonIgnore]
    public long NextGeneration => checked(Math.Max(HighestAllocated, Record?.Generation ?? 0) + 1);

    public override string ToString() => $"AuthorizationWriteSnapshot {{ ProfileId = {ProfileId}, HighestAllocated = {HighestAllocated}, LastCommitted = {LastCommitted}, Record = [protected], Hash = [redacted] }}";
}

public interface ISnapshotAuthorizationVault : IConditionalAuthorizationVault
{
    Task<AuthorizationWriteSnapshot> ReadWriteSnapshotAsync(Guid profileId, CancellationToken ct);
    Task<AuthorizationMutationResult> TryCommitSnapshotAsync(AuthorizationWriteSnapshot expected, AuthorizationRecord replacement,
        Guid mutationId, CancellationToken ct);
    Task<AuthorizationMutationResult> TryDeleteSnapshotAsync(AuthorizationWriteSnapshot expected, CancellationToken ct);
}

public sealed class AuthorizationVaultBusyException() : IOException("The protected store is busy. Wait for its owned operation to finish, then retry.");
