using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

// Every owner a packaged self-check stage creates is shut down within a bound, so a stalled owner cannot hold the report.
public sealed class SelfCheckOwnersTests
{
    // These owners have no worker to join, or bound their own shutdown, so a plain `await using` cannot hold the report:
    // an in-memory SQLite probe, a synthetic fixture whose disposal is already complete, the report's own hash read, and the
    // diagnostics journal, which waits at most 3 s for its writer and then cancels it.
    private static readonly string[] UnboundedDisposalAllowed=["SqliteConnection","SyntheticNativePreparation","FileStream","ManagerDiagnostics"];

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
}
