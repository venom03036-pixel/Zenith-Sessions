using System.Text.Json.Serialization;

namespace Zenith.Sessions.Core;

// ExtraClientArguments (R-launch-16) is this account's own extra client arguments for Zenith's direct player start, replacing
// the saved default when set; empty by default. It stays in the account library only: [JsonIgnore] keeps it out of metadata
// exports (and an export carrying it is refused by the import allowlist), and it is never logged.
public sealed record AccountProfile(Guid Id, string Label, string Username, string Group, int Priority,
    long? PreferredPlaceId, long? RobloxUserId, DateTimeOffset UpdatedUtc, long Revision = 0,
    string Notes = "", bool IsFavorite = false, string AdditionalGroups = "", [property: JsonIgnore] string ExtraClientArguments = "")
{
    [JsonIgnore] public IReadOnlyList<string> GroupNames => AccountGroups.Names(Group, AdditionalGroups);
    [JsonIgnore] public string GroupsSummary => string.Join(" · ", GroupNames);
    public static AccountProfile Create(string label) => new(Guid.NewGuid(), label, "", "General", 0, null, null, DateTimeOffset.UtcNow);
    public AccountProfile Validated()
    {
        if (Id == Guid.Empty || Revision < 0 || Revision == long.MaxValue) throw new ArgumentException("Account identity or revision is invalid.");
        if(Label is null || Username is null || Group is null || Notes is null) throw new ArgumentException("Name, username, group and notes cannot be null.");
        string label = Label.Trim(), username = Username.Trim(), group = AccountGroups.ValidateName(Group);
        if (label.Length is < 1 or > 100 || label.Any(char.IsControl)) throw new ArgumentException("Name must contain 1–100 readable characters.");
        if (username.Length > 20 || !PublicRobloxUsername.HasValidCharacters(username))
            throw new ArgumentException("Username must use up to 20 letters, numbers, or underscores.");
        if (Priority is < 0 or > 100) throw new ArgumentException("Priority must be between 0 and 100.");
        if (PreferredPlaceId is <= 0 || RobloxUserId is <= 0) throw new ArgumentException("Roblox IDs must be positive numbers.");
        if (Notes.Length > 4000 || Notes.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new ArgumentException("Notes must contain up to 4,000 readable characters. Do not enter passwords or tokens.");
        if (ExtraClientArguments is null) throw new ArgumentException("Extra client arguments cannot be null.");
        string arguments = ExtraClientArgumentPolicy.Validate(ExtraClientArguments);
        return this with { Label = label, Username = username, Group = group, AdditionalGroups = AccountGroups.Canonicalize(group, AdditionalGroups), ExtraClientArguments = arguments };
    }
    // User-entered metadata is never treated as verified authentication or process identity.
    public string IdentityState => "Not linked";
}

/// <summary>Bounded user-entered metadata only. Membership never grants identity or launch authority.</summary>
public static class AccountGroups
{
    public const int MaximumNameLength = 60, MaximumGroups = 32, MaximumAdditionalGroups = MaximumGroups - 1, MaximumInputLength = 2048;
    public static string ValidateName(string name)
    {
        if (name is null || name.Any(char.IsControl) || !ValidUnicode(name)) throw new ArgumentException("Group must contain 1–60 readable characters.");
        string trimmed = name.Trim();
        if (trimmed.Length is < 1 or > MaximumNameLength) throw new ArgumentException("Group must contain 1–60 readable characters.");
        return trimmed;
    }
    public static string Canonicalize(string primary, string additional)
    {
        primary = ValidateName(primary);
        if (additional is null || additional.Length > MaximumInputLength) throw new ArgumentException("Additional groups must contain at most 2,048 characters.");
        string lines = additional.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (lines.Any(value => char.IsControl(value) && value != '\n') || !ValidUnicode(lines))
            throw new ArgumentException("Enter readable group names on separate lines; other control characters are not allowed.");
        var names = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary };
        foreach (string line in lines.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string name = ValidateName(line);
            if (!seen.Add(name)) continue;
            if (names.Count == MaximumAdditionalGroups) throw new ArgumentException("A profile can belong to at most 32 groups including its primary group.");
            names.Add(name);
        }
        return string.Join('\n', names);
    }
    public static IReadOnlyList<string> Names(string primary, string additional)
    {
        primary = ValidateName(primary); string canonical = Canonicalize(primary, additional);
        return Array.AsReadOnly(canonical.Length == 0 ? new[] { primary } : new[] { primary }.Concat(canonical.Split('\n')).ToArray());
    }
    public static bool Contains(AccountProfile profile, string group)
    {
        ArgumentNullException.ThrowIfNull(profile); group = ValidateName(group);
        return Names(profile.Group, profile.AdditionalGroups).Contains(group, StringComparer.OrdinalIgnoreCase);
    }
    public static AccountProfile Add(AccountProfile profile, string group)
    {
        ArgumentNullException.ThrowIfNull(profile); profile = profile.Validated(); group = ValidateName(group);
        if (Contains(profile, group)) return profile;
        return (profile with { AdditionalGroups = profile.AdditionalGroups.Length == 0 ? group : profile.AdditionalGroups + "\n" + group }).Validated();
    }
    private static bool ValidUnicode(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 == value.Length || !char.IsLowSurrogate(value[++index])) return false;
        }
        return true;
    }
}

