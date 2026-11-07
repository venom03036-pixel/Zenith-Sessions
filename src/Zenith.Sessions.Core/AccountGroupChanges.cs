namespace Zenith.Sessions.Core;

public enum AccountGroupChangeKind { Rename, Remove }

/// <summary>Metadata membership change only; removal never deletes an account or authorization.</summary>
public sealed record AccountGroupChange(AccountGroupChangeKind Kind, string SourceName, string DestinationName, bool AllowMerge = false)
{
    public AccountGroupChange Validated()
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Choose a supported group change.");
        string source = AccountGroups.ValidateName(SourceName), destination = AccountGroups.ValidateName(DestinationName);
        if (Kind == AccountGroupChangeKind.Remove && StringComparer.OrdinalIgnoreCase.Equals(source, destination))
            throw new ArgumentException("Choose a different fallback group before removing this group.");
        return this with { SourceName = source, DestinationName = destination };
    }
}

/// <summary>
/// Bounded nonsecret review facts, not authorization or a list of account recipients. The hash
/// binds the change, affected profile revisions/group metadata and destination existence.
/// AffectedCount includes source memberships whose exact spelling may already be unchanged.
/// </summary>
public sealed record AccountGroupChangePreview(AccountGroupChange Change, int AffectedCount, int PrimaryCount,
    bool DestinationExists, string SnapshotFingerprint, bool SourceExists=false, long CatalogRevision=0);

public interface IAccountGroupLifecycle
{
    Task<AccountGroupChangePreview> PreviewGroupChangeAsync(AccountGroupChange change, CancellationToken ct);
    // Rechecks the exact preview and applies changed memberships in one database transaction.
    // A stale preview fails as a whole. No-op rows retain their revision and update time.
    Task<int> ApplyGroupChangeAsync(AccountGroupChangePreview expected, CancellationToken ct);
}
