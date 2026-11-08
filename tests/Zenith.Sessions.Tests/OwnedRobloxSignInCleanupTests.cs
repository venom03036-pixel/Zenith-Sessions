using System.IO;
using System.Runtime.InteropServices;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// The sign-in browser's teardown, driven through its step delegates so no WebView2 runtime, window, network or
/// account is needed. After a browser crash the WPF control and its closed controller can reject each native step;
/// none of those failures may skip the later steps or strand the private folder's lease.
/// </summary>
public sealed class OwnedRobloxSignInCleanupTests : IDisposable
{
    // RPC_E_DISCONNECTED: what a call into a controller whose browser process has gone returns.
    private const int Disconnected = unchecked((int)0x80010108);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-signin-teardown-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CrashedBrowserTeardownStillReleasesTheLeaseSoTheNextSignInCanStart()
    {
        await using var ui = await GatedUiThread.StartAsync(); ui.Release();
        await using var signIn = new OwnedRobloxSignIn(_root, ui.Dispatcher);
        var profile = Guid.NewGuid();
        var directory = SignInProfileDirectory.Create(_root, profile, default);
        var steps = new List<string>();
        var teardown = OwnedRobloxSignIn.TeardownAsync(
            stopAndClearCookies: () => { steps.Add("cookies"); throw new InvalidOperationException("browser process crashed"); },
            dispose: () => { steps.Add("dispose"); throw new COMException("controller closed", Disconnected); },
            close: () => steps.Add("close"),
            controllerReady: true, runningBrowserProcesses: null, browserExited: Task.CompletedTask, directory,
            detachGuards: () => steps.Add("detach"));
        Assert.True(await signIn.SettleCleanupAsync(teardown, directory, TimeSpan.FromSeconds(5)),
            "A crashed browser's teardown did not remove its folder after the browser's exit was confirmed.");
        Assert.Equal(new[] { "cookies", "dispose", "close", "detach" }, steps);
        Assert.False(Directory.Exists(directory.Path));
        Assert.False(signIn.CleanupPending);
        // The lease went with the folder: the same account admits a new private folder at once instead of "in use".
        SignInProfileDirectory.Create(_root, profile, default).Remove(default);
    }

