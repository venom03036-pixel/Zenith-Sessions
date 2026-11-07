using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Native interprocess contention using this repository's test-only helper. All vaults are newly
/// owned synthetic Temp fixtures; no installed application, browser, account or network is used.
/// </summary>
public sealed class VaultCrossProcessTests
{
    private static readonly Guid ProfileId=Guid.Parse("d87a0474-b2e3-4bc1-8b70-0cde43000001");
    private const long UserId=900000123;
    private const string Initial="synthetic-vault-cas-initial",CandidateA="synthetic-vault-cas-a",CandidateB="synthetic-vault-cas-b",Foreign="synthetic-vault-cas-foreign";
    private static AuthorizationRecord Record(long generation=1,string value=Initial)=>new AuthorizationRecord(ProfileId,RobloxSessionIdentity.Provider,UserId,generation,
        "","",DateTimeOffset.MaxValue,"fixture_user",AuthorizationHealth.Linked,RobloxSessionIdentity.Issuer,
        UserId.ToString(CultureInfo.InvariantCulture),SessionCookie:value).Validated();

    [Fact]
    public async Task ActualOtherProcessLeaseExcludesReadsAndMutationsAndHonorsCancellation()
    {
        await using var fixture=await Fixture.CreateAsync();
        var holder=fixture.Start("hold",Guid.NewGuid());await holder.ReadyAsync();
        using var vault=new ProtectedCredentialVault(fixture.Root);IConditionalAuthorizationVault conditional=vault;
        using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await vault.ReadAsync(ProfileId,cancel.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await conditional.TryReplaceNextAsync(Record(),Record(value:CandidateA),Guid.NewGuid(),cancel.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<AuthorizationVaultBusyException>(async()=>await vault.GetSummaryAsync(ProfileId,default).WaitAsync(TimeSpan.FromSeconds(5)));
        holder.Signal("release");var released=await holder.CompleteAsync();Assert.Equal("Released",released.Status);
        var current=Assert.IsType<AuthorizationRecord>(await vault.ReadAsync(ProfileId,default));
        Assert.Equal(Record(),current);Assert.Equal(0L,new FileInfo(Path.Combine(fixture.Root,"accounts.vault",ProtectedCredentialVault.WriterLockFileName)).Length);
    }

    [Fact]
    public async Task CompetingProcessesHaveOneAtomicWinnerAndOneUnchangedConflict()
    {
        await using var fixture=await Fixture.CreateAsync();
        var a=fixture.Start("replace-a",Guid.NewGuid());var b=fixture.Start("replace-b",Guid.NewGuid());
        await Task.WhenAll(a.ReadyAsync(),b.ReadyAsync());a.Signal("go");b.Signal("go");
        var results=await Task.WhenAll(a.CompleteAsync(),b.CompleteAsync());
        var winner=Assert.Single(results.Where(r=>r.Status=="Applied"));var loser=Assert.Single(results.Where(r=>r.Status=="Conflict"));
        Assert.Equal(2L,winner.CommittedGeneration);Assert.Null(loser.CommittedGeneration);
        using var reopened=new ProtectedCredentialVault(fixture.Root);
        var current=Assert.IsType<AuthorizationRecord>(await reopened.ReadAsync(ProfileId,default));
        Assert.Equal(Record(2,winner.Scenario=="replace-a"?CandidateA:CandidateB),current);
        var summary=await reopened.GetSummaryAsync(ProfileId,default);Assert.NotNull(summary);Assert.Equal(2L,summary.Generation);
        Assert.Equal(AuthorizationHealth.Linked,summary.Health);Assert.True(a.Exited);Assert.True(b.Exited);
    }

    [Fact]
    public async Task ProcessesWithSnapshotsPinnedBeforeReadinessHaveOneGenerationTwoWinner()
    {
        await using var fixture=await Fixture.CreateAsync();
        var a=fixture.Start("snapshot-a",Guid.NewGuid());var b=fixture.Start("snapshot-b",Guid.NewGuid());
        // Neither go signal exists until both actual workers read and validated the G1 snapshot.
        await Task.WhenAll(a.ReadyAsync(),b.ReadyAsync());a.Signal("go");b.Signal("go");
        var results=await Task.WhenAll(a.CompleteAsync(),b.CompleteAsync());
        var winner=Assert.Single(results.Where(result=>result.Status=="Applied"));
        var loser=Assert.Single(results.Where(result=>result.Status=="Conflict"));
        Assert.Equal(2L,winner.CommittedGeneration);Assert.Null(loser.CommittedGeneration);
        Assert.True(a.Exited);Assert.True(b.Exited);
        using var reopened=new ProtectedCredentialVault(fixture.Root);
        var current=Assert.IsType<AuthorizationRecord>(await reopened.ReadAsync(ProfileId,default));
        Assert.Equal(Record(2,winner.Scenario=="snapshot-a"?CandidateA:CandidateB),current);
        Assert.Equal(AuthorizationHealth.Linked,current.Health);
        var summary=await reopened.GetSummaryAsync(ProfileId,default);Assert.NotNull(summary);
        Assert.Equal(2L,summary.Generation);Assert.Equal(AuthorizationHealth.Linked,summary.Health);
        var committed=await reopened.ReadWriteSnapshotAsync(ProfileId,default);
        Assert.Equal(2L,committed.HighestAllocated);Assert.Equal(2L,committed.LastCommitted);
        Assert.Equal(current,committed.Record);
    }

    [Fact]
    public async Task StaleOwnedFenceCannotInvalidateAForeignProcessesLaterGrant()
    {
        await using var fixture=await Fixture.CreateAsync();Guid olderMutation=Guid.NewGuid();
        var first=fixture.Start("replace-a",olderMutation);await first.ReadyAsync();first.Signal("go");
        Assert.Equal(2L,(await first.CompleteAsync()).CommittedGeneration);
        var foreign=fixture.Start("foreign-a",Guid.NewGuid());await foreign.ReadyAsync();foreign.Signal("go");
        Assert.Equal(3L,(await foreign.CompleteAsync()).CommittedGeneration);
        string path=Path.Combine(fixture.Root,"accounts.vault",ProfileId.ToString("N")+".dpapi");
        byte[] before=SHA256.HashData(await File.ReadAllBytesAsync(path));
        var stale=fixture.Start("fence",olderMutation);await stale.ReadyAsync();stale.Signal("go");
        var result=await stale.CompleteAsync();Assert.Equal("FenceChecked",result.Status);Assert.False(result.Fenced);
        Assert.Equal(before,SHA256.HashData(await File.ReadAllBytesAsync(path)));
        using var reopened=new ProtectedCredentialVault(fixture.Root);
        Assert.Equal(Record(3,Foreign),await reopened.ReadAsync(ProfileId,default));
        Assert.Equal(AuthorizationHealth.Linked,(await reopened.GetSummaryAsync(ProfileId,default))!.Health);
    }

    [Fact]
    public async Task OwningMutationCanFenceItsGrantAcrossProcessesWithoutReplayingProtectedBytes()
    {
        await using var fixture=await Fixture.CreateAsync();Guid owner=Guid.NewGuid();
        var replacement=fixture.Start("replace-a",owner);await replacement.ReadyAsync();replacement.Signal("go");
        Assert.Equal(2L,(await replacement.CompleteAsync()).CommittedGeneration);
        var fence=fixture.Start("fence",owner);await fence.ReadyAsync();fence.Signal("go");Assert.True((await fence.CompleteAsync()).Fenced);
        using var reopened=new ProtectedCredentialVault(fixture.Root);
        var record=Assert.IsType<AuthorizationRecord>(await reopened.ReadAsync(ProfileId,default));
        Assert.Equal(AuthorizationHealth.SignInRequired,record.Health);Assert.Empty(record.SessionCookie);Assert.Empty(record.AccessToken);Assert.Empty(record.RefreshToken);
        IConditionalAuthorizationVault conditional=reopened;
        var stale=await conditional.TryReplaceNextAsync(Record(),Record(value:CandidateB),Guid.NewGuid(),default);
        Assert.Equal(AuthorizationMutationStatus.Conflict,stale.Status);Assert.Null(stale.CommittedGeneration);
        Assert.Equal(4L,await reopened.AllocateNextGenerationAsync(ProfileId,0,default));
    }

    private sealed record WorkerResult(int Version,string Scenario,string Status,long? CommittedGeneration=null,bool? Fenced=null);
    private sealed class Fixture : IAsyncDisposable
    {
        private const string Prefix="zenith-vault-cas-",Marker=".zenith-vault-cas-owner",Owner="Zenith Sessions synthetic vault CAS fixture v1";
        private readonly List<OwnedWorker> _workers=[];
        public string Root {get;}=Path.Combine(Path.GetTempPath(),Prefix+Guid.NewGuid().ToString("N"));
        public static async Task<Fixture> CreateAsync()
        {
            var fixture=new Fixture();Directory.CreateDirectory(fixture.Root);
            await File.WriteAllTextAsync(Path.Combine(fixture.Root,Marker),Owner,new UTF8Encoding(false));
            try
            {
                fixture.Validate();using var vault=new ProtectedCredentialVault(fixture.Root);
                await vault.SaveAsync(Record(),default);return fixture;
            }
            catch{await fixture.DisposeAsync();throw;}
        }
        public OwnedWorker Start(string scenario,Guid mutation)
        {
            Validate();string executable=FindWorker();AssertNotRedirected(executable);
            Guid control=Guid.NewGuid();var start=new ProcessStartInfo(executable)
            {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Root};
            foreach(string argument in new[]{scenario,Root,control.ToString("N"),mutation.ToString("N")})start.ArgumentList.Add(argument);
            var process=Process.Start(start)??throw new InvalidOperationException("The owned vault test worker did not start.");
            var worker=new OwnedWorker(this,process,scenario,control);_workers.Add(worker);
            worker.CaptureIdentity();return worker; // Track before querying identity, including a failed query.
        }
        private static string FindWorker()=>TestWorkerLocator.Find("Zenith.Sessions.VaultTestWorker","net10.0-windows10.0.19041.0");
        public string SignalPath(Guid control,string kind)=>Path.Combine(Root,"worker-"+control.ToString("N")+"."+kind);
        public void Signal(Guid control,string kind)
        {
            Validate();if(kind is not ("go" or "release"))throw new ArgumentException("Unknown owned fixture signal.");
            string path=SignalPath(control,kind);if(File.Exists(path))return;
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            byte[] bytes=Encoding.ASCII.GetBytes(kind=="go"?"continue-v1":"release-v1");
            try
            {
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}
                File.Move(temporary,path); // Only the closed, complete owned signal becomes visible.
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        public void Validate()
        {
            string full=Path.GetFullPath(Root),temp=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),leaf=Path.GetFileName(full);
            if(!string.Equals(Path.GetDirectoryName(full),temp,StringComparison.OrdinalIgnoreCase)||!leaf.StartsWith(Prefix,StringComparison.Ordinal)
                ||!Guid.TryParseExact(leaf[Prefix.Length..],"N",out Guid id)||id==Guid.Empty)throw new InvalidOperationException("Unexpected owned fixture path.");
            AssertNotRedirected(full);string marker=Path.Combine(full,Marker);var info=new FileInfo(marker);
            if(!info.Exists||info.Length!=Encoding.UTF8.GetByteCount(Owner)||(info.Attributes&FileAttributes.ReparsePoint)!=0||File.ReadAllText(marker)!=Owner)
                throw new InvalidOperationException("The synthetic fixture owner marker changed.");
        }
        private static void AssertNotRedirected(string path)
        {
            for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Redirected owned fixture was preserved.");
        }
        public async ValueTask DisposeAsync()
        {
            List<Exception>? failures=null;
            foreach(var worker in _workers)
                try{await worker.DisposeAsync();}catch(Exception error){(failures??=[]).Add(error);}
            if(failures is not null)throw new AggregateException("An owned worker did not join; its synthetic directory was preserved.",failures);
            if(!Directory.Exists(Root))return;Validate();
            var pending=new Stack<string>();pending.Push(Root);int count=0;
            while(pending.TryPop(out var directory))
                foreach(string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if(++count>256)throw new InvalidOperationException("Synthetic cleanup inventory exceeded its bound.");
                    var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Redirected synthetic data was preserved.");
                    if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);
                }
            Directory.Delete(Root,true); // Exact marked Temp child, bounded tree checked above; workers already joined.
        }
    }
    private sealed class OwnedWorker(Fixture fixture,Process process,string scenario,Guid control) : IAsyncDisposable
    {
        private readonly Stopwatch _age=Stopwatch.StartNew();
        private int _pid;private long _start;private bool _disposed;
        private TimeSpan Remaining {get{var remaining=TimeSpan.FromSeconds(20)-_age.Elapsed;return remaining>TimeSpan.Zero?remaining:TimeSpan.Zero;}}
        public bool Exited=>process.HasExited;
        public void CaptureIdentity(){_pid=process.Id;_start=process.StartTime.ToUniversalTime().Ticks;}
        public void Signal(string kind)=>fixture.Signal(control,kind);
        public async Task ReadyAsync()
        {
            using var deadline=new CancellationTokenSource(Remaining<TimeSpan.FromSeconds(8)?Remaining:TimeSpan.FromSeconds(8));
            string path=fixture.SignalPath(control,"ready");
            while(true)
            {
                deadline.Token.ThrowIfCancellationRequested();fixture.Validate();
                if(process.HasExited)throw new InvalidOperationException("The owned fixture worker exited before readiness.");
                var info=new FileInfo(path);
                if(info.Exists)
                {
                    try
                    {
                        if(info.Length>32||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("The owned worker readiness signal is invalid.");
                        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                        if(file.Length>32)throw new InvalidDataException("The owned worker readiness signal is invalid.");
                        byte[] bytes=new byte[(int)file.Length];file.ReadExactly(bytes);
                        if(Encoding.ASCII.GetString(bytes)=="ready-v1")return;
                    }
                    catch(FileNotFoundException){/* Retry only within this worker's existing readiness deadline. */}
                    catch(IOException error) when((error.HResult&0xffff) is 32 or 33){/* A transient native sharing conflict is not readiness. */}
                }
                await Task.Delay(20,deadline.Token);
            }
        }
        public async Task<WorkerResult> CompleteAsync()
        {
            await process.WaitForExitAsync().WaitAsync(Remaining);Assert.Equal(0,process.ExitCode);fixture.Validate();
            string path=fixture.SignalPath(control,"result.json");var info=new FileInfo(path);
            if(!info.Exists||info.Length>1024||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("The owned worker result is invalid.");
            var result=JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllBytesAsync(path),new JsonSerializerOptions{MaxDepth=4,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow});
            if(result is not {Version:1}||result.Scenario!=scenario||result.Status is not ("Released" or "Applied" or "Conflict" or "FenceChecked"))
                throw new InvalidDataException("The owned worker result did not match its fixed operation.");
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            if(_disposed)return;
            bool signalFailed=false;
            if(!process.HasExited&&scenario=="hold")
                try{Signal("release");}catch(Exception){signalFailed=true;}
            TimeSpan wait=_start==0||Remaining<TimeSpan.FromSeconds(2)?Remaining:TimeSpan.FromSeconds(2);
            try{await process.WaitForExitAsync().WaitAsync(wait);}
            catch(TimeoutException)
            {
                // Kill only this Process.Start-owned handle after exact captured identity checks.
                // No PID enumeration, name match, process tree or unrelated process is targeted.
                if(_pid<=0||_start<=0||process.Id!=_pid||process.StartTime.ToUniversalTime().Ticks!=_start)
                    throw new InvalidOperationException("The owned fixture worker identity could not be revalidated; its fixture was preserved.");
                if(!process.HasExited)process.Kill(entireProcessTree:false);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            if(!process.HasExited)throw new InvalidOperationException("The owned fixture worker did not stop.");
            _disposed=true;process.Dispose();
            if(signalFailed)throw new InvalidOperationException("The owned release signal failed; the helper was joined and its synthetic fixture was preserved.");
        }
    }
}
