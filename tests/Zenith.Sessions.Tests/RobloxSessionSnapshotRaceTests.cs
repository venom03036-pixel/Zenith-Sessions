using System.IO;
using System.Globalization;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// All credentials/users/browser replies are synthetic; only the selected newly owned Temp
// vault uses actual DPAPI/files. No browser, Roblox endpoint or native client is invoked.
public sealed class RobloxSessionSnapshotRaceTests : IDisposable
{
    private readonly string _name="zenith-session-snapshot-"+Guid.NewGuid().ToString("N");
    private string Root=>Path.Combine(Path.GetTempPath(),_name);
    private const string Original="synthetic-snapshot-original",Current="synthetic-snapshot-current",Foreign="synthetic-snapshot-foreign";
    private static LinkedIdentity Identity()=>new(456,"verified_player",RobloxSessionIdentity.Issuer,"456");
    private static AuthorizationRecord Record(Guid id,long generation=1,string cookie=Original)=>new(id,RobloxSessionIdentity.Provider,456,generation,"","",DateTimeOffset.MaxValue,
        "verified_player",AuthorizationHealth.Linked,RobloxSessionIdentity.Issuer,"456",SessionCookie:cookie);
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateSignInCannotReplaceANewerGrantWhenItsBrowserStartedFirst(bool existing)
    {
        await using var fixture=await Fixture.Open(Root,existing);var entered=Signal();var release=Signal();
        fixture.Browser.Run=async _=>{entered.TrySetResult();await release.Task;return new(Current);};
        var linking=fixture.Service.LinkAsync(fixture.Profile.Id,default);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await fixture.ForeignAsync();}
        finally{release.TrySetResult();}
        var error=await Assert.ThrowsAsync<AccountLinkException>(()=>linking.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("changed",error.Message);var retained=Assert.IsType<AuthorizationRecord>(await fixture.Inner.ReadAsync(fixture.Profile.Id,default));
        Assert.Equal(Foreign,retained.SessionCookie);Assert.Equal(existing?2:1,retained.Generation);Assert.Equal(AuthorizationHealth.Linked,retained.Health);
        Assert.Equal(1,fixture.Client.Calls);Assert.Equal(1,fixture.Browser.Calls);Assert.Equal(0,fixture.Vault.InventoryReads);
    }

    [Fact]
    public async Task AbsentGrantSnapshotCannotSurviveAnInterveningDisconnectTombstone()
    {
        await using var fixture=await Fixture.Open(Root,false);var entered=Signal();var release=Signal();
        fixture.Browser.Run=async _=>{entered.TrySetResult();await release.Task;return new(Current);};
        var linking=fixture.Service.LinkAsync(fixture.Profile.Id,default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await using var other=new RobloxSessionLinkService(fixture.Profiles,fixture.Inner,new Client(),new Browser());
            await other.DisconnectAsync(fixture.Profile.Id,default);Assert.Null(await fixture.Inner.ReadAsync(fixture.Profile.Id,default));
        }
        finally{release.TrySetResult();}
        await Assert.ThrowsAsync<AccountLinkException>(()=>linking.WaitAsync(TimeSpan.FromSeconds(3)));
        var snapshot=await fixture.Inner.ReadWriteSnapshotAsync(fixture.Profile.Id,default);
        Assert.Null(snapshot.Record);Assert.Equal(1,snapshot.HighestAllocated);Assert.Equal(0,snapshot.LastCommitted);
    }

