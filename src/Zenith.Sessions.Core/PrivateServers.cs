using System.Globalization;

namespace Zenith.Sessions.Core;

/// <summary>
/// Saved private server metadata. The link code itself is stored separately under protection; only its last four
/// characters are kept here. Saving proves neither access nor that the server still exists. GameName is the public game
/// name known when the link was checked (null when none was known); it is display text only and never looked up again.
/// </summary>
public sealed record SavedPrivateServer(Guid Id, string Name, long PlaceId, long UniverseId, long? OwnerUserId, string Last4, long Revision,
    string? GameName = null)
{
    public const int MaximumNameLength = 80;
    public const int MaximumGameNameLength = 256;
    public const int MaximumCount = 100;
    private const char LineSeparator = (char)0x2028, ParagraphSeparator = (char)0x2029;
    private static readonly string[] SensitiveNameMarkers = ["privateServerLinkCode=", "reservedServerAccessCode=", "accessCode=", "linkCode=",
        "code=", "://", ".ROBLOSECURITY", "Bearer ", "refresh_token", "access_token", "client_secret", "authorization="];

    public static string NormalizeName(string? name) => (name ?? "").Trim();
    /// <summary>A public game name fit to keep and show on one line, or null when it is missing, too long or not plain text.</summary>
    public static string? NormalizeGameName(string? name)
    {
        string trimmed = (name ?? "").Trim();
        return trimmed.Length is 0 or > MaximumGameNameLength || trimmed.Any(c => char.IsControl(c) || c == LineSeparator || c == ParagraphSeparator) ? null : trimmed;
    }
    /// <summary>The saved list's Game column: the game's name, or its place when no name was known.</summary>
    public string GameLabel => GameName ?? "Place " + PlaceId.ToString(CultureInfo.InvariantCulture);

    public SavedPrivateServer Validated()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > MaximumNameLength || Name != Name.Trim()
            || Name.Any(c => char.IsControl(c) || c == LineSeparator || c == ParagraphSeparator)
            || SensitiveNameMarkers.Any(marker => Name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || GameName is not null && NormalizeGameName(GameName) != GameName
            || PlaceId <= 0 || UniverseId <= 0 || OwnerUserId is <= 0 || Last4 is not { Length: 4 } || !Last4.All(char.IsAsciiLetterOrDigit) || Revision < 0)
            throw new ArgumentException("A saved private server needs a short plain name (no links or codes), a plain one-line game name when one is kept, positive place and universe IDs and a four-character code suffix.");
        return this;
    }
    public override string ToString() => "Saved private server { Place = " + PlaceId.ToString(CultureInfo.InvariantCulture)
        + ", Revision = " + Revision.ToString(CultureInfo.InvariantCulture) + ", Name = [omitted], Code = [protected] }";
}

public enum PrivateServerStorageProblem { Damaged, UnsupportedVersion }
public sealed class PrivateServerStorageException(PrivateServerStorageProblem problem, string message) : IOException(message)
{ public PrivateServerStorageProblem Problem { get; } = problem; }
public sealed class PrivateServerConflictException() : Exception("This saved private server changed elsewhere or no longer exists. Refresh before using or removing it.");
public sealed class PrivateServerCapacityException() : Exception("The 100-server limit for saved private servers is reached. Remove one before saving another.");

/// <summary>
/// Protected metadata-plus-code storage. A code leaves the store only as a <see cref="ProtectedPrivateServerCode"/>,
/// never inside a list, log or text. Explicit caller-owned operations only; no polling or background work.
/// </summary>
public interface IPrivateServerStore
{
    Task<IReadOnlyList<SavedPrivateServer>> ReadAsync(CancellationToken ct);
    /// <summary>Save a server. <paramref name="gameName"/> is the checked game's public name, or null when none was known.</summary>
    Task<SavedPrivateServer> AddAsync(string name, long placeId, long universeId, long? ownerUserId, string? gameName, ProtectedPrivateServerCode code, CancellationToken ct);
    Task DeleteAsync(Guid id, long revision, CancellationToken ct);
    Task<ProtectedPrivateServerCode> ReadCodeAsync(Guid id, CancellationToken ct);
}
