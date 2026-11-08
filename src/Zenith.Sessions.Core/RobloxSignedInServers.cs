namespace Zenith.Sessions.Core;

public enum SignedInServerOrder { Recommended, BestLatency, OwnedPrivate }

/// <summary>One explicit page. A cursor belongs to one account, place and order; callers must discard it on changes.</summary>
public sealed record SignedInServerQuery(long PlaceId, SignedInServerOrder Order, bool ExcludeFull = false, string? Cursor = null)
{
    public const int PageSize = 25;
    public SignedInServerQuery Validated()
    {
        if (PlaceId <= 0 || !Enum.IsDefined(Order) || Cursor is not null && (Cursor.Length is < 1 or > 2048 || Cursor.Any(char.IsControl)))
            throw new ArgumentException("Choose a valid place and server order.");
        return this;
    }
    public override string ToString() => "[Signed-in server query; cursor omitted]";
}
public sealed record OwnedPrivateServer(long Id, long PlaceId, long OwnerUserId, string Name, int Playing, int MaxPlayers)
{
    public static bool IsNewName(string name) => DisplayNameRules.IsPlain(name, SavedPrivateServer.MaximumNameLength) && name == name.Trim();
    public static bool IsReadableName(string name) => DisplayNameRules.IsReadable(name, SavedPrivateServer.MaximumNameLength);
    public override string ToString() => "[Owned private server; name omitted]";
}
public sealed record SignedInServerPage(IReadOnlyList<RobloxPublicServer> PublicServers,
    IReadOnlyList<OwnedPrivateServer> PrivateServers, string? NextCursor, bool JoinRestricted = false)
{
    public override string ToString() => "[Signed-in server page; private metadata omitted]";
}
public sealed record SignedInServerResult(RobloxAccountOutcome Outcome, RobloxAccountContext Context, SignedInServerPage? Page = null, TimeSpan? RetryAfter = null)
{
    public override string ToString() => $"Signed-in server result: {Outcome}";
}
public sealed class SignedInServerResponse(RobloxAccountOutcome outcome, SignedInServerPage? page = null,
    RobloxSessionCredential? rotation = null, TimeSpan? retryAfter = null) : IDisposable
{
    public RobloxAccountOutcome Outcome { get; } = outcome;
    public SignedInServerPage? Page { get; } = page;
    public RobloxSessionCredential? Rotation { get; } = rotation;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public void Dispose() => Rotation?.Dispose();
    public override string ToString() => $"Signed-in server response: {Outcome}";
}
/// <summary>Fixed Roblox endpoints only. No purchase, renewal, subscription, permissions or code regeneration methods.</summary>
public interface IRobloxSignedInServerClient
{
    Task<SignedInServerResponse> BrowseAsync(SignedInServerQuery query, long expectedUserId, RobloxSessionCredential credential, CancellationToken ct);
    Task<SignedInServerResponse> RenameAsync(OwnedPrivateServer server, string name, RobloxSessionCredential credential, CancellationToken ct);
}
public interface IRobloxSignedInServerDirectory
{
    Task<SignedInServerResult> BrowseSignedInAsync(RobloxAccountContext context, SignedInServerQuery query, CancellationToken ct);
    Task<SignedInServerResult> RenamePrivateAsync(RobloxAccountContext context, OwnedPrivateServer server, string name, CancellationToken ct);
}
