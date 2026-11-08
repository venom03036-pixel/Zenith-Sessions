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
        var stalled = new TaskCompletionSource<TargetedInputValidation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var run = PackagedTargetedInputCheck.VerifyAsync(inner => new StalledCleanupTransport(inner, stalled.Task),
            TimeSpan.FromMilliseconds(200), deadline.Token);
        try
        {
            Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
            var timeout = await Assert.ThrowsAsync<TimeoutException>(() => run);
            Assert.IsAssignableFrom<OperationCanceledException>(timeout.InnerException); // the deadline failure is kept, not dropped
        }
        finally { stalled.TrySetResult(TargetedInputValidation.Current); } // let the abandoned owner's workers retire
    }

    // A cleanup check that ignores its token, like a hung native call: the first pause and every
    // later stop wait on it, so the owner's disposal (which joins them) cannot finish either.
    private sealed class StalledCleanupTransport(ITargetedInputTransport inner, Task<TargetedInputValidation> stalled) : ITargetedInputTransport
    {
        public ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
            => cleanup ? new(stalled) : inner.ValidateAsync(target, cleanup, ct);
        public ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
            => inner.SubmitAsync(command, admissionOpen, ct);
    }
}
