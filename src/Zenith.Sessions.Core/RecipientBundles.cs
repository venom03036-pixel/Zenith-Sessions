using System.IO;

namespace Zenith.Sessions.Core;

/// <summary>Ordered local profile references only. A bundle never authenticates an account or requests a launch.</summary>
public sealed record RecipientBundle(Guid Id, string Name, IReadOnlyList<Guid> ProfileIds, long Revision = 0, Guid ChangeId = default)
{
    public const int MaximumCount = 100, MaximumRecipients = 1000, MaximumTotalRecipientReferences = 10000, MaximumNameLength = 80;
    public static RecipientBundle Create(string name, IReadOnlyList<Guid> profileIds) => new RecipientBundle(Guid.NewGuid(), name, profileIds).Validated();
    public RecipientBundle Validated()
    {
        if (Id == Guid.Empty || Revision < 0 || (Revision == 0) != (ChangeId == Guid.Empty))
            throw new ArgumentException("The recipient bundle identity, revision or change identity is invalid.");
        string name = Revision == 0 ? ValidateName(Name) : ValidateSavedName(Name);
        if (ProfileIds is null || ProfileIds.Count is < 1 or > MaximumRecipients)
            throw new ArgumentException("Choose 1 to 1,000 saved profile references for a recipient bundle.");
        int count = ProfileIds.Count; var ids = new Guid[count]; var unique = new HashSet<Guid>();
        for (int index = 0; index < count; index++)
        {
            Guid id = ProfileIds[index];
            if (id == Guid.Empty || !unique.Add(id)) throw new ArgumentException("Recipient profile references must be nonempty and unique. Duplicate references were not saved.");
            ids[index] = id;
        }
        return this with { Name = name, ProfileIds = Array.AsReadOnly(ids) };
    }
    /// <summary>Checks a new or changed bundle name against the shared display-name rule and returns it trimmed.</summary>
    public static string ValidateName(string name) => DisplayNameRules.IsPlain(name, MaximumNameLength) ? name.Trim() : throw new ArgumentException(NameRule);
    // A saved revision keeps the name it was saved under; a rename goes through ValidateName.
    private static string ValidateSavedName(string name) => DisplayNameRules.IsReadable(name, MaximumNameLength) ? name.Trim() : throw new ArgumentException(NameRule);
    private const string NameRule = "Use a bundle name of 1 to 80 characters on one line, without links, access codes or credentials.";

    /// <summary>Call with existence results for these references, not a credential/identity inventory. Missing references remain saved until explicitly edited.</summary>
    public RecipientBundleResolution ResolveRecipients(IReadOnlySet<Guid> existingProfileIds)
    {
        ArgumentNullException.ThrowIfNull(existingProfileIds);
        var bundle = Validated(); var available = new List<Guid>(); var missing = new List<Guid>();
        foreach (Guid id in bundle.ProfileIds) (existingProfileIds.Contains(id) ? available : missing).Add(id);
        return new(bundle.Id, available.AsReadOnly(), missing.AsReadOnly());
    }
    public override string ToString() => $"RecipientBundle {{ Id = {Id}, Recipients = {ProfileIds?.Count ?? 0}, Revision = {Revision}, Authorization = none }}";
}

public sealed record RecipientBundleResolution(Guid BundleId, IReadOnlyList<Guid> AvailableProfileIds, IReadOnlyList<Guid> MissingProfileIds)
{
    public bool RequiresReview => MissingProfileIds.Count != 0;
    public bool AuthorizesLaunch => false;
    public string Summary => $"{AvailableProfileIds.Count} saved profile references found; {MissingProfileIds.Count} missing. References do not establish sign-in, process identity, capacity or arrival.";
}
public sealed record RecipientBundleSnapshot(long Revision, IReadOnlyList<RecipientBundle> Bundles, string? Notice = null);

public interface IRecipientBundleStore
{
    Task<RecipientBundleSnapshot> LoadAsync(CancellationToken ct);
    Task<RecipientBundle> SaveAsync(RecipientBundle bundle, CancellationToken ct);
    Task<RecipientBundle> RenameAsync(Guid id, long expectedRevision, Guid expectedChangeId, string name, CancellationToken ct);
    Task DeleteAsync(Guid id, long expectedRevision, Guid expectedChangeId, CancellationToken ct);
}
public sealed class RecipientBundleConflictException() : Exception("This recipient bundle changed elsewhere. Refresh bundles before saving, renaming or deleting it.");
public sealed class RecipientBundleCapacityException() : Exception("Recipient bundle storage reached its operational limit: 100 bundles, 1,000 references per bundle or 10,000 references total. Saved account profiles are not limited.");
public sealed class RecipientBundleBusyException() : IOException("Recipient bundle storage is busy. Wait for its owned operations to finish, then retry.");
public enum RecipientBundleStorageProblem { Damaged, UnsupportedVersion, RecoveryBlocked }
public sealed class RecipientBundleStorageException(RecipientBundleStorageProblem problem, string message) : IOException(message)
{
    public RecipientBundleStorageProblem Problem { get; } = problem;
}
