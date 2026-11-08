using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class NativeHandoffPreparationTests
{
    private const string PrivateCode = "12345678909876543210";
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PrivateTrialUsesExactProtectedReferenceAndFinalAccountGrant(bool changeReferenceAtDispatch)
    {
        using var store = new PrivateServerStore(_root); using var code = new ProtectedPrivateServerCode(PrivateCode);
        var saved = await store.AddAsync("Private trial", 1818, 42, null, null, code, default);
        await using var f = await Fixture.Open(_root, privateServers: store); using var browser = new Browser();
        var intent = f.Intent with { RequestedServerKind = RobloxServerKind.Private, PrivateServer = new(saved.Id, saved.Revision) };
        browser.Run = (context, _, _) => Task.FromResult(PrivateCapture(context));
        var answer = await f.Service.PrepareNativeHandoffAsync(intent, browser, default);
        Assert.Equal(NativeHandoffPreparationStatus.Ready, answer.Status);
        using var prepared = answer.Preparation!;
        Assert.Equal(intent.PrivateServer, prepared.Intent.PrivateServer);
        Assert.Equal(3, prepared.Intent.AuthorizationGeneration);
        var removing = store.DeleteAsync(saved.Id, saved.Revision, default); Assert.False(removing.IsCompleted);
        using var starter = new Starter(); await using var coordinator = new NativeHandoffCoordinator(f.Vault);
        var result = await coordinator.DispatchAsync(prepared.Candidate, prepared.FinalGrant,
            changeReferenceAtDispatch ? prepared.Intent with { PrivateServer = new(saved.Id, saved.Revision + 1) } : prepared.Intent, starter,
            new LaunchAuthorizationValidator(f.Repository, f.Vault).ValidateAsync, () => true, () => new(true, "Synthetic capacity"), default);
        Assert.Equal(changeReferenceAtDispatch ? NativeHandoffAttemptStatus.NotAuthorized : NativeHandoffAttemptStatus.StartedUnverified, result.Status);
        Assert.Equal(changeReferenceAtDispatch ? 0 : 1, starter.Calls);
        await removing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(prepared.Candidate.IsClosed);
    }

    [Theory]
    [InlineData("revision")] [InlineData("place")] [InlineData("removed")] [InlineData("missing-store")]
    public async Task ChangedSavedPrivateTargetNeverFencesAccountOrOpensBrowser(string change)
    {
        using var store = new PrivateServerStore(_root); using var code = new ProtectedPrivateServerCode(PrivateCode);
        var saved = await store.AddAsync("Private trial", 1818, 42, null, null, code, default);
        await using var f = await Fixture.Open(_root, privateServers: change == "missing-store" ? null : store); using var browser = new Browser();
        var intent = f.Intent with { RequestedServerKind = RobloxServerKind.Private, PrivateServer = new(saved.Id, saved.Revision + (change == "revision" ? 1 : 0)),
            PlaceId = change == "place" ? 1819 : 1818 };
        if (change == "removed") await store.DeleteAsync(saved.Id, saved.Revision, default);
        var answer = await f.Service.PrepareNativeHandoffAsync(intent, browser, default);
        Assert.Equal(NativeHandoffPreparationStatus.RequestChanged, answer.Status);
        Assert.Null(answer.Preparation); Assert.Equal(0, browser.Calls); Assert.Empty(f.Client.Seen);
        Assert.Equal(AuthorizationHealth.Linked, (await f.Vault.ReadAsync(f.Profile.Id, default))!.Health);
    }

    [Fact]
    public async Task PrivateTrialCancellationReleasesCodeLeaseAndRefusesLateBrowserReply()
    {
        using var store = new PrivateServerStore(_root); using var code = new ProtectedPrivateServerCode(PrivateCode);
        var saved = await store.AddAsync("Private trial", 1818, 42, null, null, code, default);
        await using var f = await Fixture.Open(_root, privateServers: store); using var browser = new Browser(); using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Run = async (context, _, _) => { var captured = PrivateCapture(context); entered.SetResult(); await release.Task; return captured; };
        var preparing = f.Service.PrepareNativeHandoffAsync(f.Intent with { RequestedServerKind = RobloxServerKind.Private, PrivateServer = new(saved.Id, saved.Revision) }, browser, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        try { await store.DeleteAsync(saved.Id, saved.Revision, default).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.SetResult(); }
        var answer = await preparing; Assert.Equal(NativeHandoffPreparationStatus.Cancelled, answer.Status);
        Assert.Null(answer.Preparation); Assert.Empty(f.Client.Seen);
    }

    private static NativeBrowserCapture PrivateCapture(NativeHandoffContext context)
    {
        var clock = new HandoffClock();
        string original = SyntheticHandoff.Uri(context, clock, raw => raw.Replace("RequestGame", "RequestPrivateGame").Replace("isPlayTogetherGame=false", "linkCode=" + PrivateCode));
        var parsed = NativeRobloxHandoff.Capture(context, NativeHandoffNavigation.InitialPage(context), RobloxHandoffProbePolicy.OfficialOrigin, original, clock);
        Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar, parsed.Status);
        return new(RobloxHandoffProbeOutcome.CapturedAndPrevented, parsed.Candidate, new(Current), parsed.Status);
    }
}
