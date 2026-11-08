using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class PrivateServerLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-private-trial-" + Guid.NewGuid().ToString("N"));
    private const string Code = "12345678901234567890";
    [Theory]
    [InlineData("valid", true)]
    [InlineData("wrong-code", false)]
    [InlineData("public", false)]
    [InlineData("extra", false)]
    [InlineData("wrong-place", false)]
    [InlineData("duplicate", false)]
    [InlineData("access-code", false)]
    public void OfficialPrivateCaptureMatchesOnlyTheLeasedCodeAndRemainsSingleUse(string variation, bool accepted)
    {
        var reference = new PrivateServerReference(Guid.NewGuid(), 2);
        using var lease = new PrivateServerLaunchLease(reference, 1818, new ProtectedPrivateServerCode(Code), () => { });
        using var context = new NativeHandoffContext(Guid.NewGuid(), Guid.NewGuid(), 123, 4, new(1818, null, RobloxServerKind.Private, reference), lease);
        var clock = new HandoffClock();
        string uri = SyntheticHandoff.Uri(context, clock, raw => variation == "public" ? raw : raw.Replace("RequestGame", "RequestPrivateGame")
            .Replace("isPlayTogetherGame=false", "linkCode=" + (variation == "wrong-code" ? "99999999999999999999" : Code))
            .Replace("placeId=1818", variation == "wrong-place" ? "placeId=1819" : "placeId=1818")
            + (variation == "extra" ? "&isPlayTogetherGame=false" : variation == "duplicate" ? "&linkCode=" + Code : variation == "access-code" ? "&accessCode=not-supported" : ""));
        string page = NativeHandoffNavigation.InitialPage(context);
        Assert.Equal("https://www.roblox.com/games/1818?privateServerLinkCode=" + Code, page);
        Assert.True(NativeHandoffNavigation.IsAllowedPage(context, page));
        Assert.False(NativeHandoffNavigation.IsAllowedPage(context, page + "1"));
        Assert.Equal(NativeHandoffCaptureStatus.ContextRejected, NativeRobloxHandoff.AcceptBuilt(context, uri, clock).Status);
        using(var ticket=new ProtectedLaunchTicket("synthetic-ticket"))
        {
            Assert.Throws<ArgumentException>(()=>NativeRobloxHandoffRequest.Build(context,ticket,123,Guid.NewGuid(),clock));
            Assert.False(ticket.IsConsumed);
        }
        var captured = NativeRobloxHandoff.Capture(context, page, RobloxHandoffProbePolicy.OfficialOrigin, uri, clock);
        Assert.Equal(accepted, captured.Status == NativeHandoffCaptureStatus.AcceptedSourceGrammar);
        Assert.DoesNotContain(Code, JsonSerializer.Serialize(captured) + context + lease);
        if (accepted)
        {
            Assert.Equal(NativeHandoffCaptureStatus.OperationAlreadyCaptured, NativeRobloxHandoff.Capture(context, page, RobloxHandoffProbePolicy.OfficialOrigin, uri, clock).Status);
            captured.Candidate!.Dispose(); Assert.False(lease.IsActive);
        }
        else Assert.Null(captured.Candidate);
    }

    [Fact]
    public async Task PrivateQueueRoundTripCarriesOnlyTheReferenceAndRejectsDowngradedFormat()
    {
        var reference = new PrivateServerReference(Guid.NewGuid(), 3);
        var intent = new LaunchIntent(Guid.NewGuid(), Guid.NewGuid(), 0, 123, 1818, 0, DateTimeOffset.UtcNow,
            5, null, RobloxServerKind.Private, true, reference);
        Assert.True(LaunchCoordinator.HasValidDestination(intent));
        Assert.False(LaunchCoordinator.HasValidDestination(intent with { PrivateServer = null }));
        Assert.False(LaunchCoordinator.HasValidDestination(intent with { RequestedServerKind = RobloxServerKind.Unknown }));
        Assert.False(LaunchCoordinator.HasValidDestination(intent with { RequestedJobId = SyntheticHandoff.Job }));
        using var store = new LaunchQueueStore(_root);
        await store.SaveAsync(new(true, [new(intent, LaunchState.Queued, LaunchProblem.None, DateTimeOffset.UtcNow)]), default);
        string path = Path.Combine(_root, "launch-queue.json"), json = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(json); Assert.Equal(9, doc.RootElement.GetProperty("Version").GetInt32());
        Assert.DoesNotContain(Code, json);
        Assert.Equal(reference, Assert.Single((await store.LoadAsync(default)).Operations).Intent.PrivateServer);
        await File.WriteAllTextAsync(path, json.Replace("\"Version\":9", "\"Version\":8"));
        using var older=new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => older.LoadAsync(default));
    }
    [Fact]
    public async Task ExactProtectedLeaseRejectsChangedRecordAndHoldsRemovalUntilDisposed()
    {
        using var store = new PrivateServerStore(_root); using var code = new ProtectedPrivateServerCode(Code);
        var saved = await store.AddAsync("Trial server", 5, 6, null, null, code, default);
        IPrivateServerStore contract = store;
        await Assert.ThrowsAsync<PrivateServerConflictException>(() => contract.AcquireForLaunchAsync(new(saved.Id, saved.Revision + 1), 5, default));
        await Assert.ThrowsAsync<PrivateServerConflictException>(() => contract.AcquireForLaunchAsync(new(saved.Id, saved.Revision), 7, default));
        using var lease = await contract.AcquireForLaunchAsync(new(saved.Id, saved.Revision), 5, default);
        Assert.True(lease.IsActive);
        Assert.DoesNotContain(Code, JsonSerializer.Serialize(lease) + lease);
        var removing = store.DeleteAsync(saved.Id, saved.Revision, default);
        Assert.False(removing.IsCompleted);
        lease.Dispose(); await removing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(lease.IsActive);
        await Assert.ThrowsAsync<PrivateServerConflictException>(() => contract.AcquireForLaunchAsync(new(saved.Id, saved.Revision), 5, default));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
