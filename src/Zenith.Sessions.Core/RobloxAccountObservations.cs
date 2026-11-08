namespace Zenith.Sessions.Core;

// These observations never confer launch authority and have no persistence/export contract.
public enum RobloxAccountRead { Presence, Robux, Premium, EmailVerified, TwoStepMethods, BanState, FriendsCount, FollowersCount, FriendshipStatus, RequestFriendship, AcceptFriendship }
public enum RobloxAccountOutcome { Success, Unavailable, SignInRequired, Challenge, RateLimited }
public enum RobloxReportedPresence { Offline, Online, InGame, InStudio }
public enum RobloxFriendshipStatus { NotFriends, Friends, RequestSent, RequestReceived }
public sealed record ServerReportedPresence(RobloxReportedPresence State, long? PlaceId, string? GameId)
{ public override string ToString() => "[Server-reported presence; not client arrival evidence]"; }

public sealed record RobloxAccountObservation(ServerReportedPresence? Presence = null, long? Number = null,
    bool? Flag = null, string? Methods = null, RobloxFriendshipStatus? Friendship = null)
{ public override string ToString() => "[Private account observation]"; }

/// <summary>Bound to the exact saved sign-in reviewed by the caller. No credentials cross this boundary.</summary>
public sealed record RobloxAccountContext(Guid ProfileId, long UserId, long Generation)
{
    public bool IsValid => ProfileId != Guid.Empty && UserId > 0 && Generation > 0;
    public override string ToString() => "[Reviewed saved-account context]";
}
public sealed record RobloxAccountResult(RobloxAccountOutcome Outcome, RobloxAccountContext Context, RobloxAccountObservation? Observation = null)
{ public override string ToString() => $"Account observation: {Outcome}"; }

/// <summary>Owned response; a replacement session is never exposed in an observation or in its string representation.</summary>
public sealed class RobloxAccountResponse(RobloxAccountOutcome outcome, RobloxAccountObservation? observation = null,
    RobloxSessionCredential? rotatedCredential = null) : IDisposable
{
    public RobloxAccountOutcome Outcome { get; } = outcome;
    public RobloxAccountObservation? Observation { get; } = observation;
    public RobloxSessionCredential? RotatedCredential { get; } = rotatedCredential;
    public void Dispose() => RotatedCredential?.Dispose();
    public override string ToString() => $"Account response: {Outcome}";
}
public interface IRobloxAccountDataClient
{
    Task<RobloxAccountResponse> ReadAsync(RobloxAccountRead kind, long userId, long? targetUserId, RobloxSessionCredential credential, CancellationToken ct);
}
public interface IRobloxAccountDataService
{
    Task<RobloxAccountResult> ReadAccountAsync(RobloxAccountContext context, RobloxAccountRead kind, RobloxAccountContext? target, CancellationToken ct);
}
