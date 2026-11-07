namespace Zenith.Sessions.Core;

public enum OwnedExperienceCommandKind { Status, JoinPublicServer }

public sealed record OwnedExperienceServer(long PlaceId, Guid JobId)
{
    internal bool IsValid => OwnedExperienceCommand.IsEngineId(PlaceId) && JobId != Guid.Empty;
}

/// <summary>An expiring request for an already-connected player in a creator-owned experience.</summary>
public sealed class OwnedExperienceCommand
{
    // Roblox exposes these IDs to Luau as numbers. Reject values it cannot compare exactly.
    private const long MaximumEngineId = 9_007_199_254_740_991;
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(45);
    public Guid OperationId { get; }
    public long UniverseId { get; }
    public long UserId { get; }
    public OwnedExperienceServer Source { get; }
    public OwnedExperienceServer? Destination { get; }
    public OwnedExperienceCommandKind Kind => Destination is null ? OwnedExperienceCommandKind.Status : OwnedExperienceCommandKind.JoinPublicServer;
    public DateTimeOffset IssuedUtc { get; }
    public DateTimeOffset ExpiresUtc => IssuedUtc + Lifetime;

    public OwnedExperienceCommand(Guid operationId, long universeId, long userId, OwnedExperienceServer source,
        OwnedExperienceServer? destination, DateTimeOffset issuedUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (operationId == Guid.Empty || !IsEngineId(universeId) || !IsEngineId(userId) || !source.IsValid
            || destination is { IsValid: false } || source.JobId == destination?.JobId
            || issuedUtc < DateTimeOffset.UnixEpoch || issuedUtc > DateTimeOffset.MaxValue - Lifetime)
            throw new ArgumentException("The command needs an exact player, source and, for a move, a different public server.");
        OperationId = operationId;
        UniverseId = universeId;
        UserId = userId;
        Source = source;
        Destination = destination;
        IssuedUtc = DateTimeOffset.FromUnixTimeSeconds(issuedUtc.ToUnixTimeSeconds());
    }

    public static bool IsEngineId(long value) => value is > 0 and <= MaximumEngineId;
}

public enum OwnedExperienceStage { AtSource, Received, TeleportInitiated, Arrived, Rejected }

/// <summary>A server-side player observation. It does not identify any local process or window.</summary>
public sealed record OwnedExperienceAcknowledgement(Guid OperationId, long UniverseId, long UserId,
    OwnedExperienceStage Stage, OwnedExperienceServer Server, DateTimeOffset ObservedUtc);

public enum OwnedExperienceBridgeState
{
    Published, Acknowledged, Pending, Expired, Busy, RateLimited, AuthorizationRequired, InvalidResponse, Unavailable,
    /// <summary>Roblox's response time shows the PC clock more than two seconds ahead of or behind Roblox's.
    /// The server may ignore the command or cut its 45 s short, so a receipt may never arrive. Syncing the
    /// Windows clock fixes it. A later read still reports any receipt the server does write.</summary>
    ClockSkew
}

public sealed record OwnedExperienceBridgeResult(OwnedExperienceBridgeState State,
    OwnedExperienceAcknowledgement? Acknowledgement = null, DateTimeOffset? RetryUtc = null);

public interface IOwnedExperienceBridge
{
    /// <summary>Publish once. A successful response establishes delivery to Open Cloud only.</summary>
    Task<OwnedExperienceBridgeResult> PublishAsync(OwnedExperienceCommand command, CancellationToken cancellationToken);
    /// <summary>Read one acknowledgement. Absence is pending until the request expires.</summary>
    Task<OwnedExperienceBridgeResult> ReadAsync(OwnedExperienceCommand command, CancellationToken cancellationToken);
}
