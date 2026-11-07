using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.Sessions.Capabilities.Infrastructure;

namespace Zenith.Sessions.Capabilities;

public sealed record SourceEvidence(string RelativePath, string Sha256);
public sealed record DocumentationReference(string Title, string Url, string Scope);
public sealed record FeasibilityReport(int SchemaVersion, DateTimeOffset ObservedAtUtc,
    EnvironmentObservation Environment, IReadOnlyList<SourceEvidence> Source,
    IReadOnlyList<DocumentationReference> Documentation, IReadOnlyList<BoundaryResult> ControlledTests,
    IReadOnlyList<CapabilityAssessment> Capabilities, GateDecision FeasibilityDecision, NativeProbeReport? NativeProbe = null)
{
    public string Stage => FeasibilityDecision.CanProceed ? "Feasibility gates passed; implementation may proceed" : "Feasibility only; full companion implementation blocked";
    public bool ReleaseReady => false; // Product implementation and sixteen-client acceptance are outside this milestone.
    public string EvidenceLimit => NativeProbe is null
        ? "Core checks use invented targets and a memory sink. No native probe is attached. No live Roblox automation or reconnect was exercised."
        : "Core checks use invented targets and a memory sink. Attached native evidence tests only the collector's own Windows receivers and in-memory capture. No Roblox input, account authentication or reconnect was exercised.";
    public object Workload => new
    {
        TargetConcurrentClients = 16,
        Modes = new[] { "Independent shared-macro runs", "Explicit leader keyboard/click broadcast" },
        MouseCameraRequired = false, ArrowKeyCameraRequired = true, PerClientImageChecks = true,
        BroadcastRequiresStoppedMacros = true, MatchClientContentSize = true,
        Places = new[] { 6564161046L, 110829983956014L, 2512643572L, 85896571713843L }
    };
}

public static class Reports
{
    private static readonly string[] SourceFiles =
    [
        "src/MacroSuite.App/MacroSuite.App.csproj",
        "src/MacroSuite.Core/Input/PlaybackSessionContext.cs",
        "src/MacroSuite.Core/Abstractions/IClientRelativePlaybackTarget.cs",
        "src/MacroSuite.Core/Execution/MacroExecutor.cs",
        "src/MacroSuite.Core/Steps/ClickStep.cs",
        "src/MacroSuite.Core/Steps/MouseMoveStep.cs",
        "src/MacroSuite.Core/Execution/TargetWindowServiceBase.cs",
        "src/MacroSuite.Infrastructure/Input/Win32InputPlayer.cs",
        "src/MacroSuite.Infrastructure/Capture/WindowsGraphicsCaptureService.cs",
        "tools/Zenith.Sessions.CapabilityHarness/CapabilityGate.cs",
        "tools/Zenith.Sessions.CapabilityHarness/LiveEvidence.cs",
        "src/Zenith.Automation.Input/TargetedInput.cs",
        "src/Zenith.Automation.Input/TargetedInputCoordinator.cs",
        "src/Zenith.Automation.Input/PixelRectangle.cs",
        "src/Zenith.MacroHost.Execution/TargetedMacroPlan.cs",
        "src/Zenith.MacroHost.Execution/TargetedMacroPlayer.cs",
        "src/Zenith.MacroHost.Execution/TargetedMacroRunner.cs",
        // The companion backend and preview capture the independent-runs and occluded-image-checks reasons describe.
        "src/Zenith.MacroHost.Execution/CompanionMacroOperations.cs",
        "src/Zenith.Sessions.Windows/WindowsClientPreviewProvider.cs",
        "tools/Zenith.Sessions.NativeProbe/Program.cs",
        "tools/Zenith.Sessions.NativeProbe/Program.MacroExecution.cs",
        "tools/Zenith.Sessions.NativeProbe/Infrastructure/OwnedWindowMessageExecutor.cs",
        "tools/Zenith.Sessions.NativeProbe/Infrastructure/OwnedWindowCapture.cs"
    ];

    public static IReadOnlyList<DocumentationReference> Documentation { get; } = new[]
    {
        new DocumentationReference("Windows SendInput", "https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput",
            "Shared keyboard/mouse stream; no per-window input parameter."),
        new DocumentationReference("Roblox OAuth implementation", "https://create.roblox.com/docs/cloud/auth/oauth2-develop",
            "PKCE account linking and authorized API calls; does not establish a native-player sign-in/reconnect handoff."),
        new DocumentationReference("Roblox deep links", "https://create.roblox.com/docs/production/promotion/deeplinks",
            "Deprecated destination-link interface. userId is a user to follow, not the account to authenticate."),
        new DocumentationReference("Roblox share links", "https://create.roblox.com/docs/production/promotion/share-links",
            "Destination links are not proof of account selection or arrival."),
        new DocumentationReference("TeleportService", "https://create.roblox.com/docs/reference/engine/classes/TeleportService",
            "Server-side movement of connected players is separate from reconnecting a disconnected native client."),
        new DocumentationReference("Windows application-window capture", "https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture",
            "Candidate WGC application-surface capture; own-window evidence is not Roblox compatibility."),
        new DocumentationReference("Windows PostMessage", "https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew",
            "Queue delivery does not prove game input consumption or raw keyboard state."),
        new DocumentationReference("Exo Panda Multi", "https://exopanda.com/guides/multi",
            "Vendor describes foreground window swapping or sequential playback; no verified background integration."),
        new DocumentationReference("Bloxstrap multi-instance removal", "https://github.com/bloxstraplabs/bloxstrap/releases/tag/v2.10.0",
            "Release removes multi-instance launching; not an established account-selecting reconnect provider."),
        new DocumentationReference("Unrelated ex_panda repository", "https://github.com/am-kantox/ex_panda",
            "Elixir AST macro expansion; unrelated to Roblox Exo Panda.")
    };

