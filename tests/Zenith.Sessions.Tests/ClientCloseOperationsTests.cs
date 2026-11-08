using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>
/// R-instances-12: the escalating close and the confirmed force close, run against a scripted adapter so no time passes and
/// nothing native is touched. The adapter stands in for one exact process instance; the tests pin the order of the posts, the
/// two bounded waits, the status for every way a client can answer, and that stopping never sends a termination.
/// </summary>
public sealed class ClientCloseOperationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static SessionObservation Session(int pid = 4242) => new(pid, Now.AddMinutes(-5).UtcTicks, "Synthetic", 77, "Synthetic", false, true, 0, 0, null, Now);
    private static ClientCloseOperations Operations(ScriptedAdapter adapter, TimeSpan? grace = null)
        => new(adapter, new FixedClock(Now), grace);

    [Fact]
    public async Task AClientThatClosesOnTheFirstRequestIsReportedExitedAfterOneRequest()
    {
        var adapter = new ScriptedAdapter { ExitAfterWait = 1 };
        var result = await Operations(adapter).CloseAsync(Session(), default);
        Assert.Equal(ClientCloseStatus.Exited, result.Status);
        Assert.Equal(1, result.CloseRequests);
        Assert.False(result.OffersForceClose);
        Assert.Equal(["open", "post", $"wait {ClientCloseOperations.LeavePromptDelay.TotalMilliseconds}", "dispose"], adapter.Calls);
        Assert.Contains("exited after one close request", result.Explanation);
    }

    [Fact]
    public async Task AClientThatShowsItsLeavePromptGetsASecondRequestAfterTheGapAndIsReportedExited()
    {
        var adapter = new ScriptedAdapter { ExitAfterWait = 2 };
        var result = await Operations(adapter).CloseAsync(Session(), default);
        Assert.Equal(ClientCloseStatus.Exited, result.Status);
        Assert.Equal(2, result.CloseRequests);
        var remaining = ClientCloseOperations.DefaultExitGrace - ClientCloseOperations.LeavePromptDelay;
        Assert.Equal(["open", "post", $"wait {ClientCloseOperations.LeavePromptDelay.TotalMilliseconds}", "post", $"wait {remaining.TotalMilliseconds}", "dispose"], adapter.Calls);
        Assert.Contains("exited after two close requests", result.Explanation);
    }

    [Fact]
    public async Task AClientStillAliveAfterTheBoundedWaitIsReportedStillOpenAndOffersForceCloseWithoutEndingIt()
    {
        var adapter = new ScriptedAdapter { ExitAfterWait = int.MaxValue };
        var result = await Operations(adapter, TimeSpan.FromSeconds(3)).CloseAsync(Session(), default);
        Assert.Equal(ClientCloseStatus.StillOpen, result.Status);
        Assert.Equal(2, result.CloseRequests);
        Assert.True(result.OffersForceClose);
        Assert.Contains("still open 3 s after two close requests", result.Explanation);
        Assert.DoesNotContain("terminate", adapter.Calls);
        Assert.Equal(0, adapter.Terminations);
    }

    [Fact]
    public async Task AClientWhoseWindowWentAwayAfterTheFirstRequestStillGetsTheFullWait()
    {
        // Roblox tears its window down before the process exits; the second post then fails but the wait continues.
        var adapter = new ScriptedAdapter { ExitAfterWait = 2, PostResults = [true, false] };
        var result = await Operations(adapter).CloseAsync(Session(), default);
        Assert.Equal(ClientCloseStatus.Exited, result.Status);
        Assert.Equal(1, result.CloseRequests);
        Assert.Equal(2, adapter.Calls.Count(call => call == "post"));
        Assert.Equal(2, adapter.Calls.Count(call => call.StartsWith("wait", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AnInstanceThatIsGoneOrChangedIsNotCurrentAndNothingIsSent()
    {
        var adapter = new ScriptedAdapter { OpenFails = true };
        var result = await Operations(adapter).CloseAsync(Session(), default);
        Assert.Equal(ClientCloseStatus.NotCurrent, result.Status);
        Assert.Equal(0, result.CloseRequests);
        Assert.False(result.OffersForceClose);
        Assert.Equal(["open"], adapter.Calls);
    }

    [Fact]
    public async Task ARefusedFirstPostIsNotSentAndDoesNotOfferForceClose()
    {
        var adapter = new ScriptedAdapter { PostResults = [false] };
        var result = await Operations(adapter).CloseAsync(Session(), default);
        Assert.Equal(ClientCloseStatus.NotSent, result.Status);
        Assert.Equal(0, result.CloseRequests);
        Assert.False(result.OffersForceClose);
        Assert.Equal(["open", "post", "dispose"], adapter.Calls);
    }

    [Fact]
    public async Task CancelDuringTheWaitReportsCancelledWithTheRequestsAlreadySentAndNeverTerminates()
    {
        var adapter = new ScriptedAdapter { ExitAfterWait = int.MaxValue };
        var operations = Operations(adapter);
        adapter.OnWait = count => { if (count == 1) operations.CancelCloseOperations(); };
        var result = await operations.CloseAsync(Session(), default);
        Assert.Equal(ClientCloseStatus.Cancelled, result.Status);
        Assert.Equal(1, result.CloseRequests);
        Assert.False(result.OffersForceClose);
        Assert.Equal(1, adapter.Calls.Count(call => call == "post"));
        Assert.Equal(0, adapter.Terminations);
        Assert.Contains("cannot be recalled", result.Explanation);
    }

    [Fact]
    public async Task ATokenCancelledDuringTheWaitReportsCancelledWithTheRequestsAlreadySentAndPostsNothingMore()
    {
        // The shell records a close request only from the result, so a stop during the wait must keep the count.
        var adapter = new ScriptedAdapter { ExitAfterWait = int.MaxValue };
        using var stop = new CancellationTokenSource();
        adapter.OnWait = count => { if (count == 1) stop.Cancel(); };
        var result = await Operations(adapter).CloseAsync(Session(), stop.Token);
        Assert.Equal(ClientCloseStatus.Cancelled, result.Status);
        Assert.Equal(1, result.CloseRequests);
        Assert.Equal(1, adapter.Calls.Count(call => call == "post"));
        Assert.Contains("dispose", adapter.Calls);
        // Cancelled before anything was sent, the contract of every other window operation holds: it throws.
        using var early = new CancellationTokenSource(); early.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operations(new ScriptedAdapter()).CloseAsync(Session(), early.Token));
    }

    [Fact]
    public async Task ForceCloseTerminatesThroughTheCheckedHandleStampsTheTimeAndConfirmsTheExit()
    {
        var adapter = new ScriptedAdapter { ExitAfterWait = 1 };
        var result = await Operations(adapter).ForceCloseAsync(Session(), default);
        Assert.Equal(ClientForceCloseStatus.Ended, result.Status);
        Assert.True(result.Accepted);
        Assert.Equal(Now, result.EndedUtc);
        Assert.Equal(["open-terminate", "terminate", $"wait {ClientCloseOperations.TerminationGrace.TotalMilliseconds}", "dispose"], adapter.Calls);
        Assert.Contains("ended by force", result.Explanation);
    }

    [Fact]
    public async Task ForceCloseWhoseExitIsNotSeenInTimeIsAcceptedButUnconfirmed()
    {
        var adapter = new ScriptedAdapter { ExitAfterWait = int.MaxValue };
        var result = await Operations(adapter).ForceCloseAsync(Session(), default);
        Assert.Equal(ClientForceCloseStatus.ExitUnconfirmed, result.Status);
        Assert.True(result.Accepted);
        Assert.Equal(Now, result.EndedUtc);
        Assert.Equal(1, adapter.Terminations);
    }

    [Theory]
    [InlineData(true, ClientForceCloseStatus.NotCurrent)]
    [InlineData(false, ClientForceCloseStatus.Denied)]
    public async Task ForceCloseThatWindowsDoesNotAcceptEndsNothingAndStampsNoTime(bool alreadyExited, ClientForceCloseStatus expected)
    {
        var adapter = new ScriptedAdapter { TerminateResult = alreadyExited ? ClientTerminateStatus.AlreadyExited : ClientTerminateStatus.Denied };
        var result = await Operations(adapter).ForceCloseAsync(Session(), default);
        Assert.Equal(expected, result.Status);
        Assert.False(result.Accepted);
        Assert.Null(result.EndedUtc);
        Assert.Contains("Nothing was ended", result.Explanation);
        Assert.DoesNotContain(adapter.Calls, call => call.StartsWith("wait", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForceCloseOnAnInstanceThatIsGoneIsNotCurrentAndOpensNothingElse()
    {
        var adapter = new ScriptedAdapter { OpenFails = true };
        var result = await Operations(adapter).ForceCloseAsync(Session(), default);
        Assert.Equal(ClientForceCloseStatus.NotCurrent, result.Status);
        Assert.Null(result.EndedUtc);
        Assert.Equal(["open-terminate"], adapter.Calls);
        Assert.Equal(0, adapter.Terminations);
    }

    [Fact]
    public async Task ForceCloseAfterCancelSendsNoTermination()
    {
        var adapter = new ScriptedAdapter();
        var operations = Operations(adapter);
        operations.CancelCloseOperations();
        // Cancel before the call stops nothing: a later click is a new decision. Cancel during the open does.
        adapter.OnOpen = () => operations.CancelCloseOperations();
        var result = await operations.ForceCloseAsync(Session(), default);
        Assert.Equal(ClientForceCloseStatus.Cancelled, result.Status);
        Assert.Null(result.EndedUtc);
        Assert.Equal(0, adapter.Terminations);
        Assert.Contains("dispose", adapter.Calls);
    }

    [Fact]
    public async Task InvalidObservationsAreRefusedBeforeAnyHandleIsOpened()
    {
        var adapter = new ScriptedAdapter();
        var operations = Operations(adapter);
        var noPid = Session() with { ProcessId = 0 };
        var noStart = Session() with { ProcessStartUtcTicks = 0 };
        Assert.Equal(ClientCloseStatus.NotCurrent, (await operations.CloseAsync(noPid, default)).Status);
        Assert.Equal(ClientCloseStatus.NotCurrent, (await operations.CloseAsync(noStart, default)).Status);
        Assert.Equal(ClientForceCloseStatus.NotCurrent, (await operations.ForceCloseAsync(noPid, default)).Status);
        Assert.Empty(adapter.Calls);
        await Assert.ThrowsAsync<ArgumentNullException>(() => operations.CloseAsync(null!, default));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ScriptedAdapter : IClientCloseAdapter
    {
        public List<string> Calls { get; } = [];
        public bool OpenFails;
        /// <summary>The 1-based wait after which the process reads as exited; int.MaxValue never exits.</summary>
        public int ExitAfterWait = int.MaxValue;
        public bool[] PostResults = [true, true];
        public ClientTerminateStatus TerminateResult = ClientTerminateStatus.Accepted;
        public int Terminations;
        public Action<int>? OnWait;
        public Action? OnOpen;
        private int _posts, _waits;

        public IClientCloseHandle? Open(SessionObservation session, bool forTermination)
        {
            Calls.Add(forTermination ? "open-terminate" : "open");
            OnOpen?.Invoke();
            return OpenFails ? null : new Handle(this);
        }

        private sealed class Handle(ScriptedAdapter owner) : IClientCloseHandle
        {
            public bool PostClose(SessionObservation session)
            {
                owner.Calls.Add("post");
                int index = owner._posts++;
                return index < owner.PostResults.Length && owner.PostResults[index];
            }
            public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
            {
                owner.Calls.Add($"wait {timeout.TotalMilliseconds}");
                int count = ++owner._waits;
                owner.OnWait?.Invoke(count);
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(count >= owner.ExitAfterWait);
            }
            public ClientTerminateStatus Terminate()
            {
                owner.Calls.Add("terminate");
                if (owner.TerminateResult == ClientTerminateStatus.Accepted) owner.Terminations++;
                return owner.TerminateResult;
            }
            public void Dispose() => owner.Calls.Add("dispose");
        }
    }
}