public enum AccountOrder { Name, Priority, RecentlyUpdated, FavoritesFirst }
public sealed record AccountQuery(string Search = "", string? Group = null, AccountOrder Order = AccountOrder.Name, int Page = 0, int PageSize = 100);
public sealed record AccountPage(IReadOnlyList<AccountProfile> Items, int Total, int Page, int PageSize);
public sealed record CredentialCleanupPage(IReadOnlyList<Guid> ProfileIds,int Total);

public interface IAccountRepository
{
    Task InitializeAsync(CancellationToken ct);
    Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct);
    Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct);
    Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct);
    Task<AccountProfile> SaveAsync(AccountProfile account, CancellationToken ct);
    Task DeleteAsync(Guid id, long revision, CancellationToken ct);
    Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct);
    Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct);
    Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct);
    Task CompleteCredentialCleanupAsync(Guid id,CancellationToken ct);
}

public interface IAccountGroupEditor
{
    const int MaximumBatchSize = 100;
    // Add membership to these exact persisted revisions, atomically; no-op rows are checked too.
    Task<int> ApplyGroupAsync(IReadOnlyList<AccountProfile> expected, string group, CancellationToken ct);
}

public sealed class RevisionConflictException() : Exception("This profile changed elsewhere. Reload it before saving.");

public enum CapabilityState { Supported, Unverified, Unavailable }
public sealed record ProviderCapability(string Name, CapabilityState State, string Explanation);
public static class ProviderCapabilities
{
    public static IReadOnlyList<ProviderCapability> Current { get; } = Array.AsReadOnly(new[]
    {
        new ProviderCapability("Account library", CapabilityState.Supported, "Saved local profiles, groups, search and metadata export."),
        new ProviderCapability("Desktop monitoring", CapabilityState.Supported, "Read-only Roblox process, window and resource observations."),
        new ProviderCapability("Optional window previews", CapabilityState.Unverified, "Opt-in bounded Windows capture and synthetic lifecycle/buffer tests are implemented. Real Roblox capture, hidden/overlapping-window freshness, resource overhead and final visual acceptance remain unverified. Minimized content is not captured."),
        new ProviderCapability("Saved Roblox sign-ins",CapabilityState.Unverified,"Explicit app-owned private Roblox sign-in, authenticated user-ID verification and per-account Windows-protected storage are implemented. Live sign-in/challenges and runtime cleanup still need acceptance testing. No OAuth app registration is required; this does not prove native-client launch."),
        new ProviderCapability("Named-account launch & reconnect", CapabilityState.Unavailable, "No verified provider can select a stored account and confirm native arrival. Browser sign-in and profile labels do not establish this."),
        new ProviderCapability("Background macros & broadcast", CapabilityState.Unverified, "Native diagnostic windows passed; Roblox input consumption and account/window isolation remain unverified."),
        new ProviderCapability("Owned-game join/status bridge", CapabilityState.Unavailable, "The published experience has not yet been configured and verified."),
    });
}
