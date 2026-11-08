using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class RobloxSessionLinkServiceTests
{
    private sealed class SignedServers(Func<bool,RobloxSessionCredential,CancellationToken,Task<SignedInServerResponse>> send) : IRobloxSignedInServerClient
    {
        public int Calls;
        public Task<SignedInServerResponse> BrowseAsync(SignedInServerQuery query,long user,RobloxSessionCredential cookie,CancellationToken ct)
        { Calls++; return send(false,cookie,ct); }
        public Task<SignedInServerResponse> RenameAsync(OwnedPrivateServer server,string name,RobloxSessionCredential cookie,CancellationToken ct)
        { Calls++; return send(true,cookie,ct); }
    }
    [Fact]
    public async Task SignedServerPageUsesTheSameDurableFenceAndRotationVerificationAsAccountReads()
    {
        using var repository=await Repository();var profile=await Profile(repository);var vault=new Vault();vault.Records[profile.Id]=Saved(profile.Id);
        var identity=new Client();var rotation=new RobloxSessionCredential("synthetic-server-rotation");
        var servers=new SignedServers((mutation,cookie,ct)=>
        {
            Assert.False(mutation);Assert.Equal(Secret,cookie.CopyValue());Assert.Equal(AuthorizationHealth.SignInRequired,vault.Records[profile.Id].Health);
            return Task.FromResult(new SignedInServerResponse(RobloxAccountOutcome.Success,new([],[],null),rotation));
        });
        await using var service=new RobloxSessionLinkService(repository,vault,identity,new Browser(),signedServers:servers);
        var result=await service.BrowseSignedInAsync(new(profile.Id,456,1),new(123,SignedInServerOrder.Recommended),default);
        Assert.NotNull(result.Page);Assert.Equal(3,result.Context.Generation);Assert.Equal("synthetic-server-rotation",vault.Records[profile.Id].SessionCookie);
        Assert.Equal(new[]{"synthetic-server-rotation"},identity.Seen);Assert.Throws<ObjectDisposedException>(()=>rotation.CopyValue());
    }
    [Fact]
    public async Task RenameChecksTheActualActorBeforeAnyRemoteMutationAndAgainAfterwards()
    {
        using var repository=await Repository();var profile=await Profile(repository);var vault=new Vault();vault.Records[profile.Id]=Saved(profile.Id);
        var identity=new Client();var servers=new SignedServers((mutation,cookie,ct)=>
        {Assert.True(mutation);Assert.Equal(1,identity.Calls);Assert.Equal(AuthorizationHealth.SignInRequired,vault.Records[profile.Id].Health);return Task.FromResult(new SignedInServerResponse(RobloxAccountOutcome.Success));});
        await using var service=new RobloxSessionLinkService(repository,vault,identity,new Browser(),signedServers:servers);
        var result=await service.RenamePrivateAsync(new(profile.Id,456,1),new(99,123,456,"Old",0,10),"New name",default);
        Assert.Equal(RobloxAccountOutcome.Success,result.Outcome);Assert.Equal(2,identity.Calls);Assert.Equal(3,result.Context.Generation);
    }
    [Fact]
    public async Task RenameRefusesForeignOwnersAndStaleReviewedAccountWithoutSending()
    {
        using var repository=await Repository();var profile=await Profile(repository);var vault=new Vault();vault.Records[profile.Id]=Saved(profile.Id);
        var servers=new SignedServers((_,_,_)=>throw new InvalidOperationException());
        await using var service=new RobloxSessionLinkService(repository,vault,new Client(),new Browser(),signedServers:servers);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.RenamePrivateAsync(new(profile.Id,456,1),new(99,123,777,"Old",0,10),"New",default));
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.RenamePrivateAsync(new(profile.Id,456,99),new(99,123,456,"Old",0,10),"New",default));
        Assert.Equal(0,servers.Calls);Assert.Equal(1,vault.Records[profile.Id].Generation);
    }
    [Fact]
    public async Task DisconnectWaitsForLateSignedPageAndDiscardsItBeforeRemovingAuthority()
    {
        using var repository=await Repository();var profile=await Profile(repository);var vault=new Vault();vault.Records[profile.Id]=Saved(profile.Id);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var held=new TaskCompletionSource<SignedInServerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var servers=new SignedServers((_,_,_)=>{entered.SetResult();return held.Task;});
        await using var service=new RobloxSessionLinkService(repository,vault,new Client(),new Browser(),signedServers:servers);
        var browse=service.BrowseSignedInAsync(new(profile.Id,456,1),new(123,SignedInServerOrder.OwnedPrivate),default);await entered.Task;
        var disconnect=service.DisconnectAsync(profile.Id,default);Assert.False(disconnect.IsCompleted);
        held.SetResult(new(RobloxAccountOutcome.Success,new([],[new(99,123,456,"Private",0,10)],null)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>browse);await disconnect;Assert.Empty(vault.Records);
    }
    [Fact]
    public async Task ServerRenameIdentityMismatchCannotReachTheRemoteMutation()
    {
        using var repository=await Repository();var profile=await Profile(repository);var vault=new Vault();vault.Records[profile.Id]=Saved(profile.Id);
        var identity=new Client{Reply=(_,_)=>Task.FromResult(new RobloxSessionValidation(Identity(777)))};
        var servers=new SignedServers((_,_,_)=>throw new InvalidOperationException());
        await using var service=new RobloxSessionLinkService(repository,vault,identity,new Browser(),signedServers:servers);
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.RenamePrivateAsync(new(profile.Id,456,1),new(99,123,456,"Old",1,10),"New",default));
        Assert.Equal(0,servers.Calls);Assert.Equal(AuthorizationHealth.SignInRequired,vault.Records[profile.Id].Health);
    }
}
