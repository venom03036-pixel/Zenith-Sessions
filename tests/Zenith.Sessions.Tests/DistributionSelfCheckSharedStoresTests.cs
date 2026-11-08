using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Shared stores are released after recording the stage failure, except when a worker is still active after a
// bounded shutdown. In that case the self-check process keeps them alive until exit (O-C11-1).
public sealed class DistributionSelfCheckSharedStoresTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-selfcheck-shared-stores-"+Guid.NewGuid().ToString("N"));
    private AccountRepository? _repository;private ProtectedCredentialVault? _vault;

    [Fact]
    public async Task AStartThatStallsInsideTheVaultIsReportedAtTheCoordinatorsShutdownNotAtTheVaultsRefusedDisposal()
    {
        var repository=_repository=new AccountRepository(_root);var vault=_vault=new ProtectedCredentialVault(_root);
        await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("Synthetic stalled handoff") with { PreferredPlaceId=1818 },default);
        var context=new NativeHandoffContext(Guid.NewGuid(),profile.Id,123,1,new(1818,null,RobloxServerKind.Unknown));
        var record=new AuthorizationRecord(profile.Id,RobloxSessionIdentity.Provider,123,1,"","",DateTimeOffset.MaxValue,"Synthetic",
            AuthorizationHealth.Linked,RobloxSessionIdentity.Issuer,"123","synthetic-current-session");
        await vault.SaveAsync(record,default);var pending=await vault.ReadWriteSnapshotAsync(profile.Id,default);
        Assert.Equal(AuthorizationMutationStatus.Applied,(await vault.TryCommitSnapshotAsync(pending,record with { Generation=2 },context.OperationId,default)).Status);
        var grant=await vault.ReadWriteSnapshotAsync(profile.Id,default);
        var intent=new LaunchIntent(context.LaunchOperationId,profile.Id,profile.Revision,123,1818,0,DateTimeOffset.UtcNow,2,null,RobloxServerKind.Unknown,true);
        using var candidate=SyntheticHandoff.Capture(context,new HandoffClock());
        var start=new StalledStart();using var deadline=new CancellationTokenSource();
        Task<NativeHandoffAttempt>? attempt=null;
        var cases=new List<DistributionSelfCheck.Case>();const string stage="Synthetic final handoff and current-grant boundary";
        Exception? failure=null;
        try
        {
            // The stage as RunAsync runs it: the owner is shut down through the bounded helper, the stage's failure is
            // recorded, and only then are the stores the stage shared released.
            try
            {
                await SelfCheckOwners.UseAsync(new NativeHandoffCoordinator(vault),async coordinator=>
                {
                    attempt=coordinator.DispatchAsync(candidate,grant,intent,start,new LaunchAuthorizationValidator(repository,vault).ValidateAsync,
                        ()=>true,()=>new(true,"Synthetic fixture reservation only"),deadline.Token);
                    await start.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    deadline.Cancel(); // The stage deadline passes while the token-less start is still inside the vault's dispatch lease.
                    await attempt.WaitAsync(deadline.Token);
                },TimeSpan.FromMilliseconds(200));
            }
            catch(Exception ex) { failure=ex;cases.Add(DistributionSelfCheck.FailedCase(stage,ex,null)); }
            finally { DistributionSelfCheck.ReleaseShared(cases,stage,null,failure,vault,repository); }
        }
        finally { start.Release(); } // Let the abandoned worker leave the vault whatever the stage reported.
        var failed=Assert.Single(cases);
        Assert.Equal(nameof(TimeoutException),failed.ErrorType);
        Assert.Equal(typeof(NativeHandoffCoordinator).FullName+".DisposeAsync",failed.FailingMethod);
        Assert.Equal(nameof(TaskCanceledException),failed.CauseType);
        Assert.True(failed.SharedResourcesRetained);
        Assert.Null(failed.ReleaseErrorType);
        Assert.DoesNotContain(SyntheticHandoff.Ticket,JsonSerializer.Serialize(cases));
        Assert.NotNull(attempt);await attempt.WaitAsync(TimeSpan.FromSeconds(10));
        vault.Dispose();repository.Dispose(); // Once the worker has left, the stores close as they would at the end of a passing run.
    }

    [Fact]
    public void AStoreThatRefusesReleaseAfterARecordedFailureIsNotedOnThatCaseAndTheOtherStoresAreStillReleased()
    {
        var timeout=new TimeoutException("Synthetic stalled owner.",new OperationCanceledException());
        var cases=new List<DistributionSelfCheck.Case>{new("Earlier stage",true),DistributionSelfCheck.FailedCase("Stage",timeout,"Step")};
        var recorded=cases[1];var released=new CountingStore();
        DistributionSelfCheck.ReleaseShared(cases,"Stage","Step",timeout,new RefusingStore(),null,released);
        Assert.Equal(2,cases.Count);
        Assert.Equal(recorded with { ReleaseErrorType=nameof(InvalidOperationException) },cases[1]);
        Assert.Equal(1,released.Disposed);
    }

    [Fact]
    public void AStoreThatRefusesReleaseAfterEveryStagePassedIsTheRunsFailure()
    {
        var cases=new List<DistributionSelfCheck.Case>{new("Last stage",true)};var released=new CountingStore();
        DistributionSelfCheck.ReleaseShared(cases,"Last stage",null,null,new RefusingStore(),released);
        Assert.Equal(2,cases.Count);Assert.True(cases[0].Passed);
        var failed=cases[1];
        Assert.False(failed.Passed);Assert.Equal("Last stage",failed.Name);Assert.Equal(nameof(InvalidOperationException),failed.ErrorType);
        Assert.Equal(typeof(RefusingStore).FullName+"."+nameof(IDisposable.Dispose),failed.FailingMethod);
        Assert.Null(failed.ReleaseErrorType);Assert.Equal(1,released.Disposed);
    }

    [Fact]
    public async Task AStalledOwnerKeepsEverySharedStoreAliveUntilProcessExit()
    {
        var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure=await Assert.ThrowsAsync<TimeoutException>(()=>SelfCheckOwners.UseAsync(new PendingOwner(stopped.Task),
            _=>Task.CompletedTask,TimeSpan.Zero));
        var store=new CountingStore();var cases=new List<DistributionSelfCheck.Case>{DistributionSelfCheck.FailedCase("Stage",failure,null)};
        DistributionSelfCheck.ReleaseShared(cases,"Stage",null,failure,store,new RefusingStore());
        Assert.True(Assert.Single(cases).SharedResourcesRetained);Assert.Equal(0,store.Disposed);
        stopped.SetResult();
    }

    [Fact]
    public void TheSharedStoresAreReleasedAfterTheFailureIsRecordedNotByUsingInsideTheTryBlock()
    {
        string source=Resource("DistributionSelfCheck.cs.txt");
        var usingShared=source.Split('\n').Select((text,index)=>(Line:index+1,Text:text))
            .Where(line=>Regex.IsMatch(line.Text,@"\busing\s*(var\s+|\(\s*var\s+)(repository|vault|cleanup|settings|setupProjection)\s*="))
            .Select(line=>line.Line+": "+line.Text.Trim()).ToArray();
        Assert.Empty(usingShared);
        Assert.Matches(@"finally\s*\{\s*ReleaseShared\(cases,\s*stage,\s*step,",source);
    }

    private static string Resource(string name)
    {
        using var stream=typeof(DistributionSelfCheckSharedStoresTests).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader=new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // A token-less start that does not return until released, like a native call that ignores cancellation.
    private sealed class StalledStart : IPreparedNativeHandoffStart
    {
        private readonly ManualResetEventSlim _gate=new();
        public TaskCompletionSource Started { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NativeHandoffDispatchResult Start(string officialHandoff)
        {
            Started.TrySetResult();
            if(!_gate.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The synthetic stalled start was never released.");
            return new(NativeHandoffDispatchOutcome.StartedUnverified,new(700004,DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks,DateTimeOffset.UtcNow));
        }
        public void Release()=>_gate.Set();
        public void Dispose() { }
    }
    // A store still in use by an abandoned worker, as ProtectedCredentialVault and AccountIdentityProjection refuse disposal.
    private sealed class RefusingStore : IDisposable
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose()=>throw new InvalidOperationException("Cancel and await pending operations before disposing.");
    }
    private sealed class CountingStore : IDisposable { public int Disposed;public void Dispose()=>Disposed++; }
    private sealed class PendingOwner(Task stopped) : IAsyncDisposable { public ValueTask DisposeAsync()=>new(stopped); }

    public void Dispose()
    {
        try { _vault?.Dispose(); } catch(InvalidOperationException) { } // Only after a failed assertion left the worker inside.
        _repository?.Dispose();
        string root=Path.GetFullPath(_root);
        if(!string.Equals(Path.GetDirectoryName(root),Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("zenith-selfcheck-shared-stores-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected synthetic fixture cleanup path.");
        if(Directory.Exists(root)) Directory.Delete(root,true);
    }
}
