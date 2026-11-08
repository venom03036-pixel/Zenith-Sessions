namespace Zenith.Sessions.Core;

/// <summary>A user-facing feature that sends input to Roblox clients and therefore waits for the live input isolation trial.</summary>
public enum GatedFeature { KeepClientsAwake, LaunchAndRun }

public sealed record CapabilityGateDecision(bool Permitted, string Explanation);

/// <summary>
/// The project's own gate in front of every feature that would send input to a Roblox client: such a feature is refused until the
/// live background input isolation trial marks the "Background macros &amp; broadcast" capability Supported. The decision is read
/// from the capability list each time, so no feature keeps its own copy of the answer. Pure and allocation-light; no I/O.
/// </summary>
public sealed class CapabilityGate
{
    public const string BackgroundInputCapability = "Background macros & broadcast";
    private readonly IReadOnlyList<ProviderCapability> _capabilities;

    public CapabilityGate(IReadOnlyList<ProviderCapability>? capabilities = null) => _capabilities = capabilities ?? ProviderCapabilities.Current;

    public static string FeatureName(GatedFeature feature) => feature switch
    {
        GatedFeature.KeepClientsAwake => "Keep clients awake",
        GatedFeature.LaunchAndRun => "Launch and run",
        _ => throw new ArgumentOutOfRangeException(nameof(feature))
    };

    public CapabilityGateDecision Check(GatedFeature feature)
    {
        string name = FeatureName(feature);
        ProviderCapability? required = null;
        foreach (var capability in _capabilities)
            if (capability.Name == BackgroundInputCapability) { required = capability; break; }
        if (required is { State: CapabilityState.Supported })
            return new(true, $"{name} is permitted: {BackgroundInputCapability} is supported.");
        string state = required is null ? "not listed" : required.State switch
        {
            CapabilityState.Unverified => "Unverified",
            CapabilityState.Unavailable => "Unavailable",
            _ => required.State.ToString()
        };
        return new(false, $"{name} is saved but not running. It sends input to Roblox clients, so it is refused until the live " +
            $"input isolation trial marks the {BackgroundInputCapability} capability Verified (it is {state}). Nothing was sent to any client.");
    }
}
