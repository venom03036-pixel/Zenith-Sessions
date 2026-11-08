using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class AuthorizationTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-authorization-tests-"+Guid.NewGuid().ToString("N"));
    private static string Query(Uri uri,string key)=>Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&').Single(p=>p.StartsWith(key+"=",StringComparison.Ordinal)).Split('=',2)[1]);
    private static Uri Callback(OAuthAttempt attempt,string code="synthetic-code")=>new(attempt.Callback+"?state="+Query(attempt.AuthorizeUri,"state")+"&code="+code);
    [Fact]
    public void PkceIsRandomAndCallbackCannotBeReusedOrChanged()
    {
        var attempt=new OAuthAttempt("12345",47635); var other=new OAuthAttempt("12345",47635);
        Assert.NotEqual(Query(attempt.AuthorizeUri,"state"),Query(other.AuthorizeUri,"state"));
        Assert.NotEqual(Query(attempt.AuthorizeUri,"code_challenge"),Query(other.AuthorizeUri,"code_challenge"));
        Assert.Equal("openid profile",Query(attempt.AuthorizeUri,"scope"));
        Assert.Equal("select_account",Query(attempt.AuthorizeUri,"prompt"));
        Assert.DoesNotContain("client_secret",attempt.AuthorizeUri.AbsoluteUri);
        Assert.Throws<AccountLinkException>(()=>attempt.ConsumeCallback(Callback(other)));
        Assert.Throws<AccountLinkException>(()=>attempt.ConsumeCallback(new Uri(Callback(attempt).AbsoluteUri.Replace("localhost","other.invalid",StringComparison.Ordinal))));
        Assert.Throws<AccountLinkException>(()=>attempt.ConsumeCallback(new Uri(Callback(attempt)+"&state=duplicate")));
        Assert.Equal("synthetic-code",attempt.ConsumeCallback(Callback(attempt)));
        Assert.Throws<AccountLinkException>(()=>attempt.ConsumeCallback(Callback(attempt)));
    }
    [Fact]
    public void LoopbackParserRejectsUnrelatedHostPathBodiesAndDuplicateFields()
    {
        var attempt=new OAuthAttempt("12345",47635); string target=Callback(attempt).PathAndQuery;
        Uri? Parse(string first,string host="localhost:47635",string extra="")=>LoopbackOAuthReceiver.ParseRequest(Encoding.ASCII.GetBytes(first+"\r\nHost: "+host+extra),attempt);
        Assert.NotNull(Parse("GET "+target+" HTTP/1.1"));
        Assert.Null(Parse("GET "+target+" HTTP/1.1","other.invalid"));
        Assert.Null(Parse("POST "+target+" HTTP/1.1"));
        Assert.Null(Parse("GET /other/?state=abc HTTP/1.1"));
        Assert.Null(Parse("GET "+target+" HTTP/1.1",extra:"\r\nHost: localhost:47635"));
        Assert.Null(Parse("GET "+target+" HTTP/1.1",extra:"\r\nContent-Length: 100"));
        Assert.Null(Parse("GET "+target+" HTTP/1.1",extra:"\r\nTransfer-Encoding: chunked"));
        Assert.Null(Parse("GET "+target+"&code=duplicate HTTP/1.1"));
    }
    [Fact]
    public async Task CodeExchangeUsesPkceAndAuthoritativeUserInfoThenCommitsOnlyTheSelectedProfile()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("Manual label") with { Username="not_proof" },default);
        var other=await repository.SaveAsync(AccountProfile.Create("Other"),default);
        var vault=new MemoryVault(); var handler=new SyntheticRobloxHandler();
        using var http=new HttpClient(handler); using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        var identity=await service.LinkAsync(profile.Id,"12345",47635,default);
        Assert.Equal(456,identity.UserId); Assert.Equal("verified_player",identity.Username);
        Assert.Equal(RobloxOAuthIdentity.Issuer,identity.Issuer); Assert.Equal("456",identity.Subject);
        var authorization=Assert.Single(vault.Records).Value;
        Assert.Equal(profile.Id,authorization.ProfileId); Assert.Equal(456,authorization.VerifiedUserId);
        Assert.Equal("verified_player",authorization.VerifiedUsername); Assert.Equal(1,authorization.Generation);
        Assert.Equal(RobloxOAuthIdentity.Issuer,authorization.Issuer); Assert.Equal("456",authorization.Subject);
        Assert.Empty(authorization.AccessToken);
        Assert.Null(await vault.ReadAsync(other.Id,default));
        Assert.Equal("not_proof",(await repository.FindAsync(profile.Id,default))!.Username);
        Assert.Null((await repository.FindAsync(profile.Id,default))!.RobloxUserId);
        Assert.Equal(2,handler.Requests);
        Assert.DoesNotContain("synthetic-access",authorization.ToString());
        Assert.DoesNotContain("synthetic-refresh",authorization.ToString());
    }
    [Fact]
    public async Task DifferentIdentityOrDeletedProfileCannotReplaceAnExistingLink()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default);
        var vault=new MemoryVault(); var existing=Record(profile.Id); vault.Records[profile.Id]=existing;
        using var http=new HttpClient(new SyntheticRobloxHandler { UserId=999 });
        using(var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser()))
            await Assert.ThrowsAsync<AccountLinkException>(()=>service.LinkAsync(profile.Id,"12345",47635,default));
        Assert.Equal(existing,vault.Records[profile.Id]);
        using var correctHttp=new HttpClient(new SyntheticRobloxHandler());
        using var deleting=new AccountLinkService(repository,vault,new(correctHttp),new SyntheticBrowser(async ()=>await repository.DeleteAsync(profile.Id,profile.Revision,default)));
        await Assert.ThrowsAsync<AccountLinkException>(()=>deleting.LinkAsync(profile.Id,"12345",47635,default));
        Assert.Equal(existing,vault.Records[profile.Id]);
    }
    [Fact]
    public async Task UncertainRefreshIsJournaledBeforeNetworkAndCannotReuseItsOneTimeToken()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default); var vault=new MemoryVault(); vault.Records[profile.Id]=Record(profile.Id);
        var handler=new SyntheticRobloxHandler { Failure=new HttpRequestException("synthetic transport failure") };
        using var http=new HttpClient(handler); using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.RefreshAsync(profile.Id,"12345",default));
        Assert.Equal(AuthorizationHealth.RefreshPending,vault.Records[profile.Id].Health); Assert.Equal(2,vault.Records[profile.Id].Generation);
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.RefreshAsync(profile.Id,"12345",default));
        Assert.Equal(1,handler.Requests);
    }
    [Fact]
    public async Task RefreshSaveFailureKeepsDurablePendingStateAndExistingProfile()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default); var vault=new MemoryVault { FailOnSave=2 }; vault.Records[profile.Id]=Record(profile.Id);
        using var http=new HttpClient(new SyntheticRobloxHandler()); using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        await Assert.ThrowsAsync<IOException>(()=>service.RefreshAsync(profile.Id,"12345",default));
        Assert.Equal(AuthorizationHealth.RefreshPending,vault.Records[profile.Id].Health);
        Assert.NotNull(await repository.FindAsync(profile.Id,default));
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.RefreshAsync(profile.Id,"12345",default));
    }
    [Fact]
    public async Task SuccessfulRefreshRotatesAndDisconnectRemovesLocalLinkEvenIfRevocationFails()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default); var vault=new MemoryVault(); vault.Records[profile.Id]=Record(profile.Id);
        var handler=new SyntheticRobloxHandler(); using var http=new HttpClient(handler); using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        await service.RefreshAsync(profile.Id,"12345",default);
        Assert.Equal(3,vault.Records[profile.Id].Generation); Assert.Equal(AuthorizationHealth.Linked,vault.Records[profile.Id].Health);
        Assert.Equal("synthetic-refresh-new",vault.Records[profile.Id].RefreshToken);
        handler.Failure=new HttpRequestException("offline");
        Assert.False(await service.DisconnectAsync(profile.Id,"12345",default));
        Assert.Empty(vault.Records); Assert.NotNull(await repository.FindAsync(profile.Id,default));
    }
    [Fact]
    public async Task CancelledLateBrowserCallbackCannotWriteAuthorization()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default); var vault=new MemoryVault();
        using var stop=new CancellationTokenSource(); using var http=new HttpClient(new SyntheticRobloxHandler());
        using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser(()=> { stop.Cancel(); return Task.CompletedTask; }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.LinkAsync(profile.Id,"12345",47635,stop.Token));
        Assert.Empty(vault.Records);
    }
    [Fact]
    public async Task MalformedOversizedAndInsufficientScopeResponsesNeverProduceAuthorization()
    {
        foreach(string payload in new[] { "not JSON", "[]", new string('a',70000), "{\"access_token\":\"token\",\"refresh_token\":\"refresh\",\"token_type\":\"Bearer\",\"expires_in\":900,\"scope\":\"openid\"}" })
        {
            using var http=new HttpClient(new SyntheticRobloxHandler { TokenResponse=payload }); var client=new RobloxOAuthClient(http); var attempt=new OAuthAttempt("12345",47635);
            await Assert.ThrowsAsync<AccountLinkException>(()=>client.ExchangeAsync(attempt,"code",default));
        }
    }
    [Fact]
    public async Task RobloxAdapterRetainsOpaqueSubjectAndMapsUserIdOnlyFromDocumentedUserInfo()
    {
        using var http=new HttpClient(new SyntheticRobloxHandler { IdentityResponse="{\"sub\":\"000456\",\"preferred_username\":\"verified_player\"}" });
        var identity=await new RobloxOAuthClient(http).IdentityAsync("synthetic-access-new",default);
        Assert.Equal(456,identity.UserId);
        Assert.Equal("000456",identity.Subject);
        Assert.Equal("https://apis.roblox.com/oauth/",identity.Issuer);
    }
    [Theory]
    [InlineData("{\"sub\":\"opaque-subject\",\"preferred_username\":\"verified_player\"}")]
    [InlineData("{\"sub\":\"9223372036854775808\",\"preferred_username\":\"verified_player\"}")]
    [InlineData("{\"sub\":\"456\",\"preferred_username\":\"verified_player\",\"iss\":\"https://other.invalid/\"}")]
    [InlineData("{\"sub\":\"456\",\"sub\":\"999\",\"preferred_username\":\"verified_player\"}")]
    public async Task UnexpectedIdentitySchemaCannotInventAUserIdOrTrustADifferentIssuer(string payload)
    {
        using var http=new HttpClient(new SyntheticRobloxHandler { IdentityResponse=payload });
        await Assert.ThrowsAsync<AccountLinkException>(()=>new RobloxOAuthClient(http).IdentityAsync("synthetic-access-new",default));
    }
    [Fact]
    public async Task SameNumericUserWithDifferentContextCannotReplaceOrRefreshTheExistingBinding()
    {
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default);
        var vault=new MemoryVault();var existing=Record(profile.Id) with { Subject="000456" };vault.Records[profile.Id]=existing;
        using var http=new HttpClient(new SyntheticRobloxHandler());using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.LinkAsync(profile.Id,"12345",47635,default));
        Assert.Equal(existing,vault.Records[profile.Id]);
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.RefreshAsync(profile.Id,"12345",default));
        Assert.Equal(AuthorizationHealth.RefreshPending,vault.Records[profile.Id].Health);
        Assert.Equal("000456",vault.Records[profile.Id].Subject);
        Assert.Equal("old-refresh",vault.Records[profile.Id].RefreshToken);
    }
    [Fact]
    public async Task DuplicateContextualIdentityIsRejectedWithoutDecryptingTheInventory()
    {
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        var target=await repository.SaveAsync(AccountProfile.Create("Target"),default);
        var existing=await repository.SaveAsync(AccountProfile.Create("Existing"),default);
        var vault=new MemoryVault();vault.Records[existing.Id]=Record(existing.Id);
        using var http=new HttpClient(new SyntheticRobloxHandler());using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.LinkAsync(target.Id,"12345",47635,default));
        Assert.False(vault.Records.ContainsKey(target.Id));
        Assert.Equal(1,vault.Reads);Assert.Equal(1,vault.IdentityLookups);
        Assert.Equal(2,(await repository.ReadAsync(new(),default)).Total);
    }
    [Fact]
    public async Task LegacyContextRequiresSignInAndExplicitRelinkPreservesTheHistoricalUserBinding()
    {
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("Legacy"),default);
        var legacy=Record(profile.Id) with { Issuer="",Subject="",AccessToken="legacy-access",RefreshToken="legacy-refresh" };
        var normalized=legacy.Validated();
        Assert.Equal(AuthorizationHealth.SignInRequired,normalized.Health);
        Assert.Empty(normalized.AccessToken);Assert.Empty(normalized.RefreshToken);
        var vault=new MemoryVault();vault.Records[profile.Id]=legacy;
        var handler=new SyntheticRobloxHandler();using var http=new HttpClient(handler);using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.RefreshAsync(profile.Id,"12345",default));
        Assert.Equal(0,handler.Requests);
        await service.LinkAsync(profile.Id,"12345",47635,default);
        Assert.Equal(RobloxOAuthIdentity.Issuer,vault.Records[profile.Id].Issuer);
        Assert.Equal("456",vault.Records[profile.Id].Subject);
        Assert.Equal(456,vault.Records[profile.Id].VerifiedUserId);
        Assert.Empty(vault.Records[profile.Id].AccessToken);
    }
    [Fact]
    public void RefreshOnlyContextIsValidButPartialOrForeignContextIsRejected()
    {
        var record=Record(Guid.NewGuid());
        Assert.Same(record,record.Validated());
        Assert.Throws<ArgumentException>(()=>(record with { Issuer="https://other.invalid/" }).Validated());
        Assert.Throws<ArgumentException>(()=>(record with { Subject="" }).Validated());
        Assert.Throws<ArgumentException>(()=>(record with { RefreshToken="" }).Validated());
    }
    [Fact]
    public async Task DisconnectImmediatelyFencesALateBrowserCallbackAndRejectsConcurrentOperations()
    {
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default);
        var vault=new MemoryVault();var handler=new SyntheticRobloxHandler();using var http=new HttpClient(handler);
        var browser=new GatedBrowser();using var service=new AccountLinkService(repository,vault,new(http),browser);
        var link=service.LinkAsync(profile.Id,"12345",47635,default);
        await browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.LinkAsync(profile.Id,"12345",47635,default));
        var disconnect=service.DisconnectAsync(profile.Id,"12345",default);
        Assert.True(browser.Token.IsCancellationRequested);
        Assert.False(disconnect.IsCompleted);
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.RefreshAsync(profile.Id,"12345",default));
        browser.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>link);
        Assert.True(await disconnect.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(vault.Records);Assert.Equal(0,handler.Requests);
        Assert.NotNull(await repository.FindAsync(profile.Id,default));
    }
    [Fact]
    public async Task DisconnectDuringRefreshCannotRestoreARotatedGrantAfterALateNetworkResponse()
    {
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default);
        var vault=new MemoryVault();vault.Records[profile.Id]=Record(profile.Id);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed=default;
        var handler=new SyntheticRobloxHandler { BeforeResponse=async (request,ct)=>
        {
            if(request.RequestUri!.AbsolutePath.EndsWith("/token",StringComparison.Ordinal))
            { observed=ct;started.TrySetResult();await release.Task; }
        } };
        using var http=new HttpClient(handler);using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        var refresh=service.RefreshAsync(profile.Id,"12345",default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AuthorizationHealth.RefreshPending,vault.Records[profile.Id].Health);
        var disconnect=service.DisconnectAsync(profile.Id,"12345",default);
        Assert.True(observed.IsCancellationRequested);
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>refresh);
        await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(vault.Records);
        Assert.NotNull(await repository.FindAsync(profile.Id,default));
    }
    [Fact]
    public async Task AStorageCommitThatIgnoresCancellationIsDeletedBeforeDisconnectCompletes()
    {
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vault=new MemoryVault { BeforeSave=async ()=> { started.TrySetResult();await release.Task; } };
        using var http=new HttpClient(new SyntheticRobloxHandler());using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        var link=service.LinkAsync(profile.Id,"12345",47635,default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disconnect=service.DisconnectAsync(profile.Id,"12345",default);
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>link);
        await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(vault.Records);
    }
    [Fact]
    public async Task DisconnectAndNewServiceRelinkNeverReuseAnEarlierGrantGeneration()
    {
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default);
        var vault=new MemoryVault();using var http=new HttpClient(new SyntheticRobloxHandler());
        long first;
        using(var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser()))
        {
            await service.LinkAsync(profile.Id,"12345",47635,default);
            first=vault.Records[profile.Id].Generation;
            Assert.True(await service.DisconnectAsync(profile.Id,"12345",default));
        }
        Assert.Empty(vault.Records);
        using(var reopenedService=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser()))
            await reopenedService.LinkAsync(profile.Id,"12345",47635,default);
        Assert.True(vault.Records[profile.Id].Generation>first);
        Assert.Equal(vault.Generations[profile.Id],vault.Records[profile.Id].Generation);
    }
    [Fact]
    public async Task InterruptedGenerationReservationCannotBeReusedByALaterRelink()
    {
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default);
        var vault=new MemoryVault { FailOnSave=1 };using var http=new HttpClient(new SyntheticRobloxHandler());
        using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        await Assert.ThrowsAsync<IOException>(()=>service.LinkAsync(profile.Id,"12345",47635,default));
        long abandoned=vault.Generations[profile.Id];
        Assert.Empty(vault.Records);
        await service.LinkAsync(profile.Id,"12345",47635,default);
        Assert.True(vault.Records[profile.Id].Generation>abandoned);
    }
    [Fact]
    public async Task NonIncreasingGenerationAllocatorCannotCommitAuthorization()
    {
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default);
        var vault=new MemoryVault { ReturnInvalidGeneration=true };using var http=new HttpClient(new SyntheticRobloxHandler());
        using var service=new AccountLinkService(repository,vault,new(http),new SyntheticBrowser());
        await Assert.ThrowsAsync<AccountLinkException>(()=>service.LinkAsync(profile.Id,"12345",47635,default));
        Assert.Empty(vault.Records);
    }
    private static AuthorizationRecord Record(Guid id)=>new(id,"roblox-oauth",456,1,"","old-refresh",DateTimeOffset.UtcNow.AddMinutes(10),"verified_player",
        Issuer:RobloxOAuthIdentity.Issuer,Subject:"456");
    [Fact]
    public async Task NetworkDeadlineReportsFailureWhileUserCancellationRemainsCancellation()
    {
        using var http=new HttpClient(new DelayedHandler()); var client=new RobloxOAuthClient(http,TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAsync<AccountLinkException>(()=>client.RefreshAsync("12345","synthetic",default));
        using var stop=new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.RefreshAsync("12345","synthetic",stop.Token));
    }
    private sealed class DelayedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { await Task.Delay(Timeout.InfiniteTimeSpan,ct); throw new InvalidOperationException("The synthetic network request must be cancelled."); }
    }
    private sealed class MemoryVault : IAccountCredentialVault,IAuthorizationMetadataReader,IAuthorizationGenerationStore
    {
        public readonly Dictionary<Guid,AuthorizationRecord> Records=[]; public int FailOnSave,Reads,IdentityLookups; private int _saves;
        public readonly Dictionary<Guid,long> Generations=[];public bool ReturnInvalidGeneration;
        public Func<Task>? BeforeSave;
        public Task<AuthorizationRecord?> ReadAsync(Guid id,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Reads++;var record=Records.GetValueOrDefault(id);
            if(record is not null && Generations.GetValueOrDefault(id)>record.Generation)
                record=record with { Health=AuthorizationHealth.SignInRequired,AccessToken="",RefreshToken="" };
            return Task.FromResult(record);
        }
        public async Task SaveAsync(AuthorizationRecord value,CancellationToken ct)
        { ct.ThrowIfCancellationRequested();value=value.Validated();if(++_saves==FailOnSave) throw new IOException("Synthetic interrupted replacement");if(BeforeSave is not null) await BeforeSave();Records[value.ProfileId]=value with { AccessToken="" }; }
        public Task DeleteAsync(Guid id,CancellationToken ct)
        { ct.ThrowIfCancellationRequested();Generations[id]=checked(Math.Max(Generations.GetValueOrDefault(id),Records.GetValueOrDefault(id)?.Generation??0)+1);Records.Remove(id);return Task.CompletedTask; }
        public Task<long> AllocateNextGenerationAsync(Guid profileId,long minimumExclusive,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(ReturnInvalidGeneration) return Task.FromResult(minimumExclusive);
            long next=checked(Math.Max(minimumExclusive,Math.Max(Generations.GetValueOrDefault(profileId),Records.GetValueOrDefault(profileId)?.Generation??0))+1);
            Generations[profileId]=next;return Task.FromResult(next);
        }
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string provider,string issuer,string subject,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();IdentityLookups++;
            var record=Records.Values.SingleOrDefault(r=>r.Provider==provider && r.Issuer==issuer && r.Subject==subject);
            return Task.FromResult(record is null?null:Metadata(record));
        }
        public Task<AuthorizationMetadata?> GetSummaryAsync(Guid id,CancellationToken ct)
        { ct.ThrowIfCancellationRequested();return Task.FromResult(Records.TryGetValue(id,out var record)?Metadata(record):null); }
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> profileIds,CancellationToken ct)
            =>throw new InvalidOperationException("Linking must not enumerate inventory metadata or decrypt other profiles.");
        private static AuthorizationMetadata Metadata(AuthorizationRecord record)=>new(record.ProfileId,"synthetic-ref",record.Provider,record.Issuer,record.Subject,
            record.VerifiedUserId,record.VerifiedUsername,record.Generation,record.Health,record.ExpiresUtc,DateTimeOffset.UtcNow);
    }
    private sealed class SyntheticBrowser(Func<Task>? action=null) : IOAuthBrowserFlow
    { public async Task<Uri> AuthorizeAsync(OAuthAttempt attempt,CancellationToken ct) { if(action is not null) await action(); return Callback(attempt); } }
    private sealed class GatedBrowser : IOAuthBrowserFlow
    {
        public TaskCompletionSource Started { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public async Task<Uri> AuthorizeAsync(OAuthAttempt attempt,CancellationToken ct)
        { Token=ct;Started.TrySetResult();await Release.Task;return Callback(attempt); }
    }
    private sealed class SyntheticRobloxHandler : HttpMessageHandler
    {
        public int Requests; public long UserId=456; public Exception? Failure; public string? TokenResponse,IdentityResponse;
        public Func<HttpRequestMessage,CancellationToken,Task>? BeforeResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests++; ct.ThrowIfCancellationRequested(); if(Failure is not null) throw Failure;
            if(BeforeResponse is not null) await BeforeResponse(request,ct);
            Assert.Equal("https",request.RequestUri!.Scheme); Assert.Equal("apis.roblox.com",request.RequestUri.Host);
            Assert.False(request.Headers.Contains("Cookie"));
            if(request.RequestUri.AbsolutePath.EndsWith("/userinfo",StringComparison.Ordinal))
            {
                Assert.Equal("Bearer",request.Headers.Authorization?.Scheme); Assert.Equal("synthetic-access-new",request.Headers.Authorization?.Parameter);
                return Json(IdentityResponse??$"{{\"sub\":\"{UserId}\",\"preferred_username\":\"verified_player\"}}");
            }
            string form=await request.Content!.ReadAsStringAsync(ct); Assert.DoesNotContain("client_secret",form);
            if(form.Contains("authorization_code",StringComparison.Ordinal))
            {
                Assert.Contains("code_verifier=",form); Assert.Contains("redirect_uri=",form);
                string verifier=Uri.UnescapeDataString(form.Split('&').Single(p=>p.StartsWith("code_verifier=",StringComparison.Ordinal)).Split('=',2)[1]);
                Assert.InRange(verifier.Length,43,128); Assert.DoesNotContain('+',verifier);
            }
            return Json(TokenResponse??"{\"access_token\":\"synthetic-access-new\",\"refresh_token\":\"synthetic-refresh-new\",\"token_type\":\"Bearer\",\"expires_in\":899,\"scope\":\"openid profile\"}");
        }
        private static HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK) { Content=new StringContent(value,Encoding.UTF8,"application/json") };
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-authorization-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
