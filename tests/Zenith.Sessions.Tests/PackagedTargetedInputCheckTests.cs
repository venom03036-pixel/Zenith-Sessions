using System.IO;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

// The packaged "Controlled targeted input ownership" stage, run against its in-memory transport only.
public sealed class PackagedTargetedInputCheckTests
{
    [Fact]
    public async Task StagePassesAndShutsTheOwnerDownWhenNothingStalls()
        => await PackagedTargetedInputCheck.VerifyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task StageFailsWithATimeoutWhenTheOwnerCannotShutDownAfterTheDeadline()
    {
        var timeout = await StalledShutdownAfterTheDeadlineAsync();
        Assert.IsAssignableFrom<OperationCanceledException>(timeout.InnerException); // the deadline failure is kept, not dropped
    }

    [Fact]
    public async Task StageReportPointsAtTheStalledShutdownAndNamesTheDeadlineAsItsCause()
    {
        // N-B10-2: the code and method must describe the timeout the case names, not the deadline cancellation it keeps.
        var timeout = await StalledShutdownAfterTheDeadlineAsync();
        var failed = DistributionSelfCheck.FailedCase("Controlled targeted input ownership", timeout, null);
        Assert.Equal(nameof(TimeoutException), failed.ErrorType);
        // N-B10-3: the shared bounded-shutdown helper throws the timeout, so the method names the owner whose shutdown stalled.
        Assert.Equal(typeof(TargetedInputCoordinator).FullName + ".DisposeAsync", failed.FailingMethod);
        Assert.Equal($"0x{timeout.HResult:X8}", failed.ErrorCode);
        Assert.Equal(timeout.InnerException!.GetType().Name, failed.CauseType);
    }

    private static async Task<TimeoutException> StalledShutdownAfterTheDeadlineAsync()
    {
        var stalled = new TaskCompletionSource<TargetedInputValidation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource();
        StalledCleanupTransport? transport = null;
        var run = PackagedTargetedInputCheck.VerifyAsync(inner => transport = new StalledCleanupTransport(inner, stalled.Task),
            TimeSpan.FromMilliseconds(200), deadline.Token);
        try
        {
            // This case tests stalled disposal after successful setup, not setup exceeding a timer.
            await transport!.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, transport.Submissions);
            deadline.Cancel();
            Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
            return await Assert.ThrowsAsync<TimeoutException>(() => run);
        }
        finally { stalled.TrySetResult(TargetedInputValidation.Current); } // let the abandoned owner's workers retire
    }

    [Fact]
    public async Task SetupCancellationCannotBeMistakenForAStalledCleanup()
    {
        using var deadline = new CancellationTokenSource();
        StalledCleanupTransport? transport = null;
        var run = PackagedTargetedInputCheck.VerifyAsync(inner => transport = new StalledCleanupTransport(inner,
            Task.FromResult(TargetedInputValidation.Current), holdSetup: true), TimeSpan.FromMilliseconds(200), deadline.Token);
        await transport!.SetupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        deadline.Cancel();
        await Assert.ThrowsAsync<InvalidDataException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, transport.Submissions);
        Assert.False(transport.CleanupEntered.Task.IsCompleted);
    }

    // A cleanup check that ignores its token, like a hung native call: the first pause and every
    // later stop wait on it, so the owner's disposal (which joins them) cannot finish either.
    private sealed class StalledCleanupTransport(ITargetedInputTransport inner, Task<TargetedInputValidation> stalled, bool holdSetup = false) : ITargetedInputTransport
    {
        private int _firstValidationHeld;
        private int _submissions;
        public int Submissions => Volatile.Read(ref _submissions);
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SetupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
        {
            if (cleanup) { CleanupEntered.TrySetResult(); return await stalled; }
            if (holdSetup && Interlocked.Exchange(ref _firstValidationHeld, 1) == 0)
            {
                // Model cancellation before any hold exists, independently of disposal timing.
                var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = ct.Register(() => expired.TrySetResult());
                SetupEntered.TrySetResult();
                await expired.Task;
            }
            return await inner.ValidateAsync(target, cleanup, ct);
        }
        public async ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            var result = await inner.SubmitAsync(command, admissionOpen, ct);
            Interlocked.Increment(ref _submissions);
            return result;
        }
    }
}