    [Fact]
    public async Task TeardownWhoseDisposeFailsStillClosesTheWindowAndKeepsTheFolderUntilTheBrowserExits()
    {
        await using var ui = await GatedUiThread.StartAsync(); ui.Release();
        await using var signIn = new OwnedRobloxSignIn(_root, ui.Dispatcher);
        var directory = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        var steps = new List<string>();
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardown = OwnedRobloxSignIn.TeardownAsync(
            stopAndClearCookies: () => steps.Add("cookies"),
            dispose: () => { steps.Add("dispose"); throw new COMException("controller closed", Disconnected); },
            close: () => steps.Add("close"),
            controllerReady: true, runningBrowserProcesses: null, browserExited: exit.Task, directory,
            detachGuards: () => steps.Add("detach"));
        Assert.False(await signIn.SettleCleanupAsync(teardown, directory, TimeSpan.Zero));
        // The window closed, but the guards stay on the live controller and its folder stays owned, not orphaned.
        Assert.Equal(new[] { "cookies", "dispose", "close" }, steps);
        Assert.True(signIn.CleanupPending, "A failed dispose was not reported as a cleanup problem.");
        Assert.True(Directory.Exists(directory.Path), "The folder was removed while its browser had not exited.");
        // Still closing, not failed: it may yet let go, so other owners hear "busy", not "restart".
        Assert.False(Assert.Throws<SignInDirectoryInUseException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default)).KeptAfterFailedCleanup);
        Assert.True(SignInProfileDirectory.HasLiveOwner(_root)); Assert.False(SignInProfileDirectory.HasFolderKeptAfterFailedCleanup(_root));
        exit.SetResult(); await signIn.CleanupSettled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "cookies", "dispose", "close", "detach" }, steps);
        Assert.False(Directory.Exists(directory.Path)); Assert.False(signIn.CleanupPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitializationWaitsForAStartedBrowserToExitBeforeRemovingItsFolder(bool processQueryFails)
    {
        await using var ui = await GatedUiThread.StartAsync(); ui.Release();
        await using var signIn = new OwnedRobloxSignIn(_root, ui.Dispatcher);
        var directory = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The controller never became ready, but initialization had started a browser that may still use the folder.
        var teardown = OwnedRobloxSignIn.TeardownAsync(null, () => { }, () => { }, controllerReady: false,
            runningBrowserProcesses: () => processQueryFails ? throw new COMException("process list unavailable", Disconnected) : 1,
            exit.Task, directory, () => { });
        Assert.False(await signIn.SettleCleanupAsync(teardown, directory, TimeSpan.FromMilliseconds(500)),
            "The folder was removed while a browser started by the failed initialization could still be using it.");
        Assert.True(Directory.Exists(directory.Path)); Assert.True(signIn.CleanupPending);
        // Its confirmed exit removes the folder, overriding the earlier problem, and readmits sign-in.
        exit.SetResult(); await signIn.CleanupSettled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Directory.Exists(directory.Path)); Assert.False(signIn.CleanupPending);
    }

    // A browser that never reports its exit (for example one left without a controller while its environment lives) held
    // the cleanup's wait open for good: the latch kept saying "still closing. Try again in a few seconds", and Exit never
    // finished, because disposal joins that cleanup before releasing the folder's lease.
    [Fact]
    public async Task CleanupWhoseBrowserNeverReportsItsExitStopsWaitingSoExitFinishesAndStartupRecoversTheFolder()
    {
        await using var ui = await GatedUiThread.StartAsync(); ui.Release();
        await using var signIn = new OwnedRobloxSignIn(_root, ui.Dispatcher);
        var directory = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        var steps = new List<string>();
        var limit = new ManualDeadline();
        var neverReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var teardown = OwnedRobloxSignIn.TeardownAsync(() => steps.Add("cookies"), () => steps.Add("dispose"), () => steps.Add("close"),
                controllerReady: true, runningBrowserProcesses: null, neverReported.Task, directory, () => steps.Add("detach"), limit);
            Assert.False(await signIn.SettleCleanupAsync(teardown, directory, TimeSpan.Zero));
            var pending = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
            Assert.Contains("still closing", pending.Message);
            // The wait for the browser's exit is bounded, well past the five-second observation.
            Assert.Equal(TimeSpan.FromSeconds(30), limit.Requested);
            await limit.FireAsync(); await signIn.CleanupSettled.WaitAsync(TimeSpan.FromSeconds(5));
            // Nothing releases the folder before Zenith closes now, so sign-in and every other owner say so.
            var refused = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
            Assert.Contains("Close Zenith Sessions", refused.Message); Assert.DoesNotContain("few seconds", refused.Message);
            Assert.True(SignInProfileDirectory.HasFolderKeptAfterFailedCleanup(_root));
            Assert.True(Assert.Throws<SignInDirectoryInUseException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default)).KeptAfterFailedCleanup);
            // A browser whose exit was never confirmed keeps every guard, and its folder is not touched.
            Assert.Equal(new[] { "cookies", "dispose", "close" }, steps);
            Assert.True(Directory.Exists(directory.Path));
            // Exit finishes: disposal releases the lease and leaves the marked folder, which the next startup sweep removes.
            await signIn.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(SignInProfileDirectory.HasLiveOwner(_root)); Assert.False(SignInProfileDirectory.HasFolderKeptAfterFailedCleanup(_root));
            Assert.True(File.Exists(Path.Combine(directory.Path, ".zenith-private-signin")), "The released folder lost its ownership marker.");
            Assert.Equal(new SignInSweepResult(1, 0), SignInProfileDirectory.SweepOwned(_root, default));
            Assert.False(Directory.Exists(directory.Path));
        }
        // Test hygiene only: a failed assertion must not leave the owner's disposal waiting on this exit.
        finally { neverReported.TrySetResult(); }
    }

    // A sign-in whose cleanup failed keeps its folder and lease until it is disposed, when Zenith closes, so every other
    // owner's admission fails until then. Counted as a live owner, inspection and native preparation answered Busy
    // ("still open or closing ... retry") for the rest of the session.
    [Fact]
    public async Task SignInFolderKeptAfterAFailedCleanupIsReportedAsCleanupFailedNotBusyUntilDisposal()
    {
        await using var ui = await GatedUiThread.StartAsync(); ui.Release();
        await using var signIn = new OwnedRobloxSignIn(_root, ui.Dispatcher);
        var directory = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        // No browser was running, but closing the window failed: the folder is preserved rather than removed.
        var teardown = OwnedRobloxSignIn.TeardownAsync(null, () => { }, () => throw new InvalidOperationException("window already closed"),
            controllerReady: false, runningBrowserProcesses: () => 0, Task.CompletedTask, directory, () => { });
        Assert.False(await signIn.SettleCleanupAsync(teardown, directory, TimeSpan.FromSeconds(5)));
        Assert.True(Directory.Exists(directory.Path));
        await using IRobloxHandoffProbe probe = new OwnedRobloxHandoffProbe(_root, ui.Dispatcher);
        await using IRobloxNativeHandoffCapture native = new OwnedRobloxNativeHandoffCapture(_root, ui.Dispatcher);
        Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed, probe.Refusal);
        Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed, native.Refusal);
        // A check that starts anyway reaches admission, which refuses the same way instead of reporting another owner busy.
        using var credential = new RobloxSessionCredential("synthetic-only");
        using (var result = await probe.InspectAsync(new(Guid.NewGuid(), 456, 1, 42), credential, default))
        {
            Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed, result.Result.Outcome);
            string status = ShellViewModel.DescribeHandoffResult(result.Result, 42);
            Assert.Contains("close Zenith", status); Assert.Contains("signin-browser-data", status);
        }
        Assert.True(Directory.Exists(directory.Path), "Another owner's kept folder was opened or removed.");
        // Released, as when Zenith closes: nothing was latched elsewhere, and the next admission recovers the folder.
        await signIn.DisposeAsync();
        Assert.Null(probe.Refusal); Assert.Null(native.Refusal);
        SignInProfileDirectory.Create(_root, Guid.NewGuid(), default).Remove(default);
        Assert.False(Directory.Exists(directory.Path), "The next admission did not recover the released folder.");
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
