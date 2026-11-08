using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

// The packaged "All-observed tiled Windows capture scheduling" stage, run against its in-memory fixture provider only.
public sealed class PackagedWindowsOverviewCheckTests
{
    [Fact]
    public async Task StagePassesAndShutsThePreviewOwnerDownWhenNothingStalls()
        => await PackagedWindowsOverviewCheck.VerifyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task StageFailsWithATimeoutNamingThePreviewOwnerWhenItsCaptureCleanupStalls()
    {
        // N-B10-3: the preview owner's DisposeAsync takes no token and waits for its provider, so a stalled
        // capture cleanup must fail the stage within the bound instead of holding the report.
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var run = PackagedWindowsOverviewCheck.VerifyAsync(inner => new StalledDisposeProvider(inner, stalled.Task),
                TimeSpan.FromMilliseconds(200), CancellationToken.None);
            Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
            var timeout = await Assert.ThrowsAsync<TimeoutException>(() => run);
            Assert.Null(timeout.InnerException); // the stage's own work passed; only the shutdown failed
            var failed = DistributionSelfCheck.FailedCase("All-observed tiled Windows capture scheduling", timeout, null);
            Assert.Equal(typeof(ClientPreviewCoordinator).FullName + ".DisposeAsync", failed.FailingMethod);
            Assert.Null(failed.CauseType);
        }
        finally { stalled.TrySetResult(); } // let the abandoned owner's worker retire
    }

    // A capture provider whose own disposal hangs like a stuck native release; everything else is the fixture's.
    private sealed class StalledDisposeProvider(IClientPreviewBatchProvider inner, Task stalled) : IClientPreviewBatchProvider
    {
        public Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
            => inner.CaptureAsync(targets, generation, ct);
        public Task<ClientPreviewBatchResult> CaptureBatchAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
            => inner.CaptureBatchAsync(targets, generation, ct);
        public Task ReleaseAsync(CancellationToken ct) => inner.ReleaseAsync(ct);
        public ValueTask DisposeAsync() => new(stalled);
    }
}
