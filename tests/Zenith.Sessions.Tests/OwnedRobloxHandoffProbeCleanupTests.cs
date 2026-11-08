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

    // Z1559 (owner decision 2026-11-08, option a): Exit joined the probe's cleanup for as long as that cleanup waited for the
    // browser's exit, so closing Zenith within the cleanup's own limit still waited the rest of it. Exit now waits at most
    // ten seconds, then releases the folder's lease and leaves the marked folder for the startup sweep. The cleanup after a
    // normal cancel is unchanged: it still joins the real exit, for at most its own limit.
    [Fact]
    public async Task ExitStopsWaitingForACleanupStillWaitingOnItsBrowserAfterTenSecondsAndLeavesTheMarkedFolderForTheNextStart()
    {
        await using var ui = await GatedUiThread.StartAsync(); ui.Release();
        var exitLimit = new ManualDeadlines();
        var probe = new OwnedRobloxHandoffProbe(_root, ui.Dispatcher, exitLimit);
        var directory = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        int detached = 0;
        // The cleanup's own 30-second wait for the browser, never fired here: Exit comes first.
        var browserLimit = new ManualDeadline();
        var neverReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = OwnedRobloxHandoffProbe.FinishCleanupAsync(null, delegate { }, neverReported.Task, awaitBrowserExit: true,
            directory, priorProblem: false, () => detached++, browserLimit);
        try
        {
            Assert.False(await probe.TrackCleanupAsync(cleanup, directory, TimeSpan.Zero));
            Assert.Equal(TimeSpan.FromSeconds(30), browserLimit.Requested);
            var exit = probe.DisposeAsync().AsTask();
            // Exit arms a limit of its own on the join instead of waiting out the browser's exit or the cleanup's limit.
            var armed = exitLimit.ArmedAsync(TimeSpan.FromSeconds(10));
            Assert.True(ReferenceEquals(armed, await Task.WhenAny(armed, Task.Delay(TimeSpan.FromSeconds(5)))),
                "Exit joined the browser's cleanup with no limit of its own.");
            await armed;
            Assert.False(exit.IsCompleted, "Exit finished before its limit while the browser had not exited.");
            await exitLimit.FireAsync(TimeSpan.FromSeconds(10));
            Assert.Same(exit, await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(5))));
            // Reported by type: the cleanup was not confirmed.
            Assert.IsType<TimeoutException>(exit.Exception?.InnerException);
            // Only Exit stopped waiting. The cleanup still waits for the real exit, and a browser whose exit was never
            // confirmed keeps every guard.
            Assert.False(cleanup.IsCompleted, "Exit's limit ended the cleanup's own wait for the browser.");
            Assert.Equal(0, detached);
            // The lease is released and nothing stays latched past this process; the folder keeps its marker and its data
            // for the next start, whose sweep removes it once the browser is gone.
            Assert.False(SignInProfileDirectory.HasLiveOwner(_root)); Assert.False(SignInProfileDirectory.HasFolderKeptAfterFailedCleanup(_root));
            Assert.True(Directory.Exists(directory.Path));
            Assert.True(File.Exists(Path.Combine(directory.Path, ".zenith-private-signin")), "The released folder lost its ownership marker.");
            Assert.Equal(new SignInSweepResult(1, 0), SignInProfileDirectory.SweepOwned(_root, default));
            Assert.False(Directory.Exists(directory.Path));
        }
        // Test hygiene only: the cleanup must not outlive the test, and a failed assertion must not leave Exit waiting.
        finally
        {
            neverReported.TrySetResult();
            await cleanup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            await probe.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
