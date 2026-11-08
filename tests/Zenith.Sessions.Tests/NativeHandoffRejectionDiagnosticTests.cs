using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class NativeHandoffRejectionDiagnosticTests
{
    [Theory]
    [InlineData("outer-name", "UnknownOuterField")]
    [InlineData("outer-encoding", "OuterEncoding")]
    [InlineData("outer-duplicate", "DuplicateOuterField")]
    [InlineData("nested-name", "UnknownDestinationField")]
    [InlineData("nested-encoding", "DestinationEncoding")]
    [InlineData("tracker", "TrackerShape")]
    public void RejectedSyntheticRequestReportsItsFirstFixedGuardWithoutEchoingInput(string mutation, string expected)
    {
        using var context = SyntheticHandoff.Context(); var clock = new HandoffClock();
        const string marker = "private-synthetic-marker";
        string request = SyntheticHandoff.Uri(context, clock, destination => mutation switch
        {
            "nested-name" => destination.Replace("joinAttemptOrigin=PlayButton", marker + "=" + marker),
            "nested-encoding" => destination.Replace("joinAttemptOrigin=PlayButton", "joinAttemptOrigin=%ZZ"),
            _ => destination
        });
        request = mutation switch
        {
            "outer-name" => request.Replace("LaunchExp:InApp", marker + ":" + marker),
            "outer-encoding" => request.Replace("LaunchExp:InApp", "LaunchExp:%ZZ"),
            "outer-duplicate" => request.Replace("LaunchExp:InApp", "launchmode:play"),
            "tracker" => request.Replace("browsertrackerid:1234567890", "browsertrackerid:0"),
            _ => request
        };
        var result = NativeRobloxHandoff.Capture(context, "https://www.roblox.com/games/1818",
            RobloxHandoffProbePolicy.OfficialOrigin, request, clock);
        Assert.Equal(NativeHandoffCaptureStatus.InvalidShape, result.Status); Assert.Null(result.Candidate);
        string serialized = JsonSerializer.Serialize(result, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
        using var json = JsonDocument.Parse(serialized);
        Assert.True(json.RootElement.TryGetProperty("RejectionReason", out var reason), "Rejected input must report its fixed guard.");
        Assert.Equal(expected, reason.GetString());
        foreach (string sensitive in new[] { marker, SyntheticHandoff.Ticket, "gameinfo", "roblox-player:", request })
            Assert.DoesNotContain(sensitive, serialized + result);
        // A rejection still cannot consume the operation's one valid candidate.
        using var accepted = SyntheticHandoff.Capture(context, clock);
    }

    [Theory]
    [InlineData("referredByPlayerId", NativeHandoffRejectionReason.UnsupportedReferrerField)]
    [InlineData("launchData", NativeHandoffRejectionReason.UnsupportedLaunchDataField)]
    [InlineData("eventId", NativeHandoffRejectionReason.UnsupportedEventField)]
    public void KnownSourceExamplesRemainRejectedWithoutRecordingTheirValues(string name, NativeHandoffRejectionReason reason)
    {
        using var context = SyntheticHandoff.Context(); var clock = new HandoffClock();
        string request = SyntheticHandoff.Uri(context, clock, value => value + "&" + name + "=private-synthetic-marker");
        var result = NativeRobloxHandoff.Capture(context, "https://www.roblox.com/games/1818", RobloxHandoffProbePolicy.OfficialOrigin, request, clock);
        Assert.Equal(NativeHandoffCaptureStatus.InvalidShape, result.Status); Assert.Null(result.Candidate);
        Assert.Equal(reason, result.RejectionReason);
        Assert.DoesNotContain("private-synthetic-marker", JsonSerializer.Serialize(result) + result);
    }

    [Fact]
    public void EarlierGuardsStillWinAndAcceptedRequestsCarryNoRejection()
    {
        using var context = SyntheticHandoff.Context(); var clock = new HandoffClock();
        string request = SyntheticHandoff.Uri(context, clock).Replace("LaunchExp:InApp", "launchmode:%ZZ");
        // Existing evaluation decodes even a duplicate field before TryAdd; retain that order.
        var encodedDuplicate = NativeRobloxHandoff.AcceptBuilt(context, request, clock);
        Assert.Equal(NativeHandoffCaptureStatus.InvalidShape, encodedDuplicate.Status);
        Assert.Equal(NativeHandoffRejectionReason.OuterEncoding, encodedDuplicate.RejectionReason);
        var tooMany = NativeRobloxHandoff.AcceptBuilt(context, request + "+unknown:one+other:two", clock);
        Assert.Equal(NativeHandoffCaptureStatus.InvalidShape, tooMany.Status);
        Assert.Equal(NativeHandoffRejectionReason.OuterFieldCount, tooMany.RejectionReason);
        var accepted = NativeRobloxHandoff.AcceptBuilt(context, SyntheticHandoff.Uri(context, clock), clock);
        using var candidate = accepted.Candidate;
        Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar, accepted.Status); Assert.NotNull(candidate);
        Assert.Null(accepted.RejectionReason);
    }

    [Fact]
    public void LegacySnapshotAndFactsWithoutOptionalReasonRemainReadable()
    {
        var source = NativeLaunchDiagnosticPresentationTests.Snapshot(Guid.NewGuid()) with { ShapeStatus = NativeHandoffCaptureStatus.InvalidShape };
        var json = JsonNode.Parse(JsonSerializer.Serialize(source))!; json.AsObject().Remove("RejectionReason");
        var restored = JsonSerializer.Deserialize<NativeLaunchDiagnosticSnapshot>(json.ToJsonString())!.Validated();
        Assert.Null(restored.RejectionReason); Assert.Equal(source, restored);
        Assert.Equal("Handoff shape: invalid structure.", NativeLaunchDiagnosticPresentation.Project(source.OperationId, restored).Shape);
        var facts = JsonSerializer.Deserialize<NativeBrowserPreparationFacts>("{\"Outcome\":0}")!.Validated();
        Assert.Null(facts.RejectionReason);
    }

    [Theory]
    [InlineData(null, NativeHandoffRejectionReason.OuterEncoding)]
    [InlineData(NativeHandoffCaptureStatus.AcceptedSourceGrammar, NativeHandoffRejectionReason.OuterEncoding)]
    [InlineData(NativeHandoffCaptureStatus.InvalidShape, (NativeHandoffRejectionReason)999)]
    public void InvalidOrAcceptedPairedReasonsCannotEnterTypedDiagnostics(NativeHandoffCaptureStatus? status, NativeHandoffRejectionReason reason)
    {
        Assert.Throws<ArgumentException>(() => new NativeBrowserCapture(RobloxHandoffProbeOutcome.MalformedHandoff, shapeStatus: status, rejectionReason: reason));
        Assert.Throws<ArgumentException>(() => new NativeBrowserPreparationFacts(RobloxHandoffProbeOutcome.MalformedHandoff, status, RejectionReason: reason).Validated());
        Assert.Throws<ArgumentException>(() => (NativeLaunchDiagnosticPresentationTests.Snapshot(Guid.NewGuid()) with { ShapeStatus = status, RejectionReason = reason }).Validated());
    }

    [Fact]
    public void EveryFixedReasonHasBoundedPresentationWithoutGrantingAuthority()
    {
        foreach (var reason in Enum.GetValues<NativeHandoffRejectionReason>())
        {
            var source = NativeLaunchDiagnosticPresentationTests.Snapshot(Guid.NewGuid()) with
                { ShapeStatus = NativeHandoffCaptureStatus.InvalidShape, RejectionReason = reason };
            var view = NativeLaunchDiagnosticPresentation.Project(source.OperationId, source);
            Assert.True(view.Available); Assert.Contains("Rejected rule:", view.Shape); Assert.DoesNotContain("unavailable", view.Shape);
            Assert.InRange(view.Shape.Length, 1, 180);
            Assert.False(view.AccountVerified); Assert.False(view.ArrivalVerified); Assert.False(view.InputAuthorized);
        }
    }
}

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task FixedRejectionReasonSurvivesConfiguredHistoryAndTypedEvidenceExport() => OnUi(async () =>
    {
        var child = new NativeEvidenceChild { DiagnosticFactory = id => NativeLaunchDiagnosticPresentationTests.Snapshot(id) with
        {
            ShapeStatus = NativeHandoffCaptureStatus.InvalidShape, RejectionReason = NativeHandoffRejectionReason.UnknownDestinationField,
            PreparationStatus = NativeHandoffPreparationStatus.CaptureRejected, BrowserOutcome = RobloxHandoffProbeOutcome.MalformedHandoff
        } };
        await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor());
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview())); await shell.StartNativeTrial.Execution;
        var selected = await NativeEvidenceStartedAsync(shell); shell.Refresh.Execute(null); await shell.Refresh.Execution;
        Assert.Equal(NativeHandoffRejectionReason.UnknownDestinationField, shell.SelectedNativeDiagnostics.Snapshot!.RejectionReason);
        Assert.Contains("unsupported destination field", shell.SelectedNativeDiagnostics.Shape);
        string report = shell.BuildNativeTrialEvidenceReport(); AssertNativeEvidenceReport(report, selected.Id);
        using var json = JsonDocument.Parse(report);
        Assert.Equal("UnknownDestinationField", json.RootElement.GetProperty("NativeDiagnostics").GetProperty("RejectionReason").GetString());
    });
}
