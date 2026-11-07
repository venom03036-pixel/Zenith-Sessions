namespace Zenith.Sessions.Capabilities;

public enum CapabilitySupport { Supported, Unverified, Unavailable }
public enum EvidenceOrigin { SourceAudit, OfficialDocumentation, ControlledBoundaryTest, LiveRobloxExercise, NativeControlledExercise }
public enum ExerciseOutcome { NotRun, Passed, Failed }

public sealed record CapabilityAssessment(string Id, CapabilitySupport Support, EvidenceOrigin Origin,
    ExerciseOutcome Outcome, string Reason, string? ProviderId = null, string? ClientVersion = null,
    DateTimeOffset? ObservedAtUtc = null, LiveExerciseEvidence? Evidence = null);
public sealed record Requirement(string Id, string Gate, string Description);
public sealed record GateDecision(bool CanProceed, IReadOnlyList<string> Blockers);

/// <summary>Passing a simulated or source-level check never authorizes a live-client release.</summary>
public static class CapabilityGate
{
    public static IReadOnlyList<Requirement> Requirements { get; } = Array.AsReadOnly(new[]
    {
        new Requirement("independent-accounts", "Shared", "Multiple independently authenticated eligible clients"),
        new Requirement("background-keyboard", "Background macros", "Unfocused key presses and balanced holds"),
        new Requirement("background-clicks", "Background macros", "Unfocused mouse buttons and balanced holds"),
        new Requirement("arrow-key-camera", "Background macros", "Arrow-key camera control; mouse-camera motion excluded"),
        new Requirement("occluded-image-checks", "Background macros", "Fresh target-specific images behind overlapping windows"),
        new Requirement("input-isolation", "Background macros", "No input reaches another client or the user's foreground app"),
        new Requirement("target-lifecycle", "Background macros", "Move, resize, restart and PID-reuse validation"),
        new Requirement("emergency-cancellation", "Background macros", "No stale input after Stop/Panic; owned holds released"),
        new Requirement("independent-runs", "Background macros", "Shared macro revision with independent image/condition/run state"),
        new Requirement("live-broadcast", "Background macros", "Explicit leader/group key and click fan-out without feedback or competing macros"),
        new Requirement("confirmed-disconnect", "Automatic reconnect", "Distinguish disconnect, authentication failure and deliberate close"),
        new Requirement("account-reconnect", "Automatic reconnect", "Unattended reconnect of the specified authorized account"),
        new Requirement("reconnect-identity", "Automatic reconnect", "Confirm the account after reconnect"),
        new Requirement("reconnect-arrival", "Automatic reconnect", "Confirm destination arrival and allowed same-game fallback")
    });

    public static GateDecision Evaluate(IEnumerable<CapabilityAssessment> assessments, DateTimeOffset? now = null)
    {
        var facts = assessments.ToArray();
        var blockers = new List<string>();
        var observedNow = now ?? DateTimeOffset.UtcNow;
        if (facts.Any(f => !Requirements.Any(r => r.Id == f.Id)))
            blockers.Add("Unknown requirement IDs are not accepted by this gate version.");
        foreach (Requirement required in Requirements)
        {
            var matches = facts.Where(f => f.Id == required.Id).ToArray();
            if (matches.Length != 1)
            {
                blockers.Add($"{required.Id}: expected exactly one assessment; found {matches.Length}.");
                continue;
            }
            var fact = matches[0];
            if (fact.Support != CapabilitySupport.Supported || fact.Outcome != ExerciseOutcome.Passed
                || fact.Origin != EvidenceOrigin.LiveRobloxExercise
                || string.IsNullOrWhiteSpace(fact.ProviderId) || string.IsNullOrWhiteSpace(fact.ClientVersion)
                || fact.ObservedAtUtc is null)
                blockers.Add($"{required.Id}: {fact.Support}/{fact.Outcome}. {fact.Reason}");
            else
                foreach (string problem in LiveEvidencePolicy.Validate(fact, observedNow))
                    blockers.Add($"{required.Id}: {problem}");
        }
        // A provider/client mixture is not evidence of one end-to-end implementation.
        if (blockers.Count == 0 && facts.Where(f => Requirements.Any(r => r.Id == f.Id))
            .Select(f => (f.ProviderId, f.ClientVersion, f.Evidence?.SuiteFingerprint)).Distinct().Count() != 1)
            blockers.Add("The required live exercises must identify the same provider configuration and client version.");
        return new(blockers.Count == 0, blockers.AsReadOnly());
    }

    public static IReadOnlyList<CapabilityAssessment> AssessExistingImplementation()
        => Requirements.Select(r => new CapabilityAssessment(r.Id,
            r.Id is "background-keyboard" or "background-clicks" or "arrow-key-camera" or "occluded-image-checks" or "independent-runs" or "live-broadcast"
                ? CapabilitySupport.Unavailable : CapabilitySupport.Unverified,
            EvidenceOrigin.SourceAudit, ExerciseOutcome.NotRun, r.Id switch
            {
                "background-keyboard" or "background-clicks" or "arrow-key-camera" =>
                    "The existing pinned-target playback session requires foreground focus. No background executor is implemented.",
                "occluded-image-checks" =>
                    "The macro capture service uses desktop duplication/GDI regions, not a proven unobscured per-window image source. Per-window WGC capture is used only by opt-in client previews and the own-window probe, not by macro image or condition checks.",
                "account-reconnect" =>
                    "No account-selecting native reconnect provider is implemented. OAuth authorization and a launch URL do not prove this capability.",
                "independent-accounts" =>
                    "Process/window discovery does not establish independent authentication or permitted multi-client capacity.",
                "independent-runs" => "A host-local executor keeps separate run state per recipient, but only for registered diagnostic targets. Binding it to Roblox targets and per-target image/condition evaluation are not implemented.",
                "live-broadcast" => "No verified leader/follower background input provider is implemented. Focus swapping cannot satisfy this requirement.",
                _ => "No supported provider is available to run and verify this live Roblox exercise."
            })).ToArray();
}
