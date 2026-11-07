using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Capabilities;

namespace Zenith.Sessions.CapabilityHarness.Tests;

public sealed class EvidenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "zenith-evidence-test-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private async Task<CapabilityAssessment[]> Fixture()
    {
        var artifact = await VerifiedArtifact.PersistAsync(_folder, "synthetic.json",
            Encoding.UTF8.GetBytes("{\"syntheticPolicyFixture\":true,\"notLiveEvidence\":true}"), default);
        var clients = new[]
        {
            new VerifiedClientInstance("fixture", 100, _now.AddHours(-1), "fixture-window-a", 1, "synthetic-binding-a"),
            new VerifiedClientInstance("fixture", 200, _now.AddHours(-1), "fixture-window-b", 2, "synthetic-binding-b")
        };
        var proof = new LiveExerciseEvidence(new string('A', 64), "input-fixture/1", "capture-fixture/1", "launch-fixture/1",
            _now.AddMinutes(-1), _now, 3, clients, [artifact], true, 3);
        return CapabilityGate.Requirements.Select(r => new CapabilityAssessment(r.Id, CapabilitySupport.Supported,
            EvidenceOrigin.LiveRobloxExercise, ExerciseOutcome.Passed, "Synthetic policy fixture only", "fixture-suite", "fixture-client",
            _now, proof)).ToArray();
    }

    [Fact]
    public async Task CompleteCollectorContractCanSatisfyPolicyInSyntheticFixture()
        => Assert.True(CapabilityGate.Evaluate(await Fixture(), _now).CanProceed);

    [Theory]
    [InlineData(-25)]
    [InlineData(2)]
    public async Task StaleOrFutureProofIsRejected(int hours)
    {
        var facts = await Fixture();
        var time = _now.AddHours(hours);
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f with
        { ObservedAtUtc = time, Evidence = f.Evidence! with { StartedUtc = time.AddMinutes(-1), CompletedUtc = time } }), _now).CanProceed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OneOffSuccessDoesNotSatisfyRepeatedExercise(int trials)
    {
        var facts = await Fixture();
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f with
        { Evidence = f.Evidence! with { SuccessfulTrials = trials } }), _now).CanProceed);
    }

    [Fact]
    public async Task UnverifiedOutcomesAndMissingAdaptersCannotPass()
    {
        var facts = await Fixture();
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f with
        { Evidence = f.Evidence! with { ObservedOutcomeVerified = false } }), _now).CanProceed);
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f with
        { Evidence = f.Evidence! with { LaunchAdapterVersion = "" } }), _now).CanProceed);
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f with
        { Evidence = f.Evidence! with { AttemptedTrials = 4 } }), _now).CanProceed);
    }

    [Fact]
    public async Task SameAccountTwiceAndReusedProcessCannotEstablishIndependentAccounts()
    {
        var facts = await Fixture();
        var clients = facts[0].Evidence!.Clients;
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f with
        { Evidence = f.Evidence! with { Clients = [clients[0], clients[1] with { VerifiedUserId = clients[0].VerifiedUserId }] } }), _now).CanProceed);
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f with
        { Evidence = f.Evidence! with { Clients = [clients[0], clients[0] with { VerifiedUserId = clients[1].VerifiedUserId }] } }), _now).CanProceed);
    }

    [Fact]
    public async Task ConfigurationFingerprintMustMatchAcrossExercises()
    {
        var facts = await Fixture();
        facts[0] = facts[0] with { Evidence = facts[0].Evidence! with { SuiteFingerprint = new string('B', 64) } };
        Assert.False(CapabilityGate.Evaluate(facts, _now).CanProceed);
    }

    [Fact]
    public async Task ChangedOrMissingArtifactInvalidatesEvidence()
    {
        var facts = await Fixture();
        string path = Path.Combine(_folder, facts[0].Evidence!.Artifacts[0].Name);
        await File.WriteAllTextAsync(path, "altered");
        Assert.False(CapabilityGate.Evaluate(facts, _now).CanProceed);
        File.Delete(path);
        Assert.False(CapabilityGate.Evaluate(facts, _now).CanProceed);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("subdir/evidence.json")]
    [InlineData("evidence:stream")]
    public async Task CollectorRejectsPathsInsteadOfSimpleArtifactNames(string name)
        => await Assert.ThrowsAsync<ArgumentException>(() => VerifiedArtifact.PersistAsync(_folder, name, new byte[] { 1 }, default));

    [Fact]
    public async Task CancelledAndOversizedWritesDoNotReplaceEvidence()
    {
        var facts = await Fixture();
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifiedArtifact.PersistAsync(_folder, "cancelled", new byte[] { 1 }, stop.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => VerifiedArtifact.PersistAsync(_folder, "large", new byte[4 * 1024 * 1024 + 1], default));
        Assert.True(CapabilityGate.Evaluate(facts, _now).CanProceed);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData("zenith-native-probe/0.1.0")]
    [InlineData("zenith-native-probe/0.2.0")]
    [InlineData("zenith-native-probe/0.3.0")]
    public async Task NativeCollectorEvidenceRetainsItsOriginAndCannotBecomeLiveEvidence(string collector)
    {
        var report = new NativeProbeReport(1, collector, EvidenceOrigin.NativeControlledExercise,
            _now.AddSeconds(-1), _now, true, [new("fixture", true, "Synthetic fixture")], "Own windows only");
        string path = Path.Combine(_folder, "native.json");
        await Reports.WriteAtomicAsync(path, JsonSerializer.Serialize(report, Reports.JsonOptions), default);
        Assert.Equal(EvidenceOrigin.NativeControlledExercise, (await NativeEvidence.ReadAsync(path, default)).Origin);
        await Reports.WriteAtomicAsync(path, JsonSerializer.Serialize(report with { Origin = EvidenceOrigin.LiveRobloxExercise }, Reports.JsonOptions), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeEvidence.ReadAsync(path, default));
    }

    [Theory]
    [InlineData("zenith-native-probe/0.4.0", "Own windows only")]
    [InlineData("zenith-native-probe/0.2.0", "")]
    public async Task NativeReaderRejectsUnknownCollectorsAndMissingLimitations(string collector, string limitation)
    {
        var report = new NativeProbeReport(1, collector, EvidenceOrigin.NativeControlledExercise,
            _now.AddSeconds(-1), _now, true, [new("fixture", true, "Synthetic fixture")], limitation);
        string path = Path.Combine(_folder, "native.json");
        await Reports.WriteAtomicAsync(path, JsonSerializer.Serialize(report, Reports.JsonOptions), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeEvidence.ReadAsync(path, default));
    }

    [Fact]
    public async Task NativeReaderRejectsNullChecksUnknownFieldsAndOversizedReports()
    {
        string path = Path.Combine(_folder, "native.json");
        await Reports.WriteAtomicAsync(path, "{\"UnknownOverride\":true}", default);
        await Assert.ThrowsAsync<JsonException>(() => NativeEvidence.ReadAsync(path, default));
        var report = new NativeProbeReport(1, "zenith-native-probe/0.1.0", EvidenceOrigin.NativeControlledExercise,
            _now.AddSeconds(-1), _now, true, [null!], "Own windows only");
        await Reports.WriteAtomicAsync(path, JsonSerializer.Serialize(report, Reports.JsonOptions), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeEvidence.ReadAsync(path, default));
        await File.WriteAllBytesAsync(path, new byte[65537]);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeEvidence.ReadAsync(path, default));
    }

    [Fact]
    public void GateMatchesApprovedKeyboardCameraAndBothAutomationModes()
    {
        Assert.DoesNotContain(CapabilityGate.Requirements, r => r.Id == "background-pointer");
        Assert.Contains(CapabilityGate.Requirements, r => r.Id == "arrow-key-camera");
        Assert.Contains(CapabilityGate.Requirements, r => r.Id == "live-broadcast");
        Assert.Contains(CapabilityGate.Requirements, r => r.Id == "independent-runs");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_folder)) return;
        // Only a newly-created, flat, test-owned directory; never recursively delete a computed tree.
        foreach (string file in Directory.GetFiles(_folder)) File.Delete(file);
        Directory.Delete(_folder, recursive: false);
    }
}
