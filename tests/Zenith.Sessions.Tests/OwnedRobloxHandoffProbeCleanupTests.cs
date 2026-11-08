using System.IO;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// The handoff probe's private-folder cleanup, driven through its tracked task so no WebView2 runtime, window, network
/// or account is needed.
/// </summary>
public sealed class OwnedRobloxHandoffProbeCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-probe-teardown-" + Guid.NewGuid().ToString("N"));

    // A handoff browser that never reports its exit held the probe's cleanup open for good. Never marked kept, its folder
    // read as a browser still closing: sign-in said to finish or cancel that window and native preparation answered Busy
    // for the rest of the session, and Exit never finished, because disposal joins that cleanup before releasing the lease.
    [Fact]
    public async Task ProbeCleanupWhoseBrowserNeverReportsItsExitStopsWaitingSoExitFinishesAndStartupRecoversTheFolder()
    {
        await using var ui = await GatedUiThread.StartAsync(); ui.Release();
        var probe = new OwnedRobloxHandoffProbe(_root, ui.Dispatcher);
        var directory = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        int detached = 0;
        var limit = new ManualDeadline();
        var neverReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var cleanup = OwnedRobloxHandoffProbe.FinishCleanupAsync(null, delegate { }, neverReported.Task, awaitBrowserExit: true,
                directory, priorProblem: false, () => detached++, limit);
            Assert.False(await probe.TrackCleanupAsync(cleanup, directory, TimeSpan.Zero));
            Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed, probe.Refusal);
            await using IRobloxNativeHandoffCapture native = new OwnedRobloxNativeHandoffCapture(_root, ui.Dispatcher);
            await using var signIn = new OwnedRobloxSignIn(_root, ui.Dispatcher);
            // Still closing, not failed: the browser may yet exit, so other owners hear "busy" for now.
            Assert.Equal(RobloxHandoffProbeOutcome.Busy, native.Refusal);
            var waiting = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
            Assert.Contains("Finish or cancel", waiting.Message);
            // The wait for the browser's exit is bounded, well past the five-second observation.
            Assert.Equal(TimeSpan.FromSeconds(30), limit.Requested);
            await limit.FireAsync();
            Assert.Same(cleanup, await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.IsType<TimeoutException>(cleanup.Exception?.InnerException);
            // Nothing releases the folder before Zenith closes now, so every other owner asks for a restart instead.
            Assert.True(SignInProfileDirectory.HasFolderKeptAfterFailedCleanup(_root));
            Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed, native.Refusal);
            var refused = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
            Assert.Contains("Close Zenith Sessions", refused.Message); Assert.DoesNotContain("Finish or cancel", refused.Message);
            // A browser whose exit was never confirmed keeps every guard, and its folder is not touched.
            Assert.Equal(0, detached);
            Assert.True(Directory.Exists(directory.Path));
            // Exit finishes: disposal reports the unconfirmed cleanup by type, releases the lease and leaves the marked
            // folder, which the next startup sweep removes.
            var exit = probe.DisposeAsync().AsTask();
            Assert.Same(exit, await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.IsType<TimeoutException>(exit.Exception?.InnerException);
            Assert.False(SignInProfileDirectory.HasLiveOwner(_root)); Assert.False(SignInProfileDirectory.HasFolderKeptAfterFailedCleanup(_root));
            Assert.True(File.Exists(Path.Combine(directory.Path, ".zenith-private-signin")), "The released folder lost its ownership marker.");
            Assert.Equal(new SignInSweepResult(1, 0), SignInProfileDirectory.SweepOwned(_root, default));
            Assert.False(Directory.Exists(directory.Path));
        }
        // Test hygiene only: a failed assertion must not leave the probe's disposal waiting on this exit.
        finally
        {
            neverReported.TrySetResult();
            await probe.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
