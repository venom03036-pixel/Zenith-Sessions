namespace Zenith.Sessions.Core;

public enum DataFolderEncryptionState { Verified, Partial, Unavailable, Stopped, Busy, Rejected }
public enum DataFolderEncryptionProblem { None, Unsupported, AccessOrSharing, UnsafePath, Limit, Changed, Failure }

/// <summary>A point-in-time filesystem check, not a portable-backup or cryptographic-erasure guarantee.</summary>
public sealed record DataFolderEncryptionResult(DataFolderEncryptionState State, int VerifiedEntries,
    int FailedEntries, DataFolderEncryptionProblem Problem);

public interface IDataFolderEncryption
{
    Task<DataFolderEncryptionResult> EncryptAsync(CancellationToken cancellationToken);
}
