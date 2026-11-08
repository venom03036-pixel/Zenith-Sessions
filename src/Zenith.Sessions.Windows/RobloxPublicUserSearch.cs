using System.Buffers;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxPublicDirectory : IPublicRobloxUsernameDirectory
{
    // Anonymous endpoint documented at https://create.roblox.com/docs/cloud/reference/domains/users. It takes
    // no cookie or CSRF token and supplies public metadata only; never attach authorization.
    private static readonly Uri UsernameLookupUri = new("https://users.roblox.com/v1/usernames/users");

    public Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxUsernameMatch>>> LookupUsersByNameAsync(
        IReadOnlyList<string> usernames, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string invalid = "Enter one to fifty different Roblox usernames, each 3 to 20 letters, digits or underscores.";
        if (usernames is null || usernames.Count is 0 or > PublicRobloxUsername.MaximumBatch)
            return Task.FromResult(Invalid<IReadOnlyList<PublicRobloxUsernameMatch>>(invalid));
        // Roblox usernames are case-insensitive, so "Roblox" and "roblox" are the same request.
        var requested = new string[usernames.Count]; var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < requested.Length; index++)
        {
            string name = usernames[index];
            if (!PublicRobloxUsername.IsValid(name) || !distinct.Add(name))
                return Task.FromResult(Invalid<IReadOnlyList<PublicRobloxUsernameMatch>>(invalid));
            requested[index] = name;
        }
        return ReadJsonAsync(UsernameLookupUri, root => ParseUsernameLookup(root, requested), ct, jsonBody: UsernameLookupBody(requested));
    }

    private static byte[] UsernameLookupBody(string[] usernames)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("usernames");
            foreach (string name in usernames) writer.WriteStringValue(name);
            writer.WriteEndArray();
            // A banned account cannot be joined or followed, so Roblox leaves it out like an unknown name.
            writer.WriteBoolean("excludeBannedUsers", true);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private PublicDirectoryResult<IReadOnlyList<PublicRobloxUsernameMatch>> ParseUsernameLookup(JsonElement root, string[] requested)
    {
        JsonElement data = ReadField(root, "data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > requested.Length) throw new InvalidPublicResponseException();
        // Roblox does not promise reply order and also matches previous usernames, so replies are matched by
        // requestedUsername, not by name. An unasked or repeated requested name makes the reply invalid.
        var positions = new Dictionary<string, int>(requested.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < requested.Length; index++) positions.Add(requested[index], index);
        var found = new PublicRobloxUser?[requested.Length];
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (var item in data.EnumerateArray())
        {
            string echoed = ReadText(item, "requestedUsername", PublicRobloxUsername.MaximumLength);
            if (!positions.TryGetValue(echoed, out int position) || found[position] is not null) throw new InvalidPublicResponseException();
            long userId = ReadId(item, "id");
            string username = ReadText(item, "name", PublicRobloxUsername.MaximumLength);
            if (!PublicRobloxUsername.HasValidCharacters(username)) throw new InvalidPublicResponseException();
            found[position] = new PublicRobloxUser(userId, username, ReadText(item, "displayName", 100), now);
        }
        var matches = new List<PublicRobloxUsernameMatch>(requested.Length);
        for (int index = 0; index < requested.Length; index++)
            if (found[index] is { } user) matches.Add(new(requested[index], user));
        string message = matches.Count == requested.Length
            ? "Public profile information. This does not verify sign-in or identify a running client."
            : matches.Count == 0 ? "No public Roblox user was found for these usernames. Banned accounts are not shown."
            : "Some usernames were not found or belong to banned accounts. Public profile information only; it does not verify sign-in or identify a running client.";
        return Success<IReadOnlyList<PublicRobloxUsernameMatch>>(matches.AsReadOnly(), message);
    }
}
