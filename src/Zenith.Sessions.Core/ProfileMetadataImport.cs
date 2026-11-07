using System.Text.Json;

namespace Zenith.Sessions.Core;

/// <summary>A bounded, metadata-only import. Its values never establish authorization.</summary>
public static class ProfileMetadataImport
{
    public const int MaximumBytes = 16 * 1024 * 1024, MaximumProfiles = 100_000;
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "Id", "Label", "Username", "Group", "Priority", "PreferredPlaceId", "RobloxUserId",
        "UpdatedUtc", "Revision", "Notes", "IsFavorite", "IdentityState", "AdditionalGroups"
    };

    public static IReadOnlyList<AccountProfile> Decode(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
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
                if (!envelope.Add(field.Name) || field.Name is not ("Version" or "Profiles")) throw Invalid();
            if (envelope.Count != 2 || !root.GetProperty("Version").TryGetInt32(out int version) || version is not (1 or 2 or 3)) throw Invalid();
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
            return result.AsReadOnly();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
    }

    private static InvalidDataException Invalid() => new("The profile export is malformed or uses unsupported fields or versions. Existing profiles were preserved.");
    private static InvalidDataException MembershipVersion() => new("Multiple group memberships require a version 3 profile export. Existing profiles were preserved.");
}
