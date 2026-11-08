using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

// Deliberately excludes executable paths/approvals, launch qualification, automation, security policy,
// process identity, OAuth configuration, webhook secrets and machine-bound display identifiers.
public sealed record PortableBackupPreferences(string Theme, bool DenseAccounts, CapacitySettings Capacity,
    ManagerPerformanceSettings Performance, ClientWindowTileOptions Tile, bool CloseButtonExits)
{
    public PortableBackupPreferences Validated()
    {
        if (Theme is not { Length: > 0 and <= 80 } || Theme.Any(char.IsControl) || Capacity is null || Performance is null || Tile is null)
            throw PortableBackupDocument.Invalid();
        Capacity.Validated(); Performance.Validated(); Tile.Validated(); return this;
    }
    public static PortableBackupPreferences From(SessionsSettings settings) => new(settings.Theme, settings.DenseAccounts,
        settings.Capacity ?? new(), settings.Performance ?? new(), settings.WindowLayout?.Tile ?? new(), settings.CloseButtonExits);
    public SessionsSettings MergeInto(SessionsSettings current, IReadOnlyDictionary<Guid, Guid> profileMap) => (current with
    {
        Theme = Theme, DenseAccounts = DenseAccounts, Capacity = Capacity,
        Performance = Performance with { MainAccountProfileId = Performance.MainAccountProfileId is { } source && profileMap.TryGetValue(source, out var target) ? target : null },
        WindowLayout = (current.WindowLayout ?? new()) with { Tile = Tile }, CloseButtonExits = CloseButtonExits
    }).Validated();
}

public sealed record PortableSavedSignIn(Guid SourceProfileId, long VerifiedUserId, DateTimeOffset ExpiresUtc, byte[] Cookie) : IDisposable
{
    public PortableSavedSignIn Validated()
    {
        if (SourceProfileId == Guid.Empty || VerifiedUserId <= 0 || ExpiresUtc == default || Cookie is not { Length: > 0 and <= RobloxSessionCredential.MaximumLength })
            throw PortableBackupDocument.Invalid();
        // Roblox cookies are ASCII. Avoid permissive UTF-8 replacement that could change imported credential bytes.
        if (Cookie.Any(value => value is < 33 or > 126)) throw PortableBackupDocument.Invalid();
        using var checkedValue = Open(); return this;
    }
    public RobloxSessionCredential Open() => new(Encoding.ASCII.GetString(Cookie), ExpiresUtc);
    public void Dispose() { if (Cookie is not null) CryptographicOperations.ZeroMemory(Cookie); }
    public override string ToString() => "PortableSavedSignIn { [encrypted backup content] }";
}

public sealed record PortableBackupDocument(int Version, AccountProfile[] Profiles, ProfileMetadataGroup[] Groups,
    FavoriteDestination[] Favorites, PortableBackupPreferences Preferences, PortableSavedSignIn[] SignIns) : IDisposable
{
    public const int CurrentVersion = 1, MaximumProfiles = 10000, MaximumGroups = 10000;
    public PortableBackupDocument Validated()
    {
        if (Version != CurrentVersion || Profiles is null || Profiles.Length > MaximumProfiles || Groups is null || Groups.Length > MaximumGroups
            || Favorites is null || Favorites.Length > FavoriteDestination.MaximumCount || Preferences is null || SignIns is null || SignIns.Length > MaximumProfiles)
            throw Invalid();
        Preferences.Validated();
        var profiles = new HashSet<Guid>();
        foreach (var profile in Profiles) { if (profile is null || !profiles.Add(profile.Validated().Id)) throw Invalid(); }
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var positions = new HashSet<long>();
        foreach (var group in Groups)
        {
            if (group is null || group.SortOrder < 0 || !groups.Add(AccountGroups.ValidateName(group.Name)) || !positions.Add(group.SortOrder)) throw Invalid();
            AccountGroupDefinition.ValidateColor(group.ColorHex);
        }
        var favorites = new HashSet<Guid>();
        foreach (var favorite in Favorites) { if (favorite is null || !favorites.Add(favorite.Validated().Id)) throw Invalid(); }
        var sessionProfiles = new HashSet<Guid>(); var users = new HashSet<long>();
        foreach (var signIn in SignIns)
        {
            if (signIn is null || !profiles.Contains(signIn.SourceProfileId) || !sessionProfiles.Add(signIn.SourceProfileId) || !users.Add(signIn.VerifiedUserId)) throw Invalid();
            signIn.Validated();
        }
        return this;
    }
    public void Dispose() { if (SignIns is not null) foreach (var signIn in SignIns) signIn?.Dispose(); }
    public override string ToString() => "PortableBackupDocument { Content = [encrypted backup content] }";
    internal static InvalidDataException Invalid() => new("The portable backup has an unsupported format, invalid fields or excessive content. No sign-ins were imported.");
}
