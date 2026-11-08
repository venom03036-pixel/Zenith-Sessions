using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class RobloxSessionLinkServiceTests
{
    private sealed class AccountData(Func<RobloxAccountRead, long, long?, RobloxSessionCredential, CancellationToken, Task<RobloxAccountResponse>> read) : IRobloxAccountDataClient
    {
        public int Calls;
        public Task<RobloxAccountResponse> ReadAsync(RobloxAccountRead kind, long user, long? target, RobloxSessionCredential credential, CancellationToken ct)
        { Calls++; return read(kind, user, target, credential, ct); }
    }
    [Fact]
    public async Task AccountObservationFencesBeforeRequestAndVerifiesRotationBeforePublishingData()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var identity = new Client(); var rotated = new RobloxSessionCredential("synthetic-observation-replacement");
        var data = new AccountData((kind, user, target, credential, _) =>
        {
            Assert.Equal(RobloxAccountRead.Robux, kind); Assert.Equal(456, user); Assert.Null(target); Assert.Equal(Secret, credential.CopyValue());
            Assert.Equal(AuthorizationHealth.SignInRequired, vault.Records[profile.Id].Health); Assert.Equal(2, vault.Records[profile.Id].Generation);
            return Task.FromResult(new RobloxAccountResponse(RobloxAccountOutcome.Success, new(Number: 123), rotated));
        });
        await using var service = new RobloxSessionLinkService(repository, vault, identity, new Browser(), accountData: data);
        var answer = await service.ReadAccountAsync(new(profile.Id, 456, 1), RobloxAccountRead.Robux, null, default);
        Assert.Equal(123, answer.Observation!.Number); Assert.Equal(3, answer.Context.Generation);
        Assert.Equal(AuthorizationHealth.Linked, vault.Records[profile.Id].Health); Assert.Equal("synthetic-observation-replacement", vault.Records[profile.Id].SessionCookie);
        Assert.Equal(new[] { "synthetic-observation-replacement" }, identity.Seen); Assert.Throws<ObjectDisposedException>(() => rotated.CopyValue());
    }
    [Theory]
    [InlineData(RobloxAccountOutcome.Challenge, AuthorizationHealth.SignInRequired, 0)]
    [InlineData(RobloxAccountOutcome.RateLimited, AuthorizationHealth.Linked, 0)]
    [InlineData(RobloxAccountOutcome.SignInRequired, AuthorizationHealth.Linked, 1)]
    public async Task AccountObservationRefusalsKeepConservativeAuthority(RobloxAccountOutcome outcome, AuthorizationHealth expected, int identityCalls)
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var identity = new Client(); var data = new AccountData((_, _, _, _, _) => Task.FromResult(new RobloxAccountResponse(outcome)));
        await using var service = new RobloxSessionLinkService(repository, vault, identity, new Browser(), accountData: data);
        var result = await service.ReadAccountAsync(new(profile.Id,456,1), RobloxAccountRead.Robux, null, default);
        Assert.Equal(expected, vault.Records[profile.Id].Health); Assert.Equal(identityCalls, identity.Calls);
        Assert.Equal(outcome == RobloxAccountOutcome.SignInRequired ? RobloxAccountOutcome.Unavailable : outcome, result.Outcome);
    }
    [Fact]
    public async Task RateLimitedRotationMustStillVerifyAndCannotRestoreTheOriginalCookie()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var identity = new Client { Reply = (_, _) => throw new RobloxSessionException(RobloxSessionFailure.NotProcessed, "Synthetic") };
        var data = new AccountData((_, _, _, _, _) => Task.FromResult(new RobloxAccountResponse(RobloxAccountOutcome.RateLimited, rotatedCredential: new("synthetic-replacement"))));
        await using var service = new RobloxSessionLinkService(repository, vault, identity, new Browser(), accountData: data);
        await Assert.ThrowsAsync<RobloxSessionException>(() => service.ReadAccountAsync(new(profile.Id,456,1), RobloxAccountRead.Robux, null, default));
        Assert.Equal(AuthorizationHealth.SignInRequired, vault.Records[profile.Id].Health); Assert.Equal(2,vault.Records[profile.Id].Generation);
    }
    [Fact]
    public async Task AccountObservationsRejectStaleIdentityAndGenerationBeforeSendingAnything()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var data = new AccountData((_, _, _, _, _) => throw new InvalidOperationException("Must not run"));
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), accountData: data);
        await Assert.ThrowsAsync<AccountLinkException>(() => service.ReadAccountAsync(new(profile.Id,999,1), RobloxAccountRead.Robux, null, default));
        await Assert.ThrowsAsync<AccountLinkException>(() => service.ReadAccountAsync(new(profile.Id,456,99), RobloxAccountRead.Robux, null, default));
        Assert.Equal(0,data.Calls); Assert.Equal(Saved(profile.Id),vault.Records[profile.Id]);
    }
    [Fact]
    public async Task FriendshipRequestsRejectAChangedTargetBeforeFencingOrSending()
    {
        using var repository = await Repository(); var first = await Profile(repository); var second = await Profile(repository);
        var vault = new Vault(); vault.Records[first.Id] = Saved(first.Id); vault.Records[second.Id] = Saved(second.Id,789);
        var data = new AccountData((_, _, _, _, _) => throw new InvalidOperationException("Must not run"));
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), accountData: data);
        await Assert.ThrowsAsync<AccountLinkException>(() => service.ReadAccountAsync(new(first.Id,456,1),RobloxAccountRead.RequestFriendship,new(second.Id,789,2),default));
        Assert.Equal(0,data.Calls); Assert.Equal(1,vault.Records[first.Id].Generation);
    }
    [Fact]
    public async Task CancellationOwnsLateResponseDisposesRotationAndNeverPublishesIt()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RobloxAccountResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = new AccountData((_, _, _, _, _) => { entered.SetResult(); return release.Task; });
        var identity = new Client(); await using var service = new RobloxSessionLinkService(repository,vault,identity,new Browser(),accountData:data);
        var running = service.ReadAccountAsync(new(profile.Id,456,1),RobloxAccountRead.Robux,null,default); await entered.Task;
        service.Cancel(); var dispose = service.DisposeAsync().AsTask(); Assert.False(dispose.IsCompleted);
        var replacement = new RobloxSessionCredential("synthetic-late"); release.SetResult(new(RobloxAccountOutcome.Success,new(Number:123),replacement));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>running); await dispose;
        Assert.Throws<ObjectDisposedException>(()=>replacement.CopyValue()); Assert.Equal(0,identity.Calls); Assert.Equal(AuthorizationHealth.SignInRequired,vault.Records[profile.Id].Health);
    }
    [Fact]
    public async Task RemovingTheOtherReviewedAccountCancelsAnActiveFriendshipRequest()
    {
        using var repository = await Repository(); var first = await Profile(repository); var second = await Profile(repository);
        var vault = new Vault(); vault.Records[first.Id] = Saved(first.Id); vault.Records[second.Id] = Saved(second.Id,789);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken observed = default;
        var data = new AccountData(async (_, _, _, _, ct) => { observed=ct; entered.SetResult(); await Task.Delay(Timeout.Infinite,ct); return new(RobloxAccountOutcome.Success); });
        await using var service = new RobloxSessionLinkService(repository,vault,new Client(),new Browser(),accountData:data);
        var running = service.ReadAccountAsync(new(first.Id,456,1),RobloxAccountRead.RequestFriendship,new(second.Id,789,1),default); await entered.Task;
        using var removal = await service.BeginProfileRemovalAsync([second.Id],default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>running); Assert.True(observed.IsCancellationRequested);
        Assert.Equal(SavedSessionOperationKind.AccountObservation,removal.StoppedOperationKind); Assert.Equal(AuthorizationHealth.SignInRequired,vault.Records[first.Id].Health);
    }
    [Fact]
    public async Task FailedObservationCannotOverwriteNewerAuthority()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var data = new AccountData((_, _, _, _, _) => { vault.Records[profile.Id]=Saved(profile.Id) with {Generation=20,SessionCookie="synthetic-newer"}; return Task.FromResult(new RobloxAccountResponse(RobloxAccountOutcome.Success,new(Number:123))); });
        await using var service = new RobloxSessionLinkService(repository,vault,new Client(),new Browser(),accountData:data);
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.ReadAccountAsync(new(profile.Id,456,1),RobloxAccountRead.Robux,null,default));
        Assert.Equal("synthetic-newer",vault.Records[profile.Id].SessionCookie); Assert.Equal(20,vault.Records[profile.Id].Generation);
    }
    [Fact]
    public async Task FriendshipMutationConfirmsActorIdentityBeforeSendingTheSocialAction()
    {
        using var repository = await Repository(); var first = await Profile(repository); var second = await Profile(repository);
        var vault=new Vault();vault.Records[first.Id]=Saved(first.Id);vault.Records[second.Id]=Saved(second.Id,789);
        var identity=new Client { Reply=(_,_)=>Task.FromResult(new RobloxSessionValidation(Identity(999))) };
        var data=new AccountData((_,_,_,_,_)=>Task.FromResult(new RobloxAccountResponse(RobloxAccountOutcome.Success,new())));
        await using var service=new RobloxSessionLinkService(repository,vault,identity,new Browser(),accountData:data);
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.ReadAccountAsync(new(first.Id,456,1),RobloxAccountRead.RequestFriendship,new(second.Id,789,1),default));
        Assert.Equal(0,data.Calls);Assert.Equal(AuthorizationHealth.SignInRequired,vault.Records[first.Id].Health);
    }
    [Fact]
    public async Task FriendshipPreflightRotationIsVerifiedBeforeUseAndSavedOnlyAfterTheAction()
    {
        using var repository=await Repository();var first=await Profile(repository);var second=await Profile(repository);
        var vault=new Vault();vault.Records[first.Id]=Saved(first.Id);vault.Records[second.Id]=Saved(second.Id,789);
        var identity=new Client{Reply=(call,_)=>Task.FromResult(new RobloxSessionValidation(Identity(),call==1?new("synthetic-preflight-replacement"):null))};
        var data=new AccountData((_,_,_,credential,_)=>
        {
            Assert.Equal(2,identity.Calls);Assert.Equal("synthetic-preflight-replacement",credential.CopyValue());
            Assert.Equal(AuthorizationHealth.SignInRequired,vault.Records[first.Id].Health);Assert.Equal(Secret,vault.Records[first.Id].SessionCookie);
            return Task.FromResult(new RobloxAccountResponse(RobloxAccountOutcome.Success,new()));
        });
        await using var service=new RobloxSessionLinkService(repository,vault,identity,new Browser(),accountData:data);
        var result=await service.ReadAccountAsync(new(first.Id,456,1),RobloxAccountRead.RequestFriendship,new(second.Id,789,1),default);
        Assert.Equal(RobloxAccountOutcome.Success,result.Outcome);Assert.Equal(3,identity.Calls);Assert.Equal(3,result.Context.Generation);
        Assert.Equal("synthetic-preflight-replacement",vault.Records[first.Id].SessionCookie);
    }
    [Fact]
    public async Task RepeatedPreflightRotationNeverDispatchesAnUnverifiedCookie()
    {
        using var repository=await Repository();var first=await Profile(repository);var second=await Profile(repository);
        var vault=new Vault();vault.Records[first.Id]=Saved(first.Id);vault.Records[second.Id]=Saved(second.Id,789);
        var identity=new Client{Reply=(call,_)=>Task.FromResult(new RobloxSessionValidation(Identity(),new("synthetic-rotation-"+call)))};
        var data=new AccountData((_,_,_,_,_)=>throw new InvalidOperationException("Must not run"));
        await using var service=new RobloxSessionLinkService(repository,vault,identity,new Browser(),accountData:data);
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.ReadAccountAsync(new(first.Id,456,1),RobloxAccountRead.AcceptFriendship,new(second.Id,789,1),default));
        Assert.Equal(2,identity.Calls);Assert.Equal(0,data.Calls);Assert.Equal(AuthorizationHealth.SignInRequired,vault.Records[first.Id].Health);
    }
}