    [Fact]
    public async Task ExplicitNewSignInCanReplaceAnUncertainFencedRecordWithoutReusingOldMaterial()
    {
        await using var fixture=await Fixture.Open(Root,true);
        await fixture.Inner.AllocateNextGenerationAsync(fixture.Profile.Id,1,default);
        Assert.Empty((await fixture.Inner.ReadAsync(fixture.Profile.Id,default))!.SessionCookie);
        await fixture.Service.LinkAsync(fixture.Profile.Id,default);
        var saved=Assert.IsType<AuthorizationRecord>(await fixture.Inner.ReadAsync(fixture.Profile.Id,default));
        Assert.Equal(3,saved.Generation);Assert.Equal(Current,saved.SessionCookie);Assert.Equal(AuthorizationHealth.Linked,saved.Health);
        Assert.Equal(new[]{Current},fixture.Client.Seen);Assert.Equal(1,fixture.Browser.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedCheckConflictsBeforeContactOrFinalCommitPreserveTheForeignGrant(bool final)
    {
        await using var fixture=await Fixture.Open(Root,true);
        fixture.Vault.BeforeCommit=async (_,replacement)=>
        {if((replacement.Health==AuthorizationHealth.Linked)==final)await fixture.ForeignAsync();};
        var error=await Assert.ThrowsAsync<AccountLinkException>(()=>fixture.Service.CheckSavedAsync(fixture.Profile.Id,default));
        Assert.Contains("changed",error.Message);Assert.Equal(final?1:0,fixture.Client.Calls);Assert.Equal(0,fixture.Browser.Calls);
        var retained=Assert.IsType<AuthorizationRecord>(await fixture.Inner.ReadAsync(fixture.Profile.Id,default));
        Assert.Equal(final?3:2,retained.Generation);Assert.Equal(Foreign,retained.SessionCookie);Assert.Equal(AuthorizationHealth.Linked,retained.Health);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedIssuedWriteFencesOnlyItsOwnerAfterActualCompletion(bool savedCheck)
    {
        await using var fixture=await Fixture.Open(Root,savedCheck);var entered=Signal();var release=Signal();byte[]? committed=null;
        string path=Path.Combine(Root,"accounts.vault",fixture.Profile.Id.ToString("N")+".dpapi");
        fixture.Vault.AfterCommit=async replacement=>
        {if(replacement.Health==AuthorizationHealth.Linked){committed=await File.ReadAllBytesAsync(path);entered.TrySetResult();await release.Task;}};
        var work=savedCheck?fixture.Service.CheckSavedAsync(fixture.Profile.Id,default):fixture.Service.LinkAsync(fixture.Profile.Id,default);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));fixture.Service.Cancel();Assert.False(work.IsCompleted);}
        finally{release.TrySetResult();}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work.WaitAsync(TimeSpan.FromSeconds(3)));
        var snapshot=await fixture.Inner.ReadWriteSnapshotAsync(fixture.Profile.Id,default);Assert.NotNull(snapshot.Record);
        Assert.Equal(AuthorizationHealth.SignInRequired,snapshot.Record.Health);Assert.Empty(snapshot.Record.SessionCookie);
        Assert.Equal(savedCheck?4:2,snapshot.HighestAllocated);Assert.Equal(savedCheck?3:1,snapshot.LastCommitted);
        Assert.Equal(Guid.Empty,snapshot.HighestMutationId);Assert.Equal(committed,await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AForeignReplacementAfterOurCommitSurvivesTheLateFailureCleanup()
    {
        await using var fixture=await Fixture.Open(Root,true);
        fixture.Vault.AfterCommit=async replacement=>
        {if(replacement.Health==AuthorizationHealth.Linked){await fixture.ForeignAsync();throw new IOException("Synthetic receipt failure");}};
        await Assert.ThrowsAsync<IOException>(()=>fixture.Service.LinkAsync(fixture.Profile.Id,default));
        var retained=Assert.IsType<AuthorizationRecord>(await fixture.Inner.ReadAsync(fixture.Profile.Id,default));
        Assert.Equal(3,retained.Generation);Assert.Equal(Foreign,retained.SessionCookie);Assert.Equal(AuthorizationHealth.Linked,retained.Health);
    }

    [Fact]
    public async Task DisconnectCannotDeleteAReplacementThatArrivesAfterItsSnapshotRead()
    {
        await using var fixture=await Fixture.Open(Root,true);fixture.Vault.BeforeDelete=_=>fixture.ForeignAsync();
        await Assert.ThrowsAsync<AccountLinkException>(()=>fixture.Service.DisconnectAsync(fixture.Profile.Id,default));
        Assert.Equal(Foreign,(await fixture.Inner.ReadAsync(fixture.Profile.Id,default))!.SessionCookie);
        Assert.NotNull(await fixture.Profiles.FindAsync(fixture.Profile.Id,default));Assert.Equal(0,fixture.Browser.Calls);Assert.Equal(0,fixture.Client.Calls);
    }

    [Fact]
    public async Task SuccessfulDisconnectAdvancesExactlyOneFenceAndLeavesTheProfile()
    {
        await using var fixture=await Fixture.Open(Root,true);await fixture.Service.DisconnectAsync(fixture.Profile.Id,default);
        var snapshot=await fixture.Inner.ReadWriteSnapshotAsync(fixture.Profile.Id,default);
        Assert.Null(snapshot.Record);Assert.Equal(2,snapshot.HighestAllocated);Assert.Equal(1,snapshot.LastCommitted);
        Assert.NotNull(await fixture.Profiles.FindAsync(fixture.Profile.Id,default));
    }

    [Fact]
    public async Task DisconnectProjectionCleanupCannotRemoveAnotherServicesNewerPreparedSignIn()
    {
        await using var fixture=await Fixture.Open(Root,true,project:true);var prepared=Signal();var release=Signal();
        var newerVault=new WrappedVault(fixture.Inner);
        using var newerProjection=new AccountIdentityProjection(Root,fixture.Profiles,newerVault);
        await using var newerService=new RobloxSessionLinkService(fixture.Profiles,newerVault,new Client(),new Browser(),newerProjection);
        newerVault.BeforeCommit=async (_,replacement)=>{if(replacement.Health==AuthorizationHealth.Linked){prepared.TrySetResult();await release.Task;}};
        Task<LinkedIdentity>? newerWork=null;
        fixture.Vault.AfterDelete=async()=>
        {newerWork=newerService.LinkAsync(fixture.Profile.Id,default);await prepared.Task.WaitAsync(TimeSpan.FromSeconds(3));};
        var removing=fixture.Service.DisconnectAsync(fixture.Profile.Id,default);
        try
        {
            await removing.WaitAsync(TimeSpan.FromSeconds(3));Assert.NotNull(newerWork);Assert.False(newerWork.IsCompleted);
            string journal=Path.Combine(Root,"account-identity-pending",fixture.Profile.Id.ToString("N")+".json");
            Assert.True(File.Exists(journal),"Disconnect removed a later operation's prepared metadata.");
        }
        finally
        {release.TrySetResult();if(newerWork is not null)await newerWork.WaitAsync(TimeSpan.FromSeconds(3));}
        var saved=Assert.IsType<AuthorizationRecord>(await fixture.Inner.ReadAsync(fixture.Profile.Id,default));
        Assert.Equal(3,saved.Generation);Assert.Equal(Current,saved.SessionCookie);Assert.Equal(AuthorizationHealth.Linked,saved.Health);
        var profile=Assert.IsType<AccountProfile>(await fixture.Profiles.FindAsync(fixture.Profile.Id,default));
        Assert.Equal(456,profile.RobloxUserId);Assert.Equal("verified_player",profile.Username);Assert.Equal("Alias preserved",profile.Label);
    }

    [Fact]
    public async Task ShareLinkRotationIsCommittedThroughTheActualVaultSnapshotAtTheNextGeneration()
    {
        // R-accounts-3: Roblox replaced the saved session while answering a share link.
        await using var fixture=await Fixture.Open(Root,true);var shareLinks=new RotatingShareLinks();
        await using var service=new RobloxSessionLinkService(fixture.Profiles,fixture.Vault,fixture.Client,fixture.Browser,shareLinks:shareLinks);
        using var link=new RobloxShareLink(new ProtectedShareCode("abc123"),RobloxShareLinkType.ExperienceDetails);
        using var resolution=await service.ResolveShareLinkAsync(fixture.Profile.Id,link,default);
        Assert.Equal(RobloxShareLinkOutcome.Experience,resolution.Outcome);Assert.Null(resolution.TakeRotatedCredential());
        Assert.Equal(Original,shareLinks.SeenCookie);Assert.Equal(new[]{Current},fixture.Client.Seen);Assert.Equal(0,fixture.Browser.Calls);
        var snapshot=await fixture.Inner.ReadWriteSnapshotAsync(fixture.Profile.Id,default);
        Assert.Equal(Current,snapshot.Record!.SessionCookie);Assert.Equal(AuthorizationHealth.Linked,snapshot.Record.Health);
        Assert.Equal(2,snapshot.Record.Generation);Assert.Equal(2,snapshot.HighestAllocated);Assert.Equal(2,snapshot.LastCommitted);
    }

    [Fact]
    public async Task AShareLinkRotationInterruptedAfterItsCommitFencesOnlyThatWriteAndTheAnswerStands()
    {
        await using var fixture=await Fixture.Open(Root,true);var shareLinks=new RotatingShareLinks();
        fixture.Vault.AfterCommit=replacement=>replacement.Health==AuthorizationHealth.Linked?throw new IOException("Synthetic receipt failure"):Task.CompletedTask;
        await using var service=new RobloxSessionLinkService(fixture.Profiles,fixture.Vault,fixture.Client,fixture.Browser,shareLinks:shareLinks);
        using var link=new RobloxShareLink(new ProtectedShareCode("abc123"),RobloxShareLinkType.ExperienceDetails);
        using var resolution=await service.ResolveShareLinkAsync(fixture.Profile.Id,link,default);
        Assert.Equal(RobloxShareLinkOutcome.Experience,resolution.Outcome);Assert.Equal(5,resolution.PlaceId);
        var snapshot=await fixture.Inner.ReadWriteSnapshotAsync(fixture.Profile.Id,default);
        Assert.Equal(AuthorizationHealth.SignInRequired,snapshot.Record!.Health);Assert.Empty(snapshot.Record.SessionCookie);
        Assert.Equal(3,snapshot.HighestAllocated);Assert.Equal(2,snapshot.LastCommitted);Assert.Equal(Guid.Empty,snapshot.HighestMutationId);
    }

    private sealed class RotatingShareLinks : IRobloxShareLinkClient
    {
        public string? SeenCookie;
        public Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link,RobloxSessionCredential credential,RobloxShareLinkTokenScope scope,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();SeenCookie=credential.CopyValue();return Task.FromResult(new RobloxShareLinkResolution(RobloxShareLinkOutcome.Experience,5,6,rotatedCredential:new(Current)));}
        public void ForgetTokens(Guid profileId){}
        public void ForgetAllTokens(){}
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required AccountRepository Profiles;public required AccountProfile Profile;public required ProtectedCredentialVault Inner;
        public required WrappedVault Vault;public required Client Client;public required Browser Browser;public required RobloxSessionLinkService Service;
        public AccountIdentityProjection? Projection;
        public static async Task<Fixture> Open(string root,bool existing,bool project=false)
        {
            var profiles=new AccountRepository(root);await profiles.InitializeAsync(default);var profile=await profiles.SaveAsync(AccountProfile.Create("Alias preserved"),default);
            var inner=new ProtectedCredentialVault(root);if(existing)await inner.SaveAsync(Record(profile.Id),default);
            var vault=new WrappedVault(inner);var client=new Client();var browser=new Browser();
            var projection=project?new AccountIdentityProjection(root,profiles,vault):null;
            return new(){Profiles=profiles,Profile=profile,Inner=inner,Vault=vault,Client=client,Browser=browser,Projection=projection,Service=new(profiles,vault,client,browser,projection)};
        }
        public async Task ForeignAsync()
        {long generation=await Inner.AllocateNextGenerationAsync(Profile.Id,0,default);await Inner.SaveAsync(Record(Profile.Id,generation,Foreign),default);}
        public async ValueTask DisposeAsync(){await Service.DisposeAsync();Projection?.Dispose();Inner.Dispose();Profiles.Dispose();}
    }
    private sealed class Browser : IRobloxSessionSignIn
    {
        public int Calls;public Func<CancellationToken,Task<RobloxSessionCredential>>? Run;
        public Task<RobloxSessionCredential> SignInAsync(Guid id,CancellationToken ct){Calls++;return Run?.Invoke(ct)??Task.FromResult(new RobloxSessionCredential(Current));}
    }
    private sealed class Client : IRobloxSessionClient
    {
        public int Calls;public readonly List<string> Seen=[];
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();Calls++;Seen.Add(credential.CopyValue());return Task.FromResult(new RobloxSessionValidation(Identity()));}
    }
    private sealed class WrappedVault(ProtectedCredentialVault inner) : IAccountCredentialVault,ISnapshotAuthorizationVault,IAuthorizationMetadataReader,IRobloxAuthorizationIdentityReader
    {
        public int InventoryReads;public Func<AuthorizationWriteSnapshot,AuthorizationRecord,Task>? BeforeCommit;public Func<AuthorizationRecord,Task>? AfterCommit;
        public Func<AuthorizationWriteSnapshot,Task>? BeforeDelete;
        public Func<Task>? AfterDelete;
        public Task<AuthorizationRecord?> ReadAsync(Guid id,CancellationToken ct)=>inner.ReadAsync(id,ct);
        public Task SaveAsync(AuthorizationRecord record,CancellationToken ct)=>throw new InvalidOperationException("The saved-session service must never fall back to a separate nonconditional save.");
        public Task DeleteAsync(Guid id,CancellationToken ct)=>throw new InvalidOperationException("The saved-session service must never fall back to a separate nonconditional delete.");
        public Task<AuthorizationWriteSnapshot> ReadWriteSnapshotAsync(Guid id,CancellationToken ct)=>inner.ReadWriteSnapshotAsync(id,ct);
        public async Task<AuthorizationMutationResult> TryCommitSnapshotAsync(AuthorizationWriteSnapshot expected,AuthorizationRecord replacement,Guid mutationId,CancellationToken ct)
        {
            if(BeforeCommit is not null)await BeforeCommit(expected,replacement);
            var result=await inner.TryCommitSnapshotAsync(expected,replacement,mutationId,ct);
            if(result.Status==AuthorizationMutationStatus.Applied&&AfterCommit is not null)await AfterCommit(replacement);
            return result;
        }
        public async Task<AuthorizationMutationResult> TryDeleteSnapshotAsync(AuthorizationWriteSnapshot expected,CancellationToken ct)
        {if(BeforeDelete is not null)await BeforeDelete(expected);var result=await inner.TryDeleteSnapshotAsync(expected,ct);if(result.Status==AuthorizationMutationStatus.Applied&&AfterDelete is not null)await AfterDelete();return result;}
        public Task<AuthorizationMutationResult> TryReplaceNextAsync(AuthorizationRecord expected,AuthorizationRecord replacement,Guid mutationId,CancellationToken ct)=>inner.TryReplaceNextAsync(expected,replacement,mutationId,ct);
        public Task<bool> TryFenceOwnedMutationAsync(Guid id,Guid mutationId,CancellationToken ct)=>inner.TryFenceOwnedMutationAsync(id,mutationId,ct);
        public Task<AuthorizationMetadata?> GetSummaryAsync(Guid id,CancellationToken ct)=>inner.GetSummaryAsync(id,ct);
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> ids,CancellationToken ct)
        {InventoryReads++;throw new InvalidOperationException("No protected inventory reads are permitted.");}
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string provider,string issuer,string subject,CancellationToken ct)=>inner.FindByIdentityAsync(provider,issuer,subject,ct);
        public Task<AuthorizationMetadata?> FindByRobloxUserIdAsync(long user,CancellationToken ct)=>inner.FindByRobloxUserIdAsync(user,ct);
    }
    public void Dispose()
    {
        string path=Path.GetFullPath(Root);
        if(Path.GetDirectoryName(path)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||Path.GetFileName(path)!=_name)
            throw new InvalidOperationException("Unexpected synthetic saved-session fixture cleanup path.");
        if(Directory.Exists(path))Directory.Delete(path,true);
    }
}
