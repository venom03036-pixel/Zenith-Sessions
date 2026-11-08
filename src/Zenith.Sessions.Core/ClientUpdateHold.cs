namespace Zenith.Sessions.Core;

/// <summary>
/// Why a launch request that is waiting to start (Blocked, InsufficientCapacity) waits. Memory-only, like
/// <see cref="LaunchQueueSnapshot.PauseReason"/>: never written to the queue file, and a restart turns every waiting row
/// into a review anyway.
/// </summary>
public enum LaunchHoldReason { ResourceHeadroom, ClientUpdate }

public sealed record ClientUpdateHoldDecision(bool Hold, string Reason);

/// <summary>
/// Whether starting the selected Roblox player now would close running clients: Roblox updates an outdated player as it
/// starts, and its installer closes every running client. Pure: the caller supplies the comparison of the selected player
/// with Roblox's current public release and how many clients may be running. Only a known mismatch with a client running
/// holds; an unknown release, a player that could not be compared and a current player never do (Rule 16).
/// </summary>
public static class ClientUpdateHold
{
    public const string Explanation = "Roblox has an update that would close running clients. The selected player is older than Roblox's current release, and Roblox updates a player as it starts. This request starts on its own once no Roblox client is running.";

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
