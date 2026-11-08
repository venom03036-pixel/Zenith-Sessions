using System.IO;
using System.Text;
using Xunit;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ExplicitReportFileTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-report-file-tests-"+Guid.NewGuid().ToString("N"));
    private string Data=>Path.Combine(_root,"data");
    private string App=>Path.Combine(_root,"app");
    private string Share=>Path.Combine(_root,"share");
    private static readonly byte[] NewReport=Encoding.UTF8.GetBytes("{\"Synthetic\":\"new report\"}");
    public ExplicitReportFileTests() {Directory.CreateDirectory(Data);Directory.CreateDirectory(App);Directory.CreateDirectory(Share);}

    [Fact]
    public async Task ReplaceRetriesWhileAPriorReportIsBrieflyHeld()
    {
        string report=Path.Combine(Share,"report.json");await File.WriteAllTextAsync(report,"old report");
        var held=new FileStream(report,FileMode.Open,FileAccess.Read,FileShare.Read);
        var clock=new RetryClock();using var stop=new CancellationTokenSource();Task? write=null;
        try
        {
            write=ExplicitReportFile.WriteReportAsync(report,NewReport,Data,App,stop.Token,clock);
            await clock.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromMilliseconds(25),clock.DueTime);
            Assert.False(write.IsCompleted);
            Assert.Equal("old report",await File.ReadAllTextAsync(report));
            await held.DisposeAsync();clock.Expire();await write.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1,clock.Count);
        }
        finally
        {
            stop.Cancel();await held.DisposeAsync();
            if(write is not null)try{await write.WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException) { }
        }
        Assert.Equal(NewReport,await File.ReadAllBytesAsync(report));Assert.Empty(Directory.GetFiles(Share,"*.tmp"));
    }

    [Fact]
    public async Task AFailureInTheChosenFolderNamesThatFolderWithoutItsPathAndKeepsTheOldReport()
    {
        string report=Path.Combine(Share,"report.json");await File.WriteAllTextAsync(report,"old report");File.SetAttributes(report,FileAttributes.ReadOnly);
        ReportFileException error;
        try {error=await Assert.ThrowsAsync<ReportFileException>(()=>ExplicitReportFile.WriteReportAsync(report,NewReport,Data,App,default));}
        finally {File.SetAttributes(report,FileAttributes.Normal);}
        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        // Diagnostics records the cause, not this notice wrapper, so a denied target is not journaled as a plain IOException.
        Assert.Equal(typeof(UnauthorizedAccessException),error.CauseType);
        Assert.Contains("chosen folder",error.Message);Assert.DoesNotContain(Share,error.Message,StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old report",await File.ReadAllTextAsync(report));Assert.Equal(new[] {report},Directory.GetFiles(Share));
    }

    [Fact]
    public async Task AMissingChosenFolderIsNamedAsUnavailableAndIsNotCreated()
    {
        string folder=Path.Combine(Share,"removed");
        var error=await Assert.ThrowsAsync<ReportFileException>(()=>ExplicitReportFile.WriteReportAsync(Path.Combine(folder,"report.json"),NewReport,Data,App,default));
        Assert.IsType<DirectoryNotFoundException>(error.InnerException);Assert.Equal(typeof(DirectoryNotFoundException),error.CauseType);
        Assert.Contains("chosen folder is no longer available",error.Message);Assert.DoesNotContain(folder,error.Message,StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task PathRulesAndCancellationAreNotRewordedAsFolderFailures()
    {
        string settings=Path.Combine(Data,"settings.json");await File.WriteAllTextAsync(settings,"synthetic settings");
        await Assert.ThrowsAsync<ArgumentException>(()=>ExplicitReportFile.WriteReportAsync(settings,NewReport,Data,App,default));
        Assert.Equal("synthetic settings",await File.ReadAllTextAsync(settings));
        string report=Path.Combine(Share,"report.json");await File.WriteAllTextAsync(report,"old report");
        using(var stopped=new CancellationTokenSource())
        {
            stopped.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ExplicitReportFile.WriteReportAsync(report,NewReport,Data,App,stopped.Token));
        }
        // Panic, Lock and shutdown cancel a write that is waiting out a held report; that must stay a cancellation.
        await using(var held=new FileStream(report,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            using var stop=new CancellationTokenSource();var clock=new RetryClock();
            var write=ExplicitReportFile.WriteReportAsync(report,NewReport,Data,App,stop.Token,clock);
            try
            {
                await clock.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(TimeSpan.FromMilliseconds(25),clock.DueTime);Assert.False(write.IsCompleted);
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>write.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                stop.Cancel();try{await write.WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException) { }
            }
        }
        Assert.Equal("old report",await File.ReadAllTextAsync(report));Assert.Equal(new[] {report},Directory.GetFiles(Share));
    }

    // Real File.Replace must first encounter the held destination. Only its retry clock is controlled;
    // the five-second waits contain a stuck fixture and do not redefine the 25-ms production delay.
    private sealed class RetryClock : TimeProvider
    {
        private TimerCallback? _callback;private object? _state;
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan DueTime {get;private set;}
        public int Count {get;private set;}
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {
            DueTime=dueTime;Count++;_callback=callback;_state=state;
            Started.TrySetResult();return new Timer();
        }
        public void Expire()=>_callback!(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime,TimeSpan period)=>true;
            public void Dispose() { }
            public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-report-file-tests-",StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}

/// <summary>
/// Signals when a staged report's temporary file is removed. A failed write removes it just before it throws and checks
/// no cancellation after that, so a UI-thread test that waits here can land Panic or Lock before the export's continuation runs.
/// </summary>
internal sealed class StagedReportRemoval : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ManualResetEventSlim _removed=new();
    public StagedReportRemoval(string folder)
    {
        _watcher=new(folder,"*.tmp") {NotifyFilter=NotifyFilters.FileName};
        _watcher.Deleted+=(_,_)=>_removed.Set();_watcher.EnableRaisingEvents=true;
    }
    public void Wait() {if(!_removed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The staged report was not removed.");}
    public void Dispose() {_watcher.Dispose();_removed.Dispose();}
}
