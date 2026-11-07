using System.Globalization;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.VaultTestWorker;

/// <summary>
/// Test-only child. It never accepts credentials, profile IDs or arbitrary vault paths. Every
/// operation is confined to an explicitly marked synthetic fixture directly below Windows Temp.
/// No account endpoint, browser, authentication flow, native client or child process is invoked.
/// </summary>
internal static class Program
{
    private const string Prefix="zenith-vault-cas-",Marker=".zenith-vault-cas-owner";
    private const string Owner="Zenith Sessions synthetic vault CAS fixture v1";
    private static readonly Guid ProfileId=Guid.Parse("d87a0474-b2e3-4bc1-8b70-0cde43000001");
    private const long UserId=900000123;
    private const string Initial="synthetic-vault-cas-initial",CandidateA="synthetic-vault-cas-a",CandidateB="synthetic-vault-cas-b",Foreign="synthetic-vault-cas-foreign";
    private sealed record WorkerResult(int Version,string Scenario,string Status,long? CommittedGeneration=null,bool? Fenced=null);

    private static async Task<int> Main(string[] args)
    {
        if(args.Length!=4 || args[0] is not ("hold" or "replace-a" or "replace-b" or "snapshot-a" or "snapshot-b" or "foreign-a" or "foreign-b" or "fence")
            || !Guid.TryParseExact(args[2],"N",out Guid control) || control==Guid.Empty
            || !Guid.TryParseExact(args[3],"N",out Guid mutation) || mutation==Guid.Empty) return 64;
        string root;
        try{root=ValidateRoot(args[1],inspectTree:true);}
        catch(Exception){return 65;}
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            if(args[0]=="hold")
            {
                // This is Zenith's own newly created synthetic vault lease, never a Roblox file.
                string leasePath=Path.Combine(root,"accounts.vault",ProtectedCredentialVault.WriterLockFileName);AssertAncestors(leasePath);
                if(new FileInfo(leasePath).Length!=0)throw new IOException("Fixture lease file must be empty.");
                using var lease=new FileStream(leasePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None,1,FileOptions.None);
                WriteSignal(root,control,"ready","ready-v1");
                await WaitForSignalAsync(root,control,"release","release-v1",deadline.Token).ConfigureAwait(false);
                WriteResult(root,control,new(1,args[0],"Released"));return 0;
            }
            if(args[0] is "snapshot-a" or "snapshot-b")
            {
                using var snapshotVault=new ProtectedCredentialVault(root);
                ISnapshotAuthorizationVault snapshots=snapshotVault;
                // Read before ready: the parent releases both contenders only after each has
                // pinned this exact synthetic G1 record and its authoritative write snapshot.
                var snapshot=await snapshots.ReadWriteSnapshotAsync(ProfileId,deadline.Token).ConfigureAwait(false);
                if(snapshot.Record is not { } selectedSnapshotRecord || selectedSnapshotRecord!=Record(1,Initial)
                    || snapshot.HighestAllocated!=1 || snapshot.LastCommitted!=1 || snapshot.NextGeneration!=2)
                    throw new InvalidOperationException("The synthetic snapshot baseline changed before readiness.");
                WriteSignal(root,control,"ready","ready-v1");
                await WaitForSignalAsync(root,control,"go","continue-v1",deadline.Token).ConfigureAwait(false);
                var snapshotReplacement=selectedSnapshotRecord with {Generation=snapshot.NextGeneration,SessionCookie=args[0]=="snapshot-a"?CandidateA:CandidateB};
                var committedSnapshot=await snapshots.TryCommitSnapshotAsync(snapshot,snapshotReplacement,mutation,deadline.Token).ConfigureAwait(false);
                string snapshotStatus=committedSnapshot.Status switch{AuthorizationMutationStatus.Applied=>"Applied",AuthorizationMutationStatus.Conflict=>"Conflict",_=>throw new InvalidOperationException("Unknown fixture mutation status.")};
                WriteResult(root,control,new(1,args[0],snapshotStatus,committedSnapshot.CommittedGeneration));return 0;
            }
            WriteSignal(root,control,"ready","ready-v1");
            await WaitForSignalAsync(root,control,"go","continue-v1",deadline.Token).ConfigureAwait(false);
            using var vault=new ProtectedCredentialVault(root);
            IConditionalAuthorizationVault conditional=vault;
            if(args[0]=="fence")
            {
                bool fenced=await conditional.TryFenceOwnedMutationAsync(ProfileId,mutation,deadline.Token).ConfigureAwait(false);
                WriteResult(root,control,new(1,args[0],"FenceChecked",Fenced:fenced));return 0;
            }
            bool foreign=args[0].StartsWith("foreign-",StringComparison.Ordinal);
            string previous=foreign?(args[0]=="foreign-a"?CandidateA:CandidateB):Initial;
            string next=foreign?Foreign:args[0]=="replace-a"?CandidateA:CandidateB;
            var expected=Record(foreign?2:1,previous);
            var replacement=expected with {SessionCookie=next}; // Store atomically chooses the successor generation.
            var result=await conditional.TryReplaceNextAsync(expected,replacement,mutation,deadline.Token).ConfigureAwait(false);
            string status=result.Status switch{AuthorizationMutationStatus.Applied=>"Applied",AuthorizationMutationStatus.Conflict=>"Conflict",_=>throw new InvalidOperationException("Unknown fixture mutation status.")};
            WriteResult(root,control,new(1,args[0],status,result.CommittedGeneration));
            return 0;
        }
        catch(OperationCanceledException)
        {TryWriteResult(root,control,new(1,args[0],"Cancelled"));return 2;}
        catch(Exception)
        {TryWriteResult(root,control,new(1,args[0],"Failed"));return 3;}
    }

    private static AuthorizationRecord Record(long generation,string value)=>new AuthorizationRecord(ProfileId,RobloxSessionIdentity.Provider,UserId,generation,
        "","",DateTimeOffset.MaxValue,"fixture_user",AuthorizationHealth.Linked,RobloxSessionIdentity.Issuer,
        UserId.ToString(CultureInfo.InvariantCulture),SessionCookie:value).Validated();

    private static string ValidateRoot(string value,bool inspectTree=false)
    {
        string full=Path.GetFullPath(value),temp=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if(!Path.IsPathFullyQualified(value) || !full.Equals(value,StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(full),temp,StringComparison.OrdinalIgnoreCase)) throw new IOException("Not an owned fixture.");
        string leaf=Path.GetFileName(full);
        if(!leaf.StartsWith(Prefix,StringComparison.Ordinal) || !Guid.TryParseExact(leaf[Prefix.Length..],"N",out Guid id) || id==Guid.Empty) throw new IOException("Not an owned fixture.");
        AssertAncestors(full);if(inspectTree)AssertFixtureTree(full);
        string marker=Path.Combine(full,Marker);var info=new FileInfo(marker);
        if(!info.Exists || info.Length!=Encoding.UTF8.GetByteCount(Owner) || (info.Attributes&FileAttributes.ReparsePoint)!=0
            || File.ReadAllText(marker)!=Owner) throw new IOException("Not an owned fixture.");
        return full;
    }
    private static void AssertAncestors(string root)
    {
        for(string? path=root;path is not null;path=Path.GetDirectoryName(path))
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new IOException("Redirected fixture preserved.");
    }
    private static void AssertFixtureTree(string root)
    {
        var pending=new Stack<string>();pending.Push(root);int visited=0;
        while(pending.TryPop(out var directory))
            foreach(string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if(++visited>256) throw new IOException("Fixture inventory exceeds its bound.");
                FileAttributes attributes;
                try{attributes=File.GetAttributes(entry);}
                catch(FileNotFoundException){continue;} // Another owned helper atomically published its temporary signal.
                catch(DirectoryNotFoundException){continue;}
                if((attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Redirected fixture preserved.");
                if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);
            }
    }
    private static string SignalPath(string root,Guid control,string kind)=>Path.Combine(root,"worker-"+control.ToString("N")+"."+kind);
    private static void WriteSignal(string root,Guid control,string kind,string content)
    {Publish(root,SignalPath(root,control,kind),Encoding.ASCII.GetBytes(content));}
    private static async Task WaitForSignalAsync(string root,Guid control,string kind,string expected,CancellationToken ct)
    {
        string path=SignalPath(root,control,kind);
        while(true)
        {
            ct.ThrowIfCancellationRequested();ValidateRoot(root);
            var info=new FileInfo(path);
            if(info.Exists)
            {
                try
                {
                    if(info.Length>32 || (info.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Fixture signal is invalid.");
                    using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                    if(file.Length>32)throw new IOException("Fixture signal is invalid.");
                    byte[] bytes=new byte[(int)file.Length];file.ReadExactly(bytes);
                    if(Encoding.ASCII.GetString(bytes)!=expected)throw new IOException("Fixture signal is invalid.");
                    return;
                }
                catch(FileNotFoundException){/* The existing bounded wait observes the next publication attempt. */}
                catch(IOException error) when((error.HResult&0xffff) is 32 or 33){/* Transient sharing during native publication; no signal is accepted. */}
            }
            await Task.Delay(25,ct).ConfigureAwait(false);
        }
    }
    private static void WriteResult(string root,Guid control,WorkerResult result)
    {
        ValidateRoot(root);byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(result);
        if(bytes.Length>1024) throw new IOException("Fixture result exceeds its bound.");
        Publish(root,SignalPath(root,control,"result.json"),bytes);
    }
    private static void Publish(string root,string path,byte[] bytes)
    {
        ValidateRoot(root);string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {file.Write(bytes);file.Flush(true);}
            // The writer is closed before the final path appears. Readers also share deletion
            // so the native rename's remaining handle does not become a readiness failure.
            File.Move(temporary,path);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static void TryWriteResult(string root,Guid control,WorkerResult result)
    {try{WriteResult(root,control,result);}catch(Exception){/* No unsafe root retry, raw exception output or external recovery. */}}
}
