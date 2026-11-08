namespace Zenith.Sessions.Core;

/// <summary>How Windows files a connected network: the profile chosen when it was first joined (Public for a café or hotel Wi-Fi).</summary>
public enum NetworkProfile { Public, Private, Domain }

/// <summary>One connected network as Windows reports it: its profile and whether it reaches the internet. Its name and id are never read.</summary>
public readonly record struct ConnectedNetwork(NetworkProfile Profile, bool ReachesInternet);

/// <summary>What the guard knows about the active network. Unknown: Windows could not say, or nothing is watching.</summary>
public enum NetworkExposure { Unknown, NotPublic, Public }

/// <summary>
/// Watches which profile Windows gives the connected networks. <see cref="Start"/> reads now and subscribes to Windows' own change
/// notifications (no polling, Rule 14); <see cref="Changed"/> carries each later reading, on whatever thread Windows delivers it.
/// <see cref="Stop"/> unsubscribes and <see cref="IDisposable.Dispose"/> releases everything (Rule 13). Replaced by a fake in tests,
/// so no test depends on this PC's network.
/// </summary>
public interface IPublicNetworkMonitor : IDisposable
{
    /// <summary>Idempotent: a second call reads again and subscribes nothing twice. Never raises <see cref="Changed"/> itself.</summary>
    NetworkExposure Start();
    /// <summary>Idempotent. A notification already under way may still raise <see cref="Changed"/>; the owner ignores it.</summary>
    void Stop();
    event Action<NetworkExposure>? Changed;
}

/// <summary>
/// The public-network guard's decision, pure: a connected network that Windows files as Public and that reaches the internet makes
/// the exposure Public. A Public network with no internet route (Hyper-V, WSL and VPN adapters are often filed Public) does not, and
/// neither does an unknown reading (Rule 16): Windows not answering never holds a launch. The Windows layer maps the Network List
/// Manager's values to these and the shell owns the hold, the lock and the notice.
/// </summary>
public static class PublicNetworkPolicy
{
    public const string HoldExplanation = "Zenith is on a public network: Windows files the active connection as Public, and Hold launches on a public network is on. This request starts on its own once the network is Private or Domain, or once that setting is saved off; cancel it to stop waiting.";

    public static NetworkExposure Classify(ReadOnlySpan<ConnectedNetwork> networks)
    {
        foreach (var network in networks)
            if (network.Profile == NetworkProfile.Public && network.ReachesInternet) return NetworkExposure.Public;
        return NetworkExposure.NotPublic;
    }

    /// <summary>Only a known Public network holds launches; Unknown never does.</summary>
    public static bool Holds(NetworkExposure exposure) => exposure == NetworkExposure.Public;
}
