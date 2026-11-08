namespace Zenith.Sessions.Core;

/// <summary>
/// Why a launch request that is waiting to start (Blocked, InsufficientCapacity) waits. Memory-only, like
/// <see cref="LaunchQueueSnapshot.PauseReason"/>: never written to the queue file, and a restart turns every waiting row
/// into a review anyway.
/// </summary>
public enum LaunchHoldReason { ResourceHeadroom, ClientUpdate }

public sealed record ClientUpdateHoldDecision(bool Hold, string Reason);

/// <summary>
/// Whether starting the selected Roblox player now may close running clients: Roblox updates an outdated player as it
/// starts, and its installer closes every running client. Pure: the caller supplies the comparison of the selected player
/// with Roblox's current public release and how many clients may be running. Only a known mismatch with a client running
/// holds; an unknown release, a player that could not be compared and a current player never do (Rule 16). The caller
/// passes NotCurrent only while that comparison is still evidence about the next start: the release was read after the
/// player was observed, the file still exists, and no client was started from it for that release since.
/// Only the default live channel is read, so the text never claims more than "may".
/// A row already held keeps that reason through a short run of process samples whose running count cannot be trusted
/// (<see cref="KeepsHoldWithoutTrustedCount"/>); a longer failure returns it to the ordinary wait for fresh evidence.
/// </summary>
public static class ClientUpdateHold
{
    public const string Explanation = "Roblox may have an update that could close running clients: the selected player does not match the current release of Roblox's default live channel (the only channel checked), and Roblox updates an outdated player as it starts. This request starts on its own once no Roblox client is running; cancel it to stop waiting.";

    /// <summary>
    /// How long a row already held for an update keeps that reason while process samples cannot be trusted (stale, incomplete
    /// or failed), measured from the newest trusted inventory's observation time. Twice
    /// <see cref="LaunchReservationProjection.MaximumEvidenceAge"/>: the 1 Hz sampler's samples alternating between fresh and
    /// untrusted on a busy PC stay inside it, so the row is not saved again and re-worded sample by sample, while a monitoring
    /// failure that outlasts it hands the row back to the resource explanation, which names what then blocks it: fresh
    /// complete process evidence, not a client that may no longer run. Starts are refused on every untrusted sample either way.
    /// </summary>
    public static readonly TimeSpan UntrustedSampleGrace = LaunchReservationProjection.MaximumEvidenceAge * 2;

    /// <summary>
    /// Whether a row already held for an update may keep that reason on a sample whose running count cannot be trusted:
    /// only while the newest trusted process inventory (observed at <paramref name="newestTrustedInventoryUtc"/>, default
    /// before any) is no older than <see cref="UntrustedSampleGrace"/>. A time in the future is not evidence.
    /// </summary>
    public static bool KeepsHoldWithoutTrustedCount(DateTimeOffset newestTrustedInventoryUtc, DateTimeOffset now)
        => newestTrustedInventoryUtc != default && newestTrustedInventoryUtc <= now && now - newestTrustedInventoryUtc <= UntrustedSampleGrace;

    // Fixed answers: admission asks once per monitor sample, so a decision never allocates.
    private static readonly ClientUpdateHoldDecision Held = new(true, Explanation),
        NothingRunning = new(false, "No Roblox client is running, so an update installed as the selected player starts closes nothing."),
        CurrentPlayer = new(false, "The selected player matches Roblox's current release."),
        NotCompared = new(false, "The selected installation could not be compared with Roblox's current release, so starts are not held for it."),
        Unknown = new(false, "Roblox's current release is unknown, so starts are not held for it.");

    public static ClientUpdateHoldDecision Evaluate(ClientReleaseMatch selectedPlayer, int runningClients)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(runningClients);
        return selectedPlayer switch
        {
            ClientReleaseMatch.NotCurrent => runningClients > 0 ? Held : NothingRunning,
            ClientReleaseMatch.Current => CurrentPlayer,
            ClientReleaseMatch.NotCompared => NotCompared,
            _ => Unknown
        };
    }
}
