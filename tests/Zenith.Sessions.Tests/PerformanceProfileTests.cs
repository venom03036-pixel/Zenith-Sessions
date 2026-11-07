using Zenith.Sessions.Core;
using Zenith.Sessions.App;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class PerformanceProfileTests
{
    private static readonly Guid Main = new("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void DefaultsResolveToBalancedWithoutSelectingOrBoostingAnAccount()
    {
        var settings = new ManagerPerformanceSettings();
        Assert.Same(settings, settings.Validated());
        Assert.Equal(ManagerPerformanceProfile.Balanced, settings.Profile);
        Assert.Null(settings.MainAccountProfileId);
        var policy = settings.Resolve();
        Assert.Same(policy, policy.Validated());
        Assert.Equal((1000, 1000, 2000, 20000), Timing(policy));
        Assert.Null(policy.MainAccountProfileId);
        Assert.Equal(0, policy.MainAccountPriorityBoost);
        Assert.False(policy.NeedsMainAccount);
    }

    [Theory]
    [InlineData(ManagerPerformanceProfile.MaximumEfficiency, 2000, 1000, 5000, 30000, "Maximum Efficiency")]
    [InlineData(ManagerPerformanceProfile.Balanced, 1000, 1000, 2000, 20000, "Balanced")]
    [InlineData(ManagerPerformanceProfile.HighPerformance, 500, 500, 2000, 20000, "High Performance")]
    [InlineData(ManagerPerformanceProfile.MainAccountPriority, 2000, 1000, 3000, 25000, "Main Account Priority")]
    public void PresetsResolveTheirExactMonitoringAndLaunchPolicies(ManagerPerformanceProfile profile, int process, int presentation, int stagger, int stabilization, string name)
    {
        var policy = new ManagerPerformanceSettings(Profile: profile).Resolve();
        Assert.Equal((process, presentation, stagger, stabilization), Timing(policy));
        Assert.Equal(profile, policy.Profile);
        Assert.Equal(name, policy.DisplayName);
        Assert.Contains(process.ToString(), policy.Explanation);
        Assert.Contains(presentation.ToString(), policy.Explanation);
        Assert.Contains(stagger.ToString(), policy.Explanation);
        Assert.Contains(stabilization.ToString(), policy.Explanation);
        Assert.Contains("manager observations", policy.PendingCapabilities);
        Assert.Contains("supported integration", policy.PendingCapabilities);
    }

    [Fact]
    public void AllFiveProfilesHaveDistinctIdentityAndCustomUsesExplicitValues()
    {
        var policies = Enum.GetValues<ManagerPerformanceProfile>().Select(profile => new ManagerPerformanceSettings(
            Profile: profile, CustomProcessSampleMs: 750, CustomPresentationMs: 750, CustomStaggerMs: 4000, CustomStabilizationMs: 45000).Resolve()).ToArray();
        Assert.Equal(5, policies.Select(policy => policy.Profile).Distinct().Count());
        Assert.Equal(5, policies.Select(policy => policy.DisplayName).Distinct().Count());
        Assert.Equal(5, policies.Select(Timing).Distinct().Count());
        var custom = Assert.Single(policies, policy => policy.Profile == ManagerPerformanceProfile.Custom);
        Assert.Equal((750, 750, 4000, 45000), Timing(custom));
    }

    [Theory]
    [InlineData(500, 250, 1000, 20000)]
    [InlineData(5000, 2000, 30000, 120000)]
    public void ExactCustomBoundsAreAccepted(int process, int presentation, int stagger, int stabilization)
    {
        var policy = new ManagerPerformanceSettings(ManagerPerformanceProfile.Custom, null, process, presentation, stagger, stabilization).Resolve();
        Assert.Equal((process, presentation, stagger, stabilization), Timing(policy));
    }

    [Theory]
    [InlineData(0, 499)]
    [InlineData(0, 5001)]
    [InlineData(1, 249)]
    [InlineData(1, 2001)]
    [InlineData(2, 999)]
    [InlineData(2, 30001)]
    [InlineData(3, 19999)]
    [InlineData(3, 120001)]
    [InlineData(0, int.MinValue)]
    [InlineData(3, int.MaxValue)]
    public void InvalidCustomValuesAreRejectedEvenWhenTheirControlsAreDormant(int field, int value)
    {
        foreach (var profile in Enum.GetValues<ManagerPerformanceProfile>())
        {
            var settings = new ManagerPerformanceSettings(Profile: profile);
            settings = field switch
            {
                0 => settings with { CustomProcessSampleMs = value },
                1 => settings with { CustomPresentationMs = value },
                2 => settings with { CustomStaggerMs = value },
                _ => settings with { CustomStabilizationMs = value }
            };
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validated());
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Resolve());
            var policy = new ManagerPerformanceSettings().Resolve();
            policy = field switch
            {
                0 => policy with { ProcessSampleMs = value },
                1 => policy with { PresentationMs = value },
                2 => policy with { StaggerMs = value },
                _ => policy with { StabilizationMs = value }
            };
            Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validated());
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void UndefinedProfilesCannotResolveOrEnterDirectPolicyConsumers(int value)
    {
        var settings = new ManagerPerformanceSettings(Profile: (ManagerPerformanceProfile)value);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Resolve());
        var policy = new ManagerPerformanceSettings().Resolve() with { Profile = (ManagerPerformanceProfile)value };
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validated());
    }

    [Fact]
    public void EmptyMainIdentityIsInvalidEvenInModesThatDoNotUseIt()
    {
        foreach (var profile in Enum.GetValues<ManagerPerformanceProfile>())
            Assert.Throws<ArgumentException>(() => new ManagerPerformanceSettings(Profile: profile, MainAccountProfileId: Guid.Empty).Validated());
    }

    [Fact]
    public void MissingMainAccountIsExplicitAndNeverInventsARecipientOrPriorityBoost()
    {
        var policy = new ManagerPerformanceSettings(Profile: ManagerPerformanceProfile.MainAccountPriority).Resolve();
        Assert.True(policy.NeedsMainAccount);
        Assert.Null(policy.MainAccountProfileId);
        Assert.Equal(0, policy.MainAccountPriorityBoost);
        Assert.Equal(0, policy.GetPriorityBoost(Main));
        Assert.Contains("Select a main saved account", policy.Explanation);
    }

    [Fact]
    public void MainPriorityIsFiniteAndTargetsOnlyTheExplicitSavedProfile()
    {
        var policy = new ManagerPerformanceSettings(Profile: ManagerPerformanceProfile.MainAccountPriority, MainAccountProfileId: Main).Resolve();
        Assert.False(policy.NeedsMainAccount);
        Assert.Equal(Main, policy.MainAccountProfileId);
        Assert.Equal(100, policy.MainAccountPriorityBoost);
        Assert.Equal(100, policy.GetPriorityBoost(Main));
        Assert.Equal(0, policy.GetPriorityBoost(Guid.NewGuid()));
        Assert.Equal(0, policy.GetPriorityBoost(Guid.Empty));
        Assert.InRange(policy.MainAccountPriorityBoost, 0, 100);
        Assert.Contains("launch-order", policy.Explanation);
        Assert.Contains("queue aging", policy.Explanation);
        // This bounds the policy contribution. Actual non-starvation belongs to scheduler tests.
    }

    [Fact]
    public void OtherProfilesKeepTheSavedMainSelectionDormant()
    {
        foreach (var profile in Enum.GetValues<ManagerPerformanceProfile>().Where(value => value != ManagerPerformanceProfile.MainAccountPriority))
        {
            var settings = new ManagerPerformanceSettings(Profile: profile, MainAccountProfileId: Main);
            var policy = settings.Resolve();
            Assert.Equal(Main, settings.MainAccountProfileId);
            Assert.Null(policy.MainAccountProfileId);
            Assert.Equal(0, policy.GetPriorityBoost(Main));
            Assert.False(policy.NeedsMainAccount);
        }
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("missing-boost")]
    [InlineData("partial-boost")]
    [InlineData("wrong-mode")]
    [InlineData("dormant-id")]
    [InlineData("empty-id")]
    [InlineData("negative-boost")]
    [InlineData("excess-boost")]
    public void DirectPolicyValidationRejectsIncoherentMainPriority(string invalid)
    {
        var valid = new ManagerPerformanceSettings(Profile: ManagerPerformanceProfile.MainAccountPriority, MainAccountProfileId: Main).Resolve();
        var policy = invalid switch
        {
            "missing-id" => valid with { MainAccountProfileId = null },
            "missing-boost" => valid with { MainAccountPriorityBoost = 0 },
            "partial-boost" => valid with { MainAccountPriorityBoost = 50 },
            "wrong-mode" => valid with { Profile = ManagerPerformanceProfile.Balanced },
            "dormant-id" => valid with { Profile = ManagerPerformanceProfile.Custom, MainAccountPriorityBoost = 0 },
            "empty-id" => valid with { MainAccountProfileId = Guid.Empty },
            "negative-boost" => valid with { MainAccountPriorityBoost = -1 },
            _ => valid with { MainAccountPriorityBoost = 101 }
        };
        Assert.Throws<ArgumentException>(() => policy.Validated());
        Assert.Throws<ArgumentException>(() => policy.GetPriorityBoost(Main));
    }

    [Fact]
    public void PresetsDoNotChangeEssentialCadenceSampleExpiryOrMemoryHeadroom()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sample = new ResourceSnapshot(now, 32 * CapacityPolicy.GiB, 16 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 16 * CapacityPolicy.GiB, 30);
        var capacity = new CapacitySettings(SparePhysicalPercent: 25, SparePhysicalGiB: 4, SpareCommitPercent: 15, SpareCommitGiB: 3);
        var before = CapacityPolicy.Evaluate(sample, capacity, [], 0, now);
        foreach (var profile in Enum.GetValues<ManagerPerformanceProfile>())
        {
            var policy = new ManagerPerformanceSettings(Profile: profile, CustomProcessSampleMs: 5000).Resolve();
            Assert.Equal(1000, policy.EssentialResourceSampleMs);
            Assert.Equal(3000, policy.ResourceSampleExpiryMs);
            Assert.Equal(before, CapacityPolicy.Evaluate(sample, capacity, [], 0, now));
            var stale = CapacityPolicy.Evaluate(sample, capacity, [], 0, now.AddMilliseconds(policy.ResourceSampleExpiryMs + 1));
            Assert.False(stale.SamplesHealthy);
            Assert.Equal(0, stale.MemoryOnlySlots);
        }
        Assert.Equal(new CapacitySettings(SparePhysicalPercent: 25, SparePhysicalGiB: 4, SpareCommitPercent: 15, SpareCommitGiB: 3), capacity);
    }

    [Fact]
    public void ResourceSampleExpiryIsOneSharedBoundary()
    {
        var expiry = CapacityPolicy.ResourceSampleExpiry;
        Assert.Equal(TimeSpan.FromSeconds(1), CapacityPolicy.EssentialResourceSampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), expiry);
        Assert.Equal(expiry, TelemetryHistory.MaximumSystemAge);
        foreach (var profile in Enum.GetValues<ManagerPerformanceProfile>())
        {
            // The figures the Performance page shows are the enforced ones.
            var policy = new ManagerPerformanceSettings(Profile: profile).Resolve();
            Assert.Equal((int)CapacityPolicy.EssentialResourceSampleInterval.TotalMilliseconds, policy.EssentialResourceSampleMs);
            Assert.Equal((int)expiry.TotalMilliseconds, policy.ResourceSampleExpiryMs);
            Assert.Contains(" Essential RAM, commit and CPU safety sampling stays at 1000 ms with 3000 ms expiry. Resource headroom stays configured separately.", policy.Explanation);
        }
        // Each consumer of one essential sample agrees at the boundary and one tick past it.
        var observed = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sample = new ResourceSnapshot(observed, 32 * CapacityPolicy.GiB, 16 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 16 * CapacityPolicy.GiB, 30);
        var capacity = new CapacitySettings();
        DateTimeOffset boundary = observed + expiry, past = boundary.AddTicks(1);

        Assert.True(CapacityPolicy.Evaluate(sample, capacity, [], 0, boundary).SamplesHealthy);
        Assert.False(CapacityPolicy.Evaluate(sample, capacity, [], 0, past).SamplesHealthy);

        Assert.NotEqual("Unavailable", OverviewWorkspaceProjection.Create(new(Desktop: new(sample, [])), boundary).Physical);
        Assert.Equal("Unavailable", OverviewWorkspaceProjection.Create(new(Desktop: new(sample, [])), past).Physical);

        // Process inventory observed later stays fresh at both instants, so only the resource sample ages out.
        var desktop = new DesktopSnapshot(sample, [], ProcessObservedUtc: boundary, ProcessRevision: 1, ProcessInventoryComplete: true);
        var queue = new LaunchQueueSnapshot(false, []);
        var fresh = LaunchReservationProjection.Evaluate(queue, desktop, capacity, boundary);
        var stale = LaunchReservationProjection.Evaluate(queue, desktop, capacity, past);
        Assert.True(fresh.FreshProcessInventory); Assert.True(stale.FreshProcessInventory);
        Assert.StartsWith("Only unique exact process receipts", fresh.Explanation);
        Assert.StartsWith("Full launch forecasts retained", stale.Explanation);

        var systemOnly = new DesktopSnapshot(sample, [], Problem: "System sample only.");
        Assert.True(new TelemetryHistory().Observe(systemOnly, boundary));
        Assert.False(new TelemetryHistory().Observe(systemOnly, past));

        // Consecutive samples one expiry apart stay continuous; one tick more is a discontinuity that holds admission.
        PressureDecision Second(DateTimeOffset at)
        {
            var tracker = new ResourcePressureTracker();
            Assert.False(tracker.Observe(sample, CapacityPolicy.Evaluate(sample, capacity, [], 0, observed), observed).Held);
            var next = sample with { ObservedUtc = at };
            return tracker.Observe(next, CapacityPolicy.Evaluate(next, capacity, [], 0, at), at);
        }
        Assert.False(Second(boundary).Held);
        Assert.True(Second(past).Held);
    }

    [Fact]
    public void SettingsEditsAndPolicyCopiesDoNotMutateAnAlreadyResolvedSnapshot()
    {
        var settings = new ManagerPerformanceSettings();
        var policy = settings.Resolve();
        var edited = settings with { Profile = ManagerPerformanceProfile.Custom, CustomProcessSampleMs = 5000, CustomStaggerMs = 30000 };
        var changed = edited.Resolve();
        Assert.Equal(ManagerPerformanceProfile.Balanced, settings.Profile);
        Assert.Equal((1000, 1000, 2000, 20000), Timing(policy));
        Assert.Equal((5000, 1000, 30000, 20000), Timing(changed));
        var copied = changed with { PresentationMs = 250 };
        Assert.Equal(1000, changed.PresentationMs);
        Assert.Equal(250, copied.Validated().PresentationMs);
    }

    private static (int, int, int, int) Timing(ManagerPerformancePolicy policy) => (policy.ProcessSampleMs, policy.PresentationMs, policy.StaggerMs, policy.StabilizationMs);
}
