namespace Zenith.Sessions.Core;

/// <summary>
/// Why a launch request that is waiting to start (Blocked, InsufficientCapacity) waits. Memory-only, like
/// <see cref="LaunchQueueSnapshot.PauseReason"/>: never written to the queue file, and a restart turns every waiting row
/// into a review anyway. PublicNetwork is the public-network guard's hold (<see cref="PublicNetworkPolicy"/>).
/// </summary>
public enum LaunchHoldReason { ResourceHeadroom, ClientUpdate, PublicNetwork }

public sealed record ClientUpdateHoldDecision(bool Hold, string Reason);

/// <summary>
/// Whether starting the selected Roblox player now may close running clients: Roblox updates an outdated player as it
/// starts, and its installer closes every running client. Pure: the caller supplies the comparison of the selected player
/// with Roblox's current public release and how many clients may be running. Only a known mismatch with a client running
/// holds; an unknown release, a player that could not be compared and a current player never do (Rule 16). The caller
/// passes NotCurrent only while that comparison is still evidence about the next start: the release was read after the
/// player was observed, the file still exists, and no client was started from it for that release since.
/// Only the default live channel is read, so the text never claims more than "may".
/// </summary>
public static class ClientUpdateHold
{
    public const string Explanation = "Roblox may have an update that could close running clients: the selected player does not match the current release of Roblox's default live channel (the only channel checked), and Roblox updates an outdated player as it starts. This request starts on its own once no Roblox client is running; cancel it to stop waiting.";

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
