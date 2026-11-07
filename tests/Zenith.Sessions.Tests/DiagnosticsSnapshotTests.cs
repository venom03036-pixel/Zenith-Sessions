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
            Assert.InRange(entry.Summary.Length,1,100);
            Assert.Equal(entry.Summary,(entry with {ErrorType="synthetic-do-not-display",Count=long.MaxValue}).Summary);
            Assert.Equal(entry.Kind==ManagerEventKind.OperationFailed?DiagnosticLevel.Error:
                entry.Kind is ManagerEventKind.Panic or ManagerEventKind.DroppedEvents?DiagnosticLevel.Warning:DiagnosticLevel.Info,entry.Level);
        });
        Assert.Throws<ArgumentOutOfRangeException>(()=>diagnostics.Record((ManagerEventKind)int.MaxValue));
        Assert.Equal(snapshot.TotalEvents,diagnostics.GetSnapshot().TotalEvents);
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
    public void Dispose()
    {
        string full=Path.GetFullPath(Root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full)!=_name)
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,recursive:true);
    }
}
