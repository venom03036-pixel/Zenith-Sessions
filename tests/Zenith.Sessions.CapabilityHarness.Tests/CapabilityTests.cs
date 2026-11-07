using System.Text.Json;
using Xunit;
using Zenith.Sessions.Capabilities;

namespace Zenith.Sessions.CapabilityHarness.Tests;

public sealed class CapabilityTests
{
    // Synthetic evidence validates the gate policy only; it must never become a live-test report.
    private static CapabilityAssessment[] SyntheticCompleteEvidence() => CapabilityGate.Requirements.Select(r =>
        new CapabilityAssessment(r.Id, CapabilitySupport.Supported, EvidenceOrigin.LiveRobloxExercise,
            ExerciseOutcome.Passed, "Synthetic test fixture, never live evidence.", "test-provider", "test-version",
            DateTimeOffset.UtcNow)).ToArray();

    [Fact]
    public void MissingOrEmptyEvidenceCannotPass()
    {
        Assert.False(CapabilityGate.Evaluate([]).CanProceed);
        var incomplete = SyntheticCompleteEvidence().Skip(1);
        Assert.Contains(CapabilityGate.Evaluate(incomplete).Blockers, b => b.StartsWith("independent-accounts:"));
    }

    [Fact]
    public void StatusLabelsWithoutCollectorArtifactsCannotApproveRelease()
        => Assert.False(CapabilityGate.Evaluate(SyntheticCompleteEvidence()).CanProceed);

    [Theory]
    [InlineData(EvidenceOrigin.ControlledBoundaryTest)]
    [InlineData(EvidenceOrigin.SourceAudit)]
    [InlineData(EvidenceOrigin.OfficialDocumentation)]
    [InlineData(EvidenceOrigin.NativeControlledExercise)]
    public void SimulatedSourceAndDocumentEvidenceCannotApproveLiveRelease(EvidenceOrigin origin)
    {
        var facts = SyntheticCompleteEvidence().Select(f => f with { Origin = origin });
        Assert.False(CapabilityGate.Evaluate(facts).CanProceed);
    }

    [Fact]
    public void DuplicatePassedEvidenceCannotHideAFailure()
    {
        var facts = SyntheticCompleteEvidence();
        var duplicate = facts[0] with { Outcome = ExerciseOutcome.Failed };
        Assert.False(CapabilityGate.Evaluate(facts.Append(duplicate)).CanProceed);
    }

    [Fact]
    public void UnsupportedOrNotRunCapabilityCannotPass()
    {
        var facts = SyntheticCompleteEvidence();
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f.Id == "account-reconnect"
            ? f with { Support = CapabilitySupport.Unverified } : f)).CanProceed);
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f.Id == "input-isolation"
            ? f with { Outcome = ExerciseOutcome.NotRun } : f)).CanProceed);
    }

    [Fact]
    public void ProviderVersionAndObservationAreRequired()
    {
        var facts = SyntheticCompleteEvidence();
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f with { ProviderId = null })).CanProceed);
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f with { ClientVersion = "" })).CanProceed);
        Assert.False(CapabilityGate.Evaluate(facts.Select(f => f with { ObservedAtUtc = null })).CanProceed);
        Assert.False(CapabilityGate.Evaluate(facts.Select((f, i) => i == 0 ? f with { ClientVersion = "different" } : f)).CanProceed);
    }

    [Fact]
    public void ExistingImplementationDoesNotClaimAnyLiveTestPassed()
    {
        var facts = CapabilityGate.AssessExistingImplementation();
        Assert.False(CapabilityGate.Evaluate(facts).CanProceed);
        Assert.All(facts, f => Assert.Equal(ExerciseOutcome.NotRun, f.Outcome));
        Assert.Contains(facts, f => f.Id == "background-keyboard" && f.Support == CapabilitySupport.Unavailable);
        Assert.Contains(facts, f => f.Id == "account-reconnect" && f.Support == CapabilitySupport.Unverified);
    }

    [Fact]
    public async Task RealCoreRejectsUnfocusedInputAndStaleIdentityWithMemorySink()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var results = await BoundaryProbe.RunAsync(deadline.Token);
        Assert.Equal(7, results.Count);
        Assert.All(results, result => Assert.True(result.Passed, result.Name + ": " + result.Detail));
        Assert.All(results, result => Assert.Equal(EvidenceOrigin.ControlledBoundaryTest, result.Origin));
    }

    [Fact]
    public async Task CancelledProbeDoesNotLeaveWorkRunning()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundaryProbe.RunAsync(cancellation.Token));
    }

    [Fact]
    public async Task AtomicReportReplacementAndCancellationPreserveLastValidFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zenith-capability-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "report.json");
        try
        {
            await Reports.WriteAtomicAsync(path, "{\"revision\":1}", default);
            await Reports.WriteAtomicAsync(path, "{\"revision\":2}", default);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reports.WriteAtomicAsync(path, "partial", stop.Token));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(2, json.RootElement.GetProperty("revision").GetInt32());
            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            // Only this test's literal, freshly created directory is owned here.
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: false);
        }
    }
}