    public static IReadOnlyList<SourceEvidence> AuditSource(string root)
    {
        var entries = new List<SourceEvidence>();
        foreach (string relative in SourceFiles)
        {
            string path = Path.Combine(root, relative);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 2 * 1024 * 1024)
                throw new InvalidDataException($"Missing or oversized required source file: {relative}.");
            using var stream = File.OpenRead(path);
            entries.Add(new(relative, Convert.ToHexString(SHA256.HashData(stream))));
        }
        return entries;
    }

    public static string Markdown(FeasibilityReport report)
    {
        var text = new StringBuilder("# Zenith Sessions feasibility report\n\n");
        text.AppendLine($"Observed UTC: {report.ObservedAtUtc:O}\n");
        text.AppendLine($"**Feasibility gate: {(report.FeasibilityDecision.CanProceed ? "PASSED" : "BLOCKED")}. Product release ready: false.** The full companion has not been implemented or installed; sixteen-client acceptance has not run.\n");
        text.AppendLine(report.EvidenceLimit + "\n");
        text.AppendLine("Target: sixteen authorized clients; two-account feasibility first. Shared macros and explicit key/click broadcasting; per-client image decisions; arrow-key camera only. Compatibility places: 6564161046, 110829983956014, 2512643572, 85896571713843.\n");
        if (report.NativeProbe is { } native)
        {
            text.AppendLine("## Native controlled probe (not a live Roblox exercise)\n");
            text.AppendLine($"Collector: {native.CollectorVersion}; UTC: {native.CompletedUtc:O}.\n");
            foreach (var check in native.Checks)
                text.AppendLine($"- {(check.Passed ? "PASS" : "NOT ESTABLISHED")}: {check.Name}. {check.Detail}");
            text.AppendLine("\n" + native.Limitation + "\n");
        }
        text.AppendLine("## Local discovery\n");
        text.AppendLine($"- OS: {report.Environment.OperatingSystem}; {report.Environment.Architecture}.");
        text.AppendLine($"- Runtime: {report.Environment.Runtime}.");
        text.AppendLine($"- Roblox process metadata entries: {report.Environment.Processes.Count}. These are not authenticated-account counts.");
        text.AppendLine($"- Installed Win32 client/launcher files observed: {report.Environment.ClientFiles.Count}.");
        text.AppendLine($"- Known Win32 logs directory exists: {report.Environment.Win32LogDirectoryPresent}. No log contents read.");
        foreach (var file in report.Environment.ClientFiles)
            text.AppendLine($"  - {file.VersionDirectory}/{file.Executable}: {file.FileVersion ?? "version unavailable"}.");
        foreach (var protocol in report.Environment.Protocols)
            text.AppendLine($"- Protocol {protocol.Scheme} ({protocol.Scope}): {(protocol.Problem != null ? "unknown: " + protocol.Problem : protocol.Registered ? "registration key observed; not launched" : "registration key absent")}.");
        foreach (string problem in report.Environment.Problems) text.AppendLine($"- Observation limitation: {problem}");
        text.AppendLine("\n## Capabilities\n");
        text.AppendLine("| Requirement | Implementation support | Live exercise | Explanation |\n|---|---|---|---|");
        foreach (var fact in report.Capabilities)
            text.AppendLine($"| {fact.Id} | {fact.Support} | {fact.Outcome} | {fact.Reason} |");
        text.AppendLine("\n## Controlled production-boundary tests\n");
        foreach (var test in report.ControlledTests)
            text.AppendLine($"- {(test.Passed ? "PASS" : "FAIL")}: {test.Name}. {test.Detail}");
        text.AppendLine("\nThese checks exercise the existing Core assembly with invented windows. They establish foreground restrictions and cancellation behavior; they do not prove live Roblox support.\n");
        text.AppendLine("## Required before continuing\n");
        text.AppendLine("A supported provider must independently demonstrate every required live check on the same provider configuration and client version, including background input isolation, unobscured images and correct-account reconnect. Installed files, multiple PIDs, OAuth grants, URL dispatch, simulated tests and server teleport commands cannot substitute for that proof.\n");
        text.AppendLine("If no such provider can be established within the approved design, stop here. No foreground-switching substitute or client-enforcement workaround is part of this implementation.\n");
        text.AppendLine("## Sources and audit fingerprint\n");
        foreach (var source in report.Source) text.AppendLine($"- `{source.RelativePath}` — SHA-256 `{source.Sha256}`");
        foreach (var reference in report.Documentation) text.AppendLine($"- [{reference.Title}]({reference.Url}): {reference.Scope}");
        return text.ToString();
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task WriteAtomicAsync(string path, string contents, CancellationToken ct)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(contents), ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
