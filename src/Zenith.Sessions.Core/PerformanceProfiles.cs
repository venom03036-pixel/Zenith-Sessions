namespace Zenith.Sessions.Core;

public enum ManagerPerformanceProfile { MaximumEfficiency, Balanced, HighPerformance, MainAccountPriority, Custom }

/// <summary>
/// Manager monitoring and eligible-launch policies. Custom values are validated even while dormant.
/// Capacity headroom, authorization, provider support and resource admission remain separate controls.
/// </summary>
public sealed record ManagerPerformanceSettings(ManagerPerformanceProfile Profile = ManagerPerformanceProfile.Balanced,
    Guid? MainAccountProfileId = null, int CustomProcessSampleMs = 1000, int CustomPresentationMs = 1000,
    int CustomStaggerMs = 2000, int CustomStabilizationMs = 20000)
{
    public ManagerPerformanceSettings Validated()
    {
        ManagerPerformanceValidation.ValidateProfile(Profile);
        if (MainAccountProfileId == Guid.Empty) throw new ArgumentException("Choose an existing saved main account, or leave it unselected.");
        ManagerPerformanceValidation.ValidateTiming(CustomProcessSampleMs, CustomPresentationMs, CustomStaggerMs, CustomStabilizationMs);
        return this;
    }

    public ManagerPerformancePolicy Resolve()
    {
        Validated();
        ManagerPerformancePolicy policy = Profile switch
        {
            ManagerPerformanceProfile.MaximumEfficiency => new(Profile, 2000, 1000, 5000, 30000),
            ManagerPerformanceProfile.Balanced => new(Profile, 1000, 1000, 2000, 20000),
            ManagerPerformanceProfile.HighPerformance => new(Profile, 500, 500, 2000, 20000),
            ManagerPerformanceProfile.MainAccountPriority => new(Profile, 2000, 1000, 3000, 25000,
                MainAccountProfileId, MainAccountProfileId is not null ? 100 : 0),
            ManagerPerformanceProfile.Custom => new(Profile, CustomProcessSampleMs, CustomPresentationMs, CustomStaggerMs, CustomStabilizationMs),
            _ => throw new ArgumentOutOfRangeException(nameof(Profile))
        };
        return policy.Validated();
    }
}

/// <summary>Immutable policy snapshots; direct consumers validate before publishing a snapshot.</summary>
public sealed record ManagerPerformancePolicy(ManagerPerformanceProfile Profile, int ProcessSampleMs, int PresentationMs,
    int StaggerMs, int StabilizationMs, Guid? MainAccountProfileId = null, int MainAccountPriorityBoost = 0)
{
    public int EssentialResourceSampleMs => (int)CapacityPolicy.EssentialResourceSampleInterval.TotalMilliseconds;
    public int ResourceSampleExpiryMs => (int)CapacityPolicy.ResourceSampleExpiry.TotalMilliseconds;
    public bool NeedsMainAccount => Profile == ManagerPerformanceProfile.MainAccountPriority && MainAccountProfileId is null;
    public string DisplayName => Profile switch
    {
        ManagerPerformanceProfile.MaximumEfficiency => "Maximum Efficiency",
        ManagerPerformanceProfile.Balanced => "Balanced",
        ManagerPerformanceProfile.HighPerformance => "High Performance",
        ManagerPerformanceProfile.MainAccountPriority => "Main Account Priority",
        ManagerPerformanceProfile.Custom => "Custom",
        _ => throw new ArgumentOutOfRangeException(nameof(Profile))
    };
    public string Explanation
    {
        get
        {
            string timing = FormattableString.Invariant($"Process observations every {ProcessSampleMs} ms; dashboard updates at most every {PresentationMs} ms; eligible launches staggered by {StaggerMs} ms with {StabilizationMs} ms stabilization.");
            string priority = NeedsMainAccount ? " Select a main saved account to enable its launch-order priority."
                : MainAccountPriorityBoost > 0 ? " The selected main account receives a bounded 100-point launch-order boost; queue aging and admission guards still apply."
                : " Launch order follows the existing profile priorities and queue aging.";
            return timing + priority + FormattableString.Invariant($" Essential RAM, commit and CPU safety sampling stays at {EssentialResourceSampleMs} ms with {ResourceSampleExpiryMs} ms expiry. Resource headroom stays configured separately.");
        }
    }
    public string PendingCapabilities => "Native FPS, graphics, rendering, client CPU caps and automatic affinity policies, live previews, macro scheduling and per-account performance overrides require supported integration and validation. Client process priority, efficiency mode and core spreading are set in Sessions, under Client CPU priority. Faster manager observations do not establish improved game FPS or measured capacity gains.";

    public ManagerPerformancePolicy Validated()
    {
        ManagerPerformanceValidation.ValidateProfile(Profile);
        ManagerPerformanceValidation.ValidateTiming(ProcessSampleMs, PresentationMs, StaggerMs, StabilizationMs);
        if (MainAccountProfileId == Guid.Empty || MainAccountPriorityBoost is < 0 or > 100
            || Profile != ManagerPerformanceProfile.MainAccountPriority && (MainAccountProfileId is not null || MainAccountPriorityBoost != 0)
            || Profile == ManagerPerformanceProfile.MainAccountPriority
                && (MainAccountProfileId is null ? MainAccountPriorityBoost != 0 : MainAccountPriorityBoost != 100))
            throw new ArgumentException("Main-account launch-order priority requires its selected saved profile and a bounded 100-point boost; unselected or other profiles have no boost.");
        return this;
    }

    public int GetPriorityBoost(Guid profileId)
    {
        Validated();
        return MainAccountProfileId == profileId ? MainAccountPriorityBoost : 0;
    }
}

internal static class ManagerPerformanceValidation
{
    public static void ValidateProfile(ManagerPerformanceProfile profile)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile), "Choose one of the five manager performance profiles.");
    }
    public static void ValidateTiming(int processSampleMs, int presentationMs, int staggerMs, int stabilizationMs)
    {
        if (processSampleMs is < 500 or > 5000) throw new ArgumentOutOfRangeException(nameof(processSampleMs), "Process observations must use 500–5000 ms.");
        if (presentationMs is < 250 or > 2000) throw new ArgumentOutOfRangeException(nameof(presentationMs), "Dashboard updates must use 250–2000 ms.");
        if (staggerMs is < 1000 or > 30000) throw new ArgumentOutOfRangeException(nameof(staggerMs), "Eligible launch staggering must use 1000–30000 ms.");
        if (stabilizationMs is < 20000 or > 120000) throw new ArgumentOutOfRangeException(nameof(stabilizationMs), "Launch stabilization must use 20000–120000 ms.");
    }
}
