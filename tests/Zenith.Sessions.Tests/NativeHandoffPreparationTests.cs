using System.Globalization;
using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Real isolated profile/DPAPI stores; all browser, HTTP, ticket and native start values are synthetic.
public sealed partial class NativeHandoffPreparationTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-owned-preparation-"+Guid.NewGuid().ToString("N"));
    private const string Original="synthetic-preparation-original",Current="synthetic-preparation-current",Raw="synthetic-secret-must-not-escape";
    private static LinkedIdentity Identity(long user=123)=>new(user,"verified_player",RobloxSessionIdentity.Issuer,user.ToString(CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task OwnBrowserCaptureAndSamePlayerVerificationReturnOnlyThisOperationsFinalGrant(bool job,bool projection)
    {
        await using var f=await Fixture.Open(_root,projection);using var browser=new Browser();bool cleaned=false;
        browser.Run=async(context,input,ct)=>
        {
            Assert.Equal(Original,input.CopyValue());Assert.Equal(f.Intent.OperationId,context.LaunchOperationId);
            var pending=await f.Vault.ReadAsync(f.Profile.Id,ct);
            Assert.Equal(2,pending!.Generation);Assert.Equal(AuthorizationHealth.SignInRequired,pending.Health);
            cleaned=true;return browser.Success(context);
        };
        f.Client.Reply=(_,_)=>{Assert.True(cleaned);return Task.FromResult(new RobloxSessionValidation(Identity()));};
        var requested=job?f.Intent with{RequestedJobId=SyntheticHandoff.Job,RequestedServerKind=RobloxServerKind.Public}:f.Intent;
        var result=await f.Service.PrepareNativeHandoffAsync(requested,browser,default);
        Assert.Equal(RobloxHandoffProbeOutcome.CapturedAndPrevented,result.BrowserFacts?.Outcome);
        Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar,result.BrowserFacts?.ShapeStatus);Assert.NotNull(result.BrowserFacts?.ObservedUtc);
        Assert.Equal(NativeHandoffPreparationStatus.Ready,result.Status);using var prepared=Assert.IsType<NativeHandoffPreparation>(result.Preparation);
        var saved=await f.Vault.ReadWriteSnapshotAsync(f.Profile.Id,default);
        Assert.Equal(Current,saved.Record!.SessionCookie);Assert.Equal(3,saved.Record.Generation);
        Assert.Equal(prepared.Candidate.Context.OperationId,saved.HighestMutationId);Assert.Equal(saved,prepared.FinalGrant);
        Assert.Equal(requested.OperationId,prepared.Intent.OperationId);Assert.Equal(requested.RequestedJobId,prepared.Intent.RequestedJobId);
        Assert.Equal(3,prepared.Intent.AuthorizationGeneration);Assert.Equal(f.Profile.Revision+(projection?1:0),prepared.Intent.ProfileRevision);
        Assert.Equal(f.Profile.Label,(await f.Repository.FindAsync(f.Profile.Id,default))!.Label);
        Assert.Equal(new[]{Current},f.Client.Seen);Assert.Equal(1,browser.Calls);Disposed(browser.Input);Disposed(browser.Issued);
        Assert.Null(browser.Returned!.Candidate);Assert.Null(browser.Returned.CurrentCredential);Assert.False(prepared.Candidate.IsConsumed);
        string text=JsonSerializer.Serialize(result)+JsonSerializer.Serialize(prepared)+result+prepared;
        foreach(string secret in new[]{Original,Current,SyntheticHandoff.Ticket})Assert.DoesNotContain(secret,text);
        // The exact prepared binding can reach the existing final dispatch boundary; startup is fake.
        using var starter=new Starter();await using var coordinator=new NativeHandoffCoordinator(f.Vault);
        var attempt=await coordinator.DispatchAsync(prepared.Candidate,prepared.FinalGrant,prepared.Intent,starter,
            new LaunchAuthorizationValidator(f.Repository,f.Vault).ValidateAsync,()=>true,()=>new(true,"Synthetic owned reservation"),default);
        Assert.Equal(NativeHandoffAttemptStatus.StartedUnverified,attempt.Status);Assert.Equal(1,starter.Calls);
    }

    [Theory]
    [InlineData("user")] [InlineData("generation")] [InlineData("revision")] [InlineData("target")]
    [InlineData("missing")] [InlineData("unlinked")] [InlineData("expired")]
    public async Task StaleOrIneligibleRequestNeverOpensBrowserOrSendsCredential(string kind)
    {
        await using var f=await Fixture.Open(_root);using var browser=new Browser();var intent=kind switch
        {
            "user"=>f.Intent with{UserId=999},"generation"=>f.Intent with{AuthorizationGeneration=2},
            "revision"=>f.Intent with{ProfileRevision=f.Profile.Revision+1},"target"=>f.Intent with{HasExplicitDestination=false},_=>f.Intent
        };
        if(kind=="missing")await f.Vault.DeleteAsync(f.Profile.Id,default);
        if(kind is "unlinked" or "expired")
        {
            var snapshot=await f.Vault.ReadWriteSnapshotAsync(f.Profile.Id,default);
            await f.Vault.TryCommitSnapshotAsync(snapshot,snapshot.Record! with{Generation=snapshot.NextGeneration,
                Health=kind=="unlinked"?AuthorizationHealth.SignInRequired:AuthorizationHealth.Linked,
                ExpiresUtc=kind=="expired"?DateTimeOffset.UtcNow.AddDays(-1):DateTimeOffset.MaxValue},Guid.NewGuid(),default);
            intent=intent with{AuthorizationGeneration=snapshot.NextGeneration};
        }
        var before=await f.Vault.ReadWriteSnapshotAsync(f.Profile.Id,default);
        var result=await f.Service.PrepareNativeHandoffAsync(intent,browser,default);
        Assert.NotEqual(NativeHandoffPreparationStatus.Ready,result.Status);Assert.Null(result.Preparation);
        Assert.Null(result.BrowserFacts);
        Assert.Equal(0,browser.Calls);Assert.Empty(f.Client.Seen);Assert.Equal(before,await f.Vault.ReadWriteSnapshotAsync(f.Profile.Id,default));
    }

    [Theory]
    [InlineData("no-candidate")] [InlineData("foreign-context")] [InlineData("expired-candidate")]
    [InlineData("no-cookie")] [InlineData("expired-cookie")] [InlineData("cleanup")]
    public async Task InvalidOrUnfinishedCaptureCannotRestoreAuthority(string kind)
    {
        await using var f=await Fixture.Open(_root);using var browser=new Browser();NativeHandoffCandidate? issued=null;
        browser.Run=(context,_,_)=>
        {
            if(kind=="cleanup")return Task.FromResult(new NativeBrowserCapture(RobloxHandoffProbeOutcome.CleanupFailed));
            var clock=new HandoffClock();var bound=kind=="foreign-context"?new NativeHandoffContext(context.LaunchOperationId,context.ProfileId,context.ExpectedUserId,context.PendingAuthorizationGeneration,context.Destination):context;
            issued=kind=="no-candidate"?null:SyntheticHandoff.Capture(bound,clock);
            if(kind=="expired-candidate")clock.Advance(TimeSpan.FromSeconds(10));
            return Task.FromResult(new NativeBrowserCapture(RobloxHandoffProbeOutcome.CapturedAndPrevented,issued,
                kind=="no-cookie"?null:new(Current,kind=="expired-cookie"?DateTimeOffset.UtcNow.AddDays(-1):null)));
        };
        var result=await f.Service.PrepareNativeHandoffAsync(f.Intent,browser,default);
        Assert.NotEqual(NativeHandoffPreparationStatus.Ready,result.Status);Assert.Null(result.Preparation);Assert.Empty(f.Client.Seen);
        Assert.Equal(AuthorizationHealth.SignInRequired,(await f.Vault.ReadAsync(f.Profile.Id,default))!.Health);
        if(issued is not null)Assert.True(issued.IsConsumed);Disposed(browser.Input);if(browser.Issued is not null)Disposed(browser.Issued);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WrongCurrentOrReplacementPlayerCannotAuthorizeTheSelectedProfile(bool rotated)
    {
        await using var f=await Fixture.Open(_root);using var browser=new Browser();int calls=0;
        f.Client.Reply=(_,_)=>Task.FromResult(rotated&&++calls==1?new RobloxSessionValidation(Identity(),new("synthetic-rotated-preparation")):new RobloxSessionValidation(Identity(999)));
        var result=await f.Service.PrepareNativeHandoffAsync(f.Intent,browser,default);
        Assert.Equal(NativeHandoffPreparationStatus.IdentityMismatch,result.Status);Assert.Null(result.Preparation);
        var pending=await f.Vault.ReadAsync(f.Profile.Id,default);Assert.Equal(123,pending!.VerifiedUserId);Assert.Equal(AuthorizationHealth.SignInRequired,pending.Health);
    }

    [Theory]
    [InlineData("browser")] [InlineData("validation")]
    public async Task ForeignNewerGrantSurvivesAnObsoletePreparationsCleanup(string at)
    {
        await using var f=await Fixture.Open(_root);using var browser=new Browser();AuthorizationWriteSnapshot? foreign=null;
        async Task Replace()
        {
            var snapshot=await f.Vault.ReadWriteSnapshotAsync(f.Profile.Id,default);
            await f.Vault.TryCommitSnapshotAsync(snapshot,snapshot.Record! with{Generation=snapshot.NextGeneration,Health=AuthorizationHealth.Linked,
                SessionCookie="synthetic-foreign-current"},Guid.NewGuid(),default);
            foreign=await f.Vault.ReadWriteSnapshotAsync(f.Profile.Id,default);
        }
        if(at=="browser")browser.Run=async(context,_,_)=>{await Replace();return browser.Success(context);};
        else f.Client.Reply=async(_,_)=>{await Replace();return new(Identity());};
        var result=await f.Service.PrepareNativeHandoffAsync(f.Intent,browser,default);
        Assert.Equal(NativeHandoffPreparationStatus.AuthorizationChanged,result.Status);Assert.Null(result.Preparation);
        Assert.NotNull(foreign);Assert.Equal(foreign,await f.Vault.ReadWriteSnapshotAsync(f.Profile.Id,default));
        if(at=="browser")Assert.Empty(f.Client.Seen);
    }

    [Theory]
    [InlineData("browser")] [InlineData("validation")]
    public async Task UserEditsNeverSilentlyRebaseAPreparedLaunch(string at)
    {
        await using var f=await Fixture.Open(_root,true);using var browser=new Browser();
        async Task Edit(){var p=(await f.Repository.FindAsync(f.Profile.Id,default))!;await f.Repository.SaveAsync(p with{Notes="New intent must survive"},default);}
        if(at=="browser")browser.Run=async(context,_,_)=>{await Edit();return browser.Success(context);};
        else f.Client.Reply=async(_,_)=>{await Edit();return new(Identity());};
        var result=await f.Service.PrepareNativeHandoffAsync(f.Intent,browser,default);
        Assert.Equal(NativeHandoffPreparationStatus.RequestChanged,result.Status);Assert.Null(result.Preparation);
        Assert.Equal("New intent must survive",(await f.Repository.FindAsync(f.Profile.Id,default))!.Notes);
        Assert.Equal(AuthorizationHealth.SignInRequired,(await f.Vault.ReadAsync(f.Profile.Id,default))!.Health);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationOrDisconnectJoinsTheActualBrowserAndDisposesItsLateCapture(bool disconnect)
    {
        await using var f=await Fixture.Open(_root);using var browser=new Browser();using var stop=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Run=async(context,_,_)=>{var captured=browser.Success(context);entered.TrySetResult();await release.Task;return captured;};
        var work=f.Service.PrepareNativeHandoffAsync(f.Intent,browser,stop.Token);Task? disconnected=null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var busy=await f.Service.PrepareNativeHandoffAsync(f.Intent,browser,default);Assert.Equal(NativeHandoffPreparationStatus.Busy,busy.Status);
            if(disconnect)disconnected=f.Service.DisconnectAsync(f.Profile.Id,default);else stop.Cancel();
            Assert.False(work.IsCompleted);if(disconnected is not null)Assert.False(disconnected.IsCompleted);
        }
        finally{release.TrySetResult();}
        var result=await work.WaitAsync(TimeSpan.FromSeconds(5));if(disconnected is not null)await disconnected.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NativeHandoffPreparationStatus.Cancelled,result.Status);Assert.Empty(f.Client.Seen);
        Assert.Equal(RobloxHandoffProbeOutcome.CapturedAndPrevented,result.BrowserFacts?.Outcome);
        Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar,result.BrowserFacts?.ShapeStatus);Assert.NotNull(result.BrowserFacts?.ObservedUtc);
        Assert.Null(browser.Returned!.Candidate);Disposed(browser.Input);Disposed(browser.Issued);
        if(disconnect)Assert.Null(await f.Vault.ReadAsync(f.Profile.Id,default));
    }

    [Theory]
    [InlineData(RobloxHandoffProbeOutcome.CleanupFailed,NativeHandoffPreparationStatus.CleanupFailed)]
    [InlineData(RobloxHandoffProbeOutcome.Busy,NativeHandoffPreparationStatus.Busy)]
    [InlineData(RobloxHandoffProbeOutcome.StorageUnavailable,NativeHandoffPreparationStatus.StorageUnavailable)]
    public async Task RefusedCaptureNeverOpensBrowserOrChangesTheLinkedGrant(RobloxHandoffProbeOutcome refusal,NativeHandoffPreparationStatus expected)
    {
        // CleanupFailed must stay CleanupFailed so the configured provider still poisons itself.
        await using var f=await Fixture.Open(_root);
        using var browser=new Browser{Refusal=refusal,Run=(_,_,_)=>throw new InvalidOperationException("A refused capture was started.")};
        var before=await f.Vault.ReadWriteSnapshotAsync(f.Profile.Id,default);
        var result=await f.Service.PrepareNativeHandoffAsync(f.Intent,browser,default);
        var after=await f.Vault.ReadWriteSnapshotAsync(f.Profile.Id,default);
        Assert.Equal(AuthorizationHealth.Linked,after.Record!.Health);Assert.Equal(before,after);
        Assert.Equal(expected,result.Status);Assert.Null(result.Preparation);Assert.Null(result.BrowserFacts);
        Assert.Equal(0,browser.Calls);Assert.Empty(f.Client.Seen);
    }

    [Fact]
    public async Task CancellationFromLateValidationCannotPublishAReadyPreparation()
    {
        await using var f=await Fixture.Open(_root);using var browser=new Browser();
        f.Client.Reply=(_,_)=>{f.Service.Cancel();return Task.FromResult(new RobloxSessionValidation(Identity()));};
        var result=await f.Service.PrepareNativeHandoffAsync(f.Intent,browser,default);
        Assert.Equal(NativeHandoffPreparationStatus.Cancelled,result.Status);Assert.Null(result.Preparation);
        Assert.Null(browser.Returned!.Candidate);Disposed(browser.Issued);
        Assert.Equal(AuthorizationHealth.SignInRequired,(await f.Vault.ReadAsync(f.Profile.Id,default))!.Health);
    }

    [Fact]
    public async Task ArbitraryProviderErrorIsRedactedAndThePendingGrantStaysFenced()
    {
        await using var f=await Fixture.Open(_root);using var browser=new Browser{Run=(_,_,_)=>throw new IOException(Raw)};
        var result=await f.Service.PrepareNativeHandoffAsync(f.Intent,browser,default);
        Assert.Equal(NativeHandoffPreparationStatus.Failed,result.Status);Assert.DoesNotContain(Raw,JsonSerializer.Serialize(result)+result);
        Assert.Equal(AuthorizationHealth.SignInRequired,(await f.Vault.ReadAsync(f.Profile.Id,default))!.Health);
    }

    private static void Disposed(RobloxSessionCredential? credential){Assert.NotNull(credential);Assert.Throws<ObjectDisposedException>(()=>credential!.CopyValue());}
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly Guid _launchId=Guid.NewGuid();
        public required AccountRepository Repository;public required ProtectedCredentialVault Vault;public required AccountProfile Profile;
        public required RobloxSessionLinkService Service;public required Client Client;public AccountIdentityProjection? Projection;
        public LaunchIntent Intent=>new(_launchId,Profile.Id,Profile.Revision,123,1818,0,DateTimeOffset.UtcNow,1,HasExplicitDestination:true);
        public static async Task<Fixture> Open(string root,bool projection=false)
        {
            var repository=new AccountRepository(root);await repository.InitializeAsync(default);
            var profile=await repository.SaveAsync(AccountProfile.Create("Manual alias") with{Notes="Keep notes"},default);
            var vault=new ProtectedCredentialVault(root);await vault.SaveAsync(new(profile.Id,RobloxSessionIdentity.Provider,123,1,"","",DateTimeOffset.MaxValue,
                "old_player",AuthorizationHealth.Linked,RobloxSessionIdentity.Issuer,"123",Original),default);
            var metadata=projection?new AccountIdentityProjection(root,repository,vault):null;var client=new Client();
            return new(){Repository=repository,Vault=vault,Profile=profile,Client=client,Projection=metadata,Service=new(repository,vault,client,new NoSignIn(),metadata)};
        }
        public async ValueTask DisposeAsync(){await Service.DisposeAsync();Projection?.Dispose();Vault.Dispose();Repository.Dispose();}
    }
    private sealed class Client:IRobloxSessionClient
    {
        public readonly List<string> Seen=[];public Func<RobloxSessionCredential,CancellationToken,Task<RobloxSessionValidation>>? Reply;
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential c,CancellationToken ct){Seen.Add(c.CopyValue());return Reply?.Invoke(c,ct)??Task.FromResult(new RobloxSessionValidation(Identity()));}
    }
    private sealed class NoSignIn:IRobloxSessionSignIn
    {public Task<RobloxSessionCredential> SignInAsync(Guid id,CancellationToken ct)=>throw new InvalidOperationException("Interactive sign-in was not requested.");}
    private sealed class Browser:IRobloxNativeHandoffCapture,IDisposable
    {
        public int Calls;public RobloxSessionCredential? Input,Issued;public NativeBrowserCapture? Returned;
        public RobloxHandoffProbeOutcome? Refusal{get;set;}
        public Func<NativeHandoffContext,RobloxSessionCredential,CancellationToken,Task<NativeBrowserCapture>>? Run;
        public NativeBrowserCapture Success(NativeHandoffContext context)=>new(RobloxHandoffProbeOutcome.CapturedAndPrevented,SyntheticHandoff.Capture(context,new HandoffClock()),new(Current),NativeHandoffCaptureStatus.AcceptedSourceGrammar);
        public async Task<NativeBrowserCapture> CaptureAsync(NativeHandoffContext context,RobloxSessionCredential c,CancellationToken ct)
        {Calls++;Input=c;Returned=Run is null?Success(context):await Run(context,c,ct);Issued=Returned.CurrentCredential;return Returned;}
        public void Dispose()=>Returned?.Dispose();public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
    private sealed class Starter:IPreparedNativeHandoffStart
    {
        public int Calls;
        public NativeHandoffDispatchResult Start(string value){Calls++;return new(NativeHandoffDispatchOutcome.StartedUnverified,new(12345,DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks,DateTimeOffset.UtcNow));}
        public void Dispose(){}
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(full).StartsWith("zenith-owned-preparation-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
