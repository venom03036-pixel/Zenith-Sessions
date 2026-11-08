using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

// Every owner a packaged self-check stage creates is shut down within a bound, so a stalled owner cannot hold the report.
public sealed class SelfCheckOwnersTests
{
    // These owners have no worker to join: an in-memory SQLite probe, an already-complete synthetic fixture,
    // and the report's local hash-read stream. Diagnostics joins its writer even after cancellation, so needs the helper.
    private static readonly string[] UnboundedDisposalAllowed=["SqliteConnection","SyntheticNativePreparation","FileStream"];

    [Fact]
    public void EverySelfCheckStageShutsItsWorkerOwnersDownThroughTheBoundedHelper()
    {
        string app=Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"src","Zenith.Sessions.App");
        var files=Directory.GetFiles(app,"Packaged*Check.cs").Append(Path.Combine(app,"DistributionSelfCheck.cs"));
        var unbounded=files.SelectMany(file=>File.ReadAllLines(file).Select((line,index)=>(File:Path.GetFileName(file),Line:index+1,Text:line)))
            .Where(line=>!line.Text.TrimStart().StartsWith("//",StringComparison.Ordinal) && Regex.IsMatch(line.Text,@"\bawait\s+using\b"))
            .Where(line=>Regex.Match(line.Text,@"\bnew\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)") is not { Success:true } created
                || Array.IndexOf(UnboundedDisposalAllowed,created.Groups["type"].Value)<0)
            .Select(line=>line.File+":"+line.Line+": "+line.Text.Trim()).ToArray();
        Assert.Empty(unbounded);
        var direct=files.SelectMany(file=>File.ReadAllLines(file).Select((line,index)=>(File:Path.GetFileName(file),Line:index+1,Text:line)))
            .Where(line=>!line.Text.TrimStart().StartsWith("//",StringComparison.Ordinal)
                && Regex.IsMatch(line.Text,@"\bawait\s+\w+\.DisposeAsync\s*\("))
            .Select(line=>line.File+":"+line.Line).ToArray();
        Assert.Empty(direct);
    }

    [Fact]
    public async Task ExpiredWaitKeepsItsCauseEvenWhenTheOwnerFinishesBeforeTheContinuationRuns()
    {
        var owner=new StalledOwner();var clock=new ManualDeadline();
        var workFailure=new InvalidDataException("Synthetic failure");
        var run=SelfCheckOwners.UseAsync(owner,_=>Task.FromException(workFailure),TimeSpan.FromSeconds(2),clock);
        clock.Expire();owner.Release();
        var failure=await Assert.ThrowsAsync<TimeoutException>(()=>run);
        Assert.Same(workFailure,failure.InnerException);
        Assert.Equal(typeof(StalledOwner).FullName,SelfCheckOwners.StalledOwner(failure));
    }

    [Fact]
    public async Task AnOwnersOwnTimeoutIsNotMislabelledAsAnExpiredShutdownWait()
    {
        var original=new TimeoutException("Synthetic owner failure");
        var failure=await Assert.ThrowsAsync<TimeoutException>(()=>SelfCheckOwners.UseAsync(new FailedOwner(original),_=>Task.CompletedTask));
        Assert.Same(original,failure);Assert.Null(SelfCheckOwners.StalledOwner(failure));
    }

    [Fact]
    public async Task AStalledDiagnosticsWriterCannotHoldThePackagedReport()
    {
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string root=Path.Combine(Path.GetTempPath(),"zenith-diagnostics-stall-"+Guid.NewGuid().ToString("N"));
        var journal=new Zenith.Sessions.Windows.ManagerDiagnostics(root,1,256,_=>gate.Task);
        try
        {
            var failure=await Assert.ThrowsAsync<TimeoutException>(()=>SelfCheckOwners.UseAsync(journal,_=>Task.CompletedTask,TimeSpan.FromMilliseconds(20)));
            Assert.Equal(typeof(Zenith.Sessions.Windows.ManagerDiagnostics).FullName,SelfCheckOwners.StalledOwner(failure));
        }
        finally { gate.TrySetResult();await journal.DisposeAsync(); }
        Assert.False(Directory.Exists(root)); // No event was written; no filesystem cleanup is needed.
    }

    [Fact]
    public async Task AStalledOwnerFailsAsATimeoutThatKeepsTheWorkFailureAsItsCause()
    {
        var owner=new StalledOwner();
        try
        {
            var run=SelfCheckOwners.UseAsync(owner,_=>Task.FromException(new OperationCanceledException()),TimeSpan.FromMilliseconds(100));
            Assert.Same(run,await Task.WhenAny(run,Task.Delay(TimeSpan.FromSeconds(5))));
            var timeout=await Assert.ThrowsAsync<TimeoutException>(()=>run);
            Assert.IsType<OperationCanceledException>(timeout.InnerException);
            Assert.Equal(1,owner.DisposeCalls);
            // The report locates the stalled shutdown at the owner's DisposeAsync and names the work failure as its cause.
            var failed=DistributionSelfCheck.FailedCase("Stage",timeout,null);
            Assert.Equal(new DistributionSelfCheck.Case("Stage",false,nameof(TimeoutException),$"0x{timeout.HResult:X8}",null,
                typeof(StalledOwner).FullName+".DisposeAsync",nameof(OperationCanceledException)),failed);
        }
        finally { owner.Release(); }
    }

    [Fact]
    public async Task AnOwnerThatShutsDownIsDisposedAndTheWorkFailureIsRethrownUnchanged()
    {
        var owner=new StalledOwner();owner.Release();
        var failure=new InvalidDataException("Synthetic stage failure.");
        var thrown=await Assert.ThrowsAsync<InvalidDataException>(()=>SelfCheckOwners.UseAsync(owner,_=>Task.FromException(failure),TimeSpan.FromSeconds(5)));
        Assert.Same(failure,thrown);
        Assert.Equal(1,owner.DisposeCalls);
        Assert.Null(SelfCheckOwners.StalledOwner(thrown));
    }

    [Fact]
    public async Task AnOwnerAroundAStalledOwnerIsAskedToStopButNotWaitedForAndTheFirstStallStaysTheFailure()
    {
        // The outer bound is far longer than the test allows: one stalled stage must cost one bound, not one per nested owner.
        var outer=new StalledOuterOwner();var inner=new StalledOwner();
        try
        {
            var run=SelfCheckOwners.UseAsync(outer,_=>SelfCheckOwners.UseAsync(inner,_=>Task.CompletedTask,TimeSpan.FromMilliseconds(100)),TimeSpan.FromSeconds(60));
            Assert.Same(run,await Task.WhenAny(run,Task.Delay(TimeSpan.FromSeconds(10))));
            var timeout=await Assert.ThrowsAsync<TimeoutException>(()=>run);
            Assert.Equal(typeof(StalledOwner).FullName,SelfCheckOwners.StalledOwner(timeout));
            Assert.Null(timeout.InnerException);
            Assert.Equal(1,outer.DisposeCalls);Assert.Equal(1,inner.DisposeCalls);
        }
        finally { inner.Release();outer.Release(); }
    }

    // An owner whose shutdown hangs, like one joining a worker stuck in a token-less native call, until Release.
    private class StalledOwner : IAsyncDisposable
    {
        private readonly TaskCompletionSource _stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCalls;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCalls);return new(_stopped.Task); }
        public void Release()=>_stopped.TrySetResult();
    }
    private sealed class StalledOuterOwner : StalledOwner;
    private sealed class FailedOwner(Exception failure) : IAsyncDisposable { public ValueTask DisposeAsync()=>new(Task.FromException(failure)); }
    private sealed class ManualDeadline : TimeProvider
    {
        private TimerCallback? _callback;private object? _state;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        { _callback=callback;_state=state;return new Timer(); }
        public void Expire()=>_callback!(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime,TimeSpan period)=>true;
            public void Dispose() { }
            public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        }
    }
}
