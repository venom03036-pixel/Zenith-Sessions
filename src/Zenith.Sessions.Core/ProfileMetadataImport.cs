using System.Text.Json;

namespace Zenith.Sessions.Core;

/// <summary>A saved group as carried by a version 4 export: presentation only, never identity or authority.</summary>
public sealed record ProfileMetadataGroup(string Name, string? ColorHex, long SortOrder);
/// <summary>Decoded export contents. <see cref="Groups"/> is in exported list order and empty before version 4.</summary>
public sealed record ProfileMetadataDocument(IReadOnlyList<AccountProfile> Profiles, IReadOnlyList<ProfileMetadataGroup> Groups);

/// <summary>A library that can take an import's saved groups and new profiles together.</summary>
public interface IAccountMetadataImport
{
    /// <summary>In one transaction, creates the saved groups this library lacks (in the given order, with their colors)
    /// and then the new profiles. A group the library already has keeps its own color and position. Any failure or
    /// cancellation adds nothing. Returns how many saved groups were added.</summary>
    Task<int> ImportMetadataAsync(IReadOnlyList<ProfileMetadataGroup> groups, IReadOnlyList<AccountProfile> profiles, CancellationToken ct);
}

/// <summary>A bounded, metadata-only import. Its values never establish authorization.</summary>
public static class ProfileMetadataImport
{
    public const int CurrentVersion = 4, MaximumBytes = 16 * 1024 * 1024, MaximumProfiles = 100_000, MaximumGroups = 100_000;
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "Id", "Label", "Username", "Group", "Priority", "PreferredPlaceId", "RobloxUserId",
        "UpdatedUtc", "Revision", "Notes", "IsFavorite", "IdentityState", "AdditionalGroups"
    };

    public static IReadOnlyList<AccountProfile> Decode(ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => DecodeDocument(bytes, ct).Profiles;

    public static ProfileMetadataDocument DecodeDocument(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (bytes.Length is 0 or > MaximumBytes) throw Invalid();
        if (bytes.Span.StartsWith("\uFEFF"u8)) bytes = bytes[3..];
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var envelope = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!envelope.Add(field.Name) || field.Name is not ("Version" or "Profiles" or "Groups")) throw Invalid();
            if (!envelope.Contains("Version") || !root.GetProperty("Version").TryGetInt32(out int version) || version is not (1 or 2 or 3 or CurrentVersion)) throw Invalid();
            // Saved groups exist only in version 4, and a version 4 file must state them (an empty list is explicit).
            if (envelope.Count != (version == CurrentVersion ? 3 : 2) || !envelope.Contains("Profiles") || envelope.Contains("Groups") != (version == CurrentVersion)) throw Invalid();
            var groups = version == CurrentVersion ? DecodeGroups(root.GetProperty("Groups"), ct) : Array.Empty<ProfileMetadataGroup>();
            var rows = root.GetProperty("Profiles");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumProfiles) throw Invalid();
            var result = new List<AccountProfile>(rows.GetArrayLength());
            var ids = new HashSet<Guid>();
            foreach (var row in rows.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (row.ValueKind != JsonValueKind.Object) throw Invalid();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in row.EnumerateObject())
                {
                    if (!Fields.Contains(field.Name) || !names.Add(field.Name)) throw Invalid();
                    if (field.Name == "IdentityState" && (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString()!.Length > 80)) throw Invalid();
                }
                var profile = (row.Deserialize<AccountProfile>() ?? throw Invalid()).Validated();
                if (version < 3 && profile.AdditionalGroups.Length != 0) throw MembershipVersion();
                // Test the raw field too: whitespace-only or duplicate-primary text normalizes empty,
                // but still cannot be smuggled into a legacy format with no membership support.
                if (version < 3 && row.TryGetProperty("AdditionalGroups", out var extras)
                    && (extras.ValueKind != JsonValueKind.String || extras.GetString()!.Length != 0)) throw MembershipVersion();
                if (!ids.Add(profile.Id)) throw Invalid();
                result.Add(profile);
            }
            ct.ThrowIfCancellationRequested();
            return new(result.AsReadOnly(), groups);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
    }

    // Exactly Name, ColorHex (a #RRGGBB string or null for the theme color) and a nonnegative SortOrder.
    // Names compare case-insensitively like the saved catalog; a repeated name or position is ambiguous.
    private static IReadOnlyList<ProfileMetadataGroup> DecodeGroups(JsonElement rows, CancellationToken ct)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumGroups) throw Invalid();
        var result = new List<ProfileMetadataGroup>(rows.GetArrayLength());
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var orders = new HashSet<long>();
        foreach (var row in rows.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (row.ValueKind != JsonValueKind.Object) throw Invalid();
            string? name = null, color = null; long order = -1; var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in row.EnumerateObject())
            {
                if (!fields.Add(field.Name)) throw Invalid();
                switch (field.Name)
                {
                    case "Name" when field.Value.ValueKind == JsonValueKind.String: name = field.Value.GetString(); break;
                    case "ColorHex" when field.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null: color = field.Value.GetString(); break;
                    case "SortOrder" when field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt64(out long value) && value >= 0: order = value; break;
                    default: throw Invalid();
                }
            }
            if (fields.Count != 3 || name is null || order < 0) throw Invalid();
            var group = new ProfileMetadataGroup(AccountGroups.ValidateName(name), AccountGroupDefinition.ValidateColor(color), order);
            if (!names.Add(group.Name) || !orders.Add(group.SortOrder)) throw Invalid();
            result.Add(group);
        }
        result.Sort((left, right) => left.SortOrder.CompareTo(right.SortOrder));
        return result.AsReadOnly();
    }

    private static InvalidDataException Invalid() => new("The profile export is malformed or uses unsupported fields or versions. Existing profiles were preserved.");
    private static InvalidDataException MembershipVersion() => new("Multiple group memberships require a version 3 profile export. Existing profiles were preserved.");
}
