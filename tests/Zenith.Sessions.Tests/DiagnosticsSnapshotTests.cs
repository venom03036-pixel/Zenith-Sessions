using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class DiagnosticsSnapshotTests : IDisposable
{
    private readonly string _name="zenith-diagnostic-snapshot-tests-"+Guid.NewGuid().ToString("N");
    private string Root=>Path.Combine(Path.GetTempPath(),_name);

    [Fact]
    public async Task FiftyThousandLiveObservationsRetainNewest512EvenWhenDiskQueueIsFull()
    {
        var entered=Signal();var release=Signal();
        await using var diagnostics=new ManagerDiagnostics(Root,1,512,async ct=>
        {entered.TrySetResult(true);await release.Task.WaitAsync(ct);});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for(int index=0;index<50000;index++)
                diagnostics.Record((index%3) switch {0=>ManagerEventKind.ProfileSaved,1=>ManagerEventKind.Panic,_=>ManagerEventKind.OperationFailed});
            var snapshot=diagnostics.GetSnapshot();
            Assert.Equal(50000,snapshot.LatestSequence);Assert.Equal(50000,snapshot.TotalEvents);
            Assert.Equal(16667,snapshot.InfoCount);Assert.Equal(16667,snapshot.WarningCount);Assert.Equal(16666,snapshot.ErrorCount);
            Assert.Equal(49999,snapshot.DroppedEvents);Assert.Null(snapshot.Failure);
            Assert.Equal(ManagerDiagnostics.JournalCapacity,snapshot.Events.Count);
            Assert.Equal(49489,snapshot.Events[0].Sequence);Assert.Equal(50000,snapshot.Events[^1].Sequence);
            AssertCoherent(snapshot);
        }
        finally {release.TrySetResult(true);}
        await diagnostics.DisposeAsync();
        Assert.Null(diagnostics.GetSnapshot().Failure);
    }

    [Fact]
    public async Task ConcurrentRecordsAndSnapshotsHaveOneCoherentSequenceAndCounts()
    {
        var entered=Signal();var release=Signal();
        await using var diagnostics=new ManagerDiagnostics(Root,1,512,async ct=>
        {entered.TrySetResult(true);await release.Task.WaitAsync(ct);});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Task[] workers=Enumerable.Range(0,8).Select(_=>Task.Run(()=>
            {
                for(int index=0;index<6250;index++)
                {
                    if((index&127)==0) stop.Token.ThrowIfCancellationRequested();
                    diagnostics.Record(ManagerEventKind.OperationFailed,typeof(IOException));
                    if((index&127)==0) AssertCoherent(diagnostics.GetSnapshot());
                }
            },stop.Token)).ToArray();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
            var snapshot=diagnostics.GetSnapshot();AssertCoherent(snapshot);
            Assert.Equal(50000,snapshot.TotalEvents);Assert.Equal(50000,snapshot.ErrorCount);
            Assert.Equal(0,snapshot.InfoCount);Assert.Equal(0,snapshot.WarningCount);
            Assert.Equal(49999,snapshot.DroppedEvents);Assert.Equal(512,snapshot.Events.Count);
            Assert.Equal(512,snapshot.Events.Select(entry=>entry.Sequence).Distinct().Count());
        }
        finally {release.TrySetResult(true);}
    }

    [Fact]
    public async Task PreviouslyReturnedSnapshotsAndTheirReadOnlyCollectionsRemainStable()
    {
        await using var diagnostics=new ManagerDiagnostics(Root);
        diagnostics.Record(ManagerEventKind.Started);
        diagnostics.Record(ManagerEventKind.Panic);
        var before=diagnostics.GetSnapshot();var original=before.Events.ToArray();
        var collection=Assert.IsAssignableFrom<IList<ManagerDiagnosticEvent>>(before.Events);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(()=>collection[0]=new(999,DateTimeOffset.UtcNow,ManagerEventKind.Shutdown,null));
        for(int index=0;index<1000;index++) diagnostics.Record(ManagerEventKind.SettingsSaved);
        Assert.Equal(2,before.TotalEvents);Assert.Equal(2,before.LatestSequence);
        Assert.Equal(original,before.Events);
        var after=diagnostics.GetSnapshot();Assert.Equal(1002,after.TotalEvents);Assert.Equal(512,after.Events.Count);
        Assert.NotSame(before.Events,after.Events);
    }

    [Fact]
    public async Task EveryKindHasFixedSummaryAndLevelAndInvalidKindsCannotGrowTheJournal()
    {
        await using var diagnostics=new ManagerDiagnostics(Root);
        foreach(var kind in Enum.GetValues<ManagerEventKind>()) diagnostics.Record(kind);
        var snapshot=diagnostics.GetSnapshot();
        Assert.Equal(Enum.GetValues<ManagerEventKind>().Length,snapshot.TotalEvents);
        Assert.Equal(1,snapshot.ErrorCount);Assert.Equal(2,snapshot.WarningCount);
        Assert.All(snapshot.Events,entry=>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Summary));
            Assert.NotEqual("Unrecognized diagnostic event.",entry.Summary);
            Assert.InRange(entry.Summary.Length,1,100);
            Assert.Equal(entry.Summary,(entry with {ErrorType="synthetic-do-not-display",Count=long.MaxValue}).Summary);
            Assert.Equal(entry.Kind==ManagerEventKind.OperationFailed?DiagnosticLevel.Error:
                entry.Kind is ManagerEventKind.Panic or ManagerEventKind.DroppedEvents?DiagnosticLevel.Warning:DiagnosticLevel.Info,entry.Level);
        });
        Assert.Throws<ArgumentOutOfRangeException>(()=>diagnostics.Record((ManagerEventKind)int.MaxValue));
        Assert.Equal(snapshot.TotalEvents,diagnostics.GetSnapshot().TotalEvents);
    }

    [Fact]
    public async Task HasChangedSinceSeesNewEventsAndWriterOnlyChangesToFailureOrSkips()
    {
        // A file where the diagnostics folder should be makes the writer fail; the gate holds it until the snapshot is taken.
        Directory.CreateDirectory(Root);await File.WriteAllTextAsync(Path.Combine(Root,"diagnostics"),"not a folder");
        var release=Signal();
        await using var diagnostics=new ManagerDiagnostics(Root,8,1024*1024,async ct=>await release.Task.WaitAsync(ct));
        try
        {
            Assert.True(diagnostics.HasChangedSince(null));
            var empty=diagnostics.GetSnapshot();Assert.False(diagnostics.HasChangedSince(empty));
            diagnostics.Record(ManagerEventKind.Started);Assert.True(diagnostics.HasChangedSince(empty));
            var recorded=diagnostics.GetSnapshot();Assert.False(diagnostics.HasChangedSince(recorded));
            Assert.True(diagnostics.HasChangedSince(recorded with {DroppedEvents=recorded.DroppedEvents+1}));
            Assert.True(diagnostics.HasChangedSince(recorded with {Failure="An earlier failure text."}));
            release.TrySetResult(true);
            await UntilAsync(()=>diagnostics.Failure is not null);
            // The failed write changes Failure and the skip count but not the sequence, so a sequence-only check would miss it.
            var failed=diagnostics.GetSnapshot();
            Assert.Equal(recorded.LatestSequence,failed.LatestSequence);Assert.Equal(1,failed.DroppedEvents);
            Assert.True(diagnostics.HasChangedSince(recorded));Assert.False(diagnostics.HasChangedSince(failed));
        }
        finally {release.TrySetResult(true);}
    }

    [Fact]
    public async Task DiskFailureRetainsTypedObservationsAndReportsAllUnwrittenEventsWithoutPayloads()
    {
        const string marker="synthetic-secret-must-not-enter-failure";
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root,"diagnostics"),marker);
        await using var diagnostics=new ManagerDiagnostics(Root,1);
        for(int index=0;index<100;index++) diagnostics.Record(ManagerEventKind.OperationFailed,typeof(IOException));
        await diagnostics.DisposeAsync();
        var snapshot=diagnostics.GetSnapshot();AssertCoherent(snapshot);
        Assert.Equal(100,snapshot.TotalEvents);Assert.Equal(100,snapshot.ErrorCount);
        Assert.Equal(100,snapshot.DroppedEvents);Assert.Equal(100,snapshot.Events.Count);
        Assert.NotNull(snapshot.Failure);Assert.DoesNotContain(marker,snapshot.Failure);
        Assert.Equal(marker,await File.ReadAllTextAsync(Path.Combine(Root,"diagnostics")));
    }

    [Fact]
    public async Task WriterRecoversAfterTheDiagnosticsFolderIsDeleted()
    {
        string folder=Path.Combine(Root,"diagnostics"),log=Path.Combine(folder,"manager.jsonl");
        await using var diagnostics=new ManagerDiagnostics(Root);
        diagnostics.Record(ManagerEventKind.Started);
        await UntilAsync(()=>ReadShared(log).Contains("Started"));
        // Same effect as the user deleting the folder after "Open logs"; the writer holds no handle between events.
        await UntilAsync(()=>TryDeleteFolder(folder));
        diagnostics.Record(ManagerEventKind.ProfileSaved);diagnostics.Record(ManagerEventKind.SettingsSaved);
        await diagnostics.DisposeAsync();
        Assert.Null(diagnostics.Failure);Assert.Equal(0,diagnostics.DroppedEvents);
        Assert.True(File.Exists(log));
        string text=await File.ReadAllTextAsync(log);
        Assert.Contains("ProfileSaved",text);Assert.Contains("SettingsSaved",text);
    }

    [Fact]
    public async Task WriterRecoversAfterATransientSharingViolation()
    {
        string log=Path.Combine(Root,"diagnostics","manager.jsonl");
        await using var diagnostics=new ManagerDiagnostics(Root);
        diagnostics.Record(ManagerEventKind.Started);
        await UntilAsync(()=>ReadShared(log).Contains("Started"));
        FileStream? blocker=null;
        // Another program (scanner, indexer, viewer) holds the log and denies writers.
        await UntilAsync(()=>(blocker=TryOpenDenyingWriters(log)) is not null);
        using(blocker)
        {
            diagnostics.Record(ManagerEventKind.ProfileSaved);
            await UntilAsync(()=>diagnostics.Failure is not null);
        }
        diagnostics.Record(ManagerEventKind.SettingsSaved);
        await diagnostics.DisposeAsync();
        Assert.Null(diagnostics.Failure);Assert.Equal(1,diagnostics.DroppedEvents);
        string text=await File.ReadAllTextAsync(log);
        Assert.Contains("SettingsSaved",text);Assert.DoesNotContain("ProfileSaved",text);
        Assert.Contains("\"Kind\":\"DroppedEvents\",\"ErrorType\":null,\"Count\":1",text);
    }

    [Fact]
    public async Task TransientWriteFailureNamesTheBusyLogAndDoesNotStopLaterEventsFromReachingDisk()
    {
        string folder=Path.Combine(Root,"diagnostics"),log=Path.Combine(folder,"manager.jsonl");
        Directory.CreateDirectory(folder);await File.WriteAllBytesAsync(log,[]);
        await using var diagnostics=new ManagerDiagnostics(Root,8);
        string? failure;
        // A log tailer, backup or sync tool that shares only Read denies the writer's append.
        using(new FileStream(log,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            diagnostics.Record(ManagerEventKind.Started);
            await UntilAsync(()=>diagnostics.Failure is not null);
            failure=diagnostics.Failure;
        }
        // The folder permissions are fine, so the status must not send the user to check them.
        Assert.DoesNotContain("permission",failure,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("another program is using the log file",failure);
        Assert.Contains("retries on the next event",failure);
        diagnostics.Record(ManagerEventKind.SettingsSaved);
        await diagnostics.DisposeAsync();
        Assert.Null(diagnostics.Failure);Assert.Equal(1,diagnostics.DroppedEvents);
        string text=await File.ReadAllTextAsync(log);
        Assert.Contains("\"Kind\":\"SettingsSaved\"",text);Assert.DoesNotContain("\"Kind\":\"Started\"",text);
        Assert.Contains("\"Kind\":\"DroppedEvents\",\"ErrorType\":null,\"Count\":1",text);
    }

    [Fact]
    public async Task AccessDeniedStillPointsAtPermissionsAndClearsOnceTheLogIsWritableAgain()
    {
        string folder=Path.Combine(Root,"diagnostics"),log=Path.Combine(folder,"manager.jsonl");
        Directory.CreateDirectory(folder);await File.WriteAllBytesAsync(log,[]);
        await using var diagnostics=new ManagerDiagnostics(Root,8);
        File.SetAttributes(log,FileAttributes.ReadOnly);
        try
        {
            diagnostics.Record(ManagerEventKind.Started);
            await UntilAsync(()=>diagnostics.Failure is not null);
            Assert.Equal("Diagnostics could not be written. Check the data-folder permissions.",diagnostics.Failure);
        }
        finally {File.SetAttributes(log,FileAttributes.Normal);}
        diagnostics.Record(ManagerEventKind.SettingsSaved);
        await diagnostics.DisposeAsync();
        Assert.Null(diagnostics.Failure);Assert.Equal(1,diagnostics.DroppedEvents);
        Assert.Contains("\"Kind\":\"SettingsSaved\"",await File.ReadAllTextAsync(log));
    }

    [Fact]
    public void WriteFailureTextNamesTheCauseWithoutCopyingTheExceptionMessage()
    {
        const string marker="synthetic-path-must-not-enter-failure";
        // HRESULT_FROM_WIN32 of ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION, ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL.
        foreach(int code in new[]{32,33})
            Assert.Contains("another program is using the log file",ManagerDiagnostics.WriteFailureText(new IOException(marker,unchecked((int)0x80070000)|code)));
        foreach(int code in new[]{112,39})
            Assert.Contains("the disk is full",ManagerDiagnostics.WriteFailureText(new IOException(marker,unchecked((int)0x80070000)|code)));
        Assert.Equal("Diagnostics could not be written to the data folder. Zenith retries on the next event.",
            ManagerDiagnostics.WriteFailureText(new DirectoryNotFoundException(marker)));
        Assert.Contains("permissions",ManagerDiagnostics.WriteFailureText(new UnauthorizedAccessException(marker)));
        foreach(Exception error in new Exception[]{new IOException(marker,unchecked((int)0x80070020)),new IOException(marker,unchecked((int)0x80070070)),new IOException(marker),new UnauthorizedAccessException(marker)})
        {
            string text=ManagerDiagnostics.WriteFailureText(error);
            Assert.DoesNotContain(marker,text);
            if(error is not UnauthorizedAccessException) Assert.DoesNotContain("permission",text,StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CanceledWriterShutdownIsSharedIdempotentAndPostDisposalRecordsAreIgnored()
    {
        var entered=Signal();
        await using var diagnostics=new ManagerDiagnostics(Root,1,512,async ct=>
        {entered.TrySetResult(true);await Task.Delay(Timeout.InfiniteTimeSpan,ct);});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        diagnostics.Record(ManagerEventKind.Started);
        Task first=diagnostics.DisposeAsync().AsTask(),second=diagnostics.DisposeAsync().AsTask();
        Assert.Same(first,second);
        diagnostics.Record(ManagerEventKind.ProfileSaved);
        await Task.WhenAll(first,second).WaitAsync(TimeSpan.FromSeconds(6));
        var snapshot=diagnostics.GetSnapshot();
        Assert.Equal(1,snapshot.TotalEvents);Assert.Equal(1,snapshot.LatestSequence);
        Assert.Equal(1,snapshot.InfoCount);Assert.Equal(1,snapshot.DroppedEvents);
        Assert.NotNull(snapshot.Failure);Assert.Contains("timed out",snapshot.Failure);
        for(int index=0;index<100;index++) diagnostics.Record(ManagerEventKind.OperationFailed);
        await diagnostics.DisposeAsync();
        var after=diagnostics.GetSnapshot();
        Assert.Equal(snapshot.LatestSequence,after.LatestSequence);Assert.Equal(snapshot.TotalEvents,after.TotalEvents);
        Assert.Equal(snapshot.DroppedEvents,after.DroppedEvents);Assert.Equal(snapshot.Events,after.Events);
    }

    [Fact]
    public async Task ErrorTypesAreFixedClassLabelsAndDiskKeepsItsOriginalFourPropertySchema()
    {
        const string secret="synthetic_refresh_secret_do_not_log";
        var assembly=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DiagnosticRedactionFixture"),AssemblyBuilderAccess.RunAndCollect);
        Type custom=assembly.DefineDynamicModule("Fixture").DefineType(secret+new string('x',500),TypeAttributes.Public,typeof(Exception)).CreateTypeInfo()!.AsType();
        await using var diagnostics=new ManagerDiagnostics(Root,capacity:256,fileLimit:256);
        diagnostics.Record(ManagerEventKind.OperationFailed,custom);
        diagnostics.Record(ManagerEventKind.OperationFailed,typeof(IOException));
        diagnostics.Record(ManagerEventKind.OperationFailed,typeof(string));
        await diagnostics.DisposeAsync();
        var snapshot=diagnostics.GetSnapshot();
        Assert.Equal(new string?[]{"Exception","IOException","NonExceptionType"},snapshot.Events.Select(entry=>entry.ErrorType).ToArray());
        Assert.DoesNotContain(secret,JsonSerializer.Serialize(snapshot));
        Assert.All(snapshot.Events,entry=>Assert.InRange(entry.ErrorType!.Length,1,32));
        var recordMethod=typeof(ManagerDiagnostics).GetMethod(nameof(ManagerDiagnostics.Record))!;
        Assert.Equal(new[]{typeof(ManagerEventKind),typeof(Type)},recordMethod.GetParameters().Select(parameter=>parameter.ParameterType).ToArray());
        string[] paths=Directory.GetFiles(Path.Combine(Root,"diagnostics"));Assert.InRange(paths.Length,1,3);
        foreach(string path in paths)
        {
            Assert.InRange(new FileInfo(path).Length,1,256);
            foreach(string line in await File.ReadAllLinesAsync(path))
            {
                using var json=JsonDocument.Parse(line);
                Assert.Equal(new[]{"Utc","Kind","ErrorType","Count"},json.RootElement.EnumerateObject().Select(property=>property.Name).ToArray());
                Assert.DoesNotContain(secret,line);Assert.DoesNotContain("Message",line);Assert.DoesNotContain("Sequence",line);
            }
        }
    }

    [Fact]
    public async Task NewInstanceNeverReplaysOldDiskHistoryIntoLiveCounters()
    {
        await using(var first=new ManagerDiagnostics(Root))
        {first.Record(ManagerEventKind.Started);first.Record(ManagerEventKind.SettingsSaved);}
        await using var next=new ManagerDiagnostics(Root);
        var snapshot=next.GetSnapshot();
        Assert.Equal(0,snapshot.TotalEvents);Assert.Equal(0,snapshot.LatestSequence);Assert.Empty(snapshot.Events);
        Assert.Equal(0,snapshot.InfoCount);Assert.Equal(0,snapshot.WarningCount);Assert.Equal(0,snapshot.ErrorCount);
    }

    private static void AssertCoherent(ManagerDiagnosticSnapshot snapshot)
    {
        Assert.Equal(snapshot.TotalEvents,snapshot.InfoCount+snapshot.WarningCount+snapshot.ErrorCount);
        Assert.InRange(snapshot.Events.Count,0,ManagerDiagnostics.JournalCapacity);
        Assert.InRange(snapshot.DroppedEvents,0,snapshot.TotalEvents);
        if(snapshot.Events.Count==0) {Assert.Equal(0,snapshot.LatestSequence);return;}
        Assert.Equal(snapshot.LatestSequence,snapshot.Events[^1].Sequence);
        for(int index=0;index<snapshot.Events.Count;index++)
        {
            Assert.Equal(snapshot.LatestSequence-snapshot.Events.Count+index+1,snapshot.Events[index].Sequence);
            Assert.Equal(TimeSpan.Zero,snapshot.Events[index].Utc.Offset);
        }
    }
    private static TaskCompletionSource<bool> Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task UntilAsync(Func<bool> condition)
    {
        // Bounded wait for the background writer; the disk has no completion event to observe.
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(!condition())
        {
            if(DateTime.UtcNow>deadline) throw new TimeoutException("The diagnostics writer did not reach the expected state.");
            await Task.Delay(10);
        }
    }
    private static string ReadShared(string path)
    {
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var reader=new StreamReader(stream);return reader.ReadToEnd();
        }
        catch(IOException) {return "";}
    }
    private static bool TryDeleteFolder(string folder)
    {
        try {Directory.Delete(folder,recursive:true);return true;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {return false;}
    }
    private static FileStream? TryOpenDenyingWriters(string path)
    {
        try {return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);}
        catch(IOException) {return null;}
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(Root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full)!=_name)
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,recursive:true);
    }
}
