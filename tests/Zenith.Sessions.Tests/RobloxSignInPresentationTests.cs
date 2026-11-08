using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class RobloxSignInPresentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-owned-signin-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("https://www.roblox.com/login", true)]
    [InlineData("https://roblox.com/home", true)]
    [InlineData("https://apis.roblox.com/", true)]
    [InlineData("https://WWW.ROBLOX.COM:443/", true)]
    [InlineData("https://roblox.com.evil.invalid/", false)]
    [InlineData("https://evilroblox.com/", false)]
    [InlineData("https://www.roblox.com:1234/login", false)]
    [InlineData("https://roblox.com@evil.invalid/", false)]
    [InlineData("https://user@www.roblox.com/", false)]
    [InlineData("http://www.roblox.com/login", false)]
    [InlineData("file:///C:/Windows/test.html", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("roblox-player:launch", false)]
    [InlineData("https://accounts.google.com/", false)]
    [InlineData(null, false)]
    public void TopLevelSignInNavigationCannotDispatchExternalPages(string? value, bool expected)
        => Assert.Equal(expected, RobloxSignInNavigation.IsAllowed(value));

    // Roblox's Play button delivers roblox-player: through a hidden child frame, which NavigationStarting never sees.
    // Frames stay scheme-based: Roblox's sign-in challenges may load third-party HTTPS frames.
    [Theory]
    [InlineData("roblox-player:1+launchmode:play", false)]
    [InlineData("roblox:", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("javascript:x", false)]
    [InlineData("http://www.roblox.com/", false)]
    [InlineData("file:///C:/Windows/test.html", false)]
    [InlineData("about:settings", false)]
    [InlineData("not a uri", false)]
    [InlineData(null, false)]
    [InlineData("about:blank", true)]
    [InlineData("about:srcdoc", true)]
    [InlineData("blob:https://www.roblox.com/0d1f9c1e-0000-4000-8000-000000000000", true)]
    [InlineData("data:text/html,synthetic", true)]
    [InlineData("https://www.roblox.com/games/1", true)]
    [InlineData("https://challenge.example.invalid/frame", true)]
    public void FrameNavigationCannotDispatchNativeSchemes(string? value, bool expected)
        => Assert.Equal(expected, RobloxSignInNavigation.IsAllowedFrame(value));

    // The guards need a live CoreWebView2 to run, so their bodies run in the tests below. This supplement checks that the
    // live handlers are exactly those calls and are attached to the navigating CoreWebView2 on every path that opens a page.
    [Fact]
    public void SignInWindowCancelsExternalSchemeLaunchesBeforeNavigating()
    {
        string code = ProductionSourceText.Code("ProductionOwnedRobloxSignIn.cs");
        ProductionSourceText.AssertRunsOnEveryPathTo(code, "core.LaunchingExternalUriScheme += ExternalScheme;", "core.Navigate(",
            "The sign-in window can open Roblox's pages without its LaunchingExternalUriScheme guard installed on that CoreWebView2 first.");
        ProductionSourceText.AssertRunsOnEveryPathTo(code, "core.FrameNavigationStarting += FrameNavigationStarting;", "core.Navigate(",
            "The sign-in window can open Roblox's pages without its FrameNavigationStarting guard installed on that CoreWebView2 first.");
        Assert.Equal("RefuseExternalLaunch(()=>e.Cancel=true,text=>status.Text=text);", ProductionSourceText.HandlerBody(code, "void ExternalScheme("));
        Assert.Equal("GuardFrameNavigation(e.Uri,()=>e.Cancel=true,text=>status.Text=text);", ProductionSourceText.HandlerBody(code, "void FrameNavigationStarting("));
    }

    [Fact]
    public void ExternalLaunchIsCancelledBeforeTheWindowSaysWhy()
    {
        var calls = new List<string>();
        OwnedRobloxSignIn.RefuseExternalLaunch(() => calls.Add("cancel"), text => calls.Add(text));
        Assert.Equal(new[] { "cancel", OwnedRobloxSignIn.ExternalLaunchRefused }, calls);
        Assert.Contains("never opens Roblox or other apps", OwnedRobloxSignIn.ExternalLaunchRefused);
    }

    [Fact]
    public void LaunchAndFrameStayCancelledWhenTheStatusLineCannotBeUpdated()
    {
        bool launch = false, frame = false;
        Action<string> closedWindow = _ => throw new InvalidOperationException("synthetic: the status line is gone");
        Assert.Throws<InvalidOperationException>(() => OwnedRobloxSignIn.RefuseExternalLaunch(() => launch = true, closedWindow));
        Assert.Throws<InvalidOperationException>(() => OwnedRobloxSignIn.GuardFrameNavigation("roblox-player:1+launchmode:play", () => frame = true, closedWindow));
        Assert.True(launch, "A status line that could not be updated left the external launch uncancelled.");
        Assert.True(frame, "A status line that could not be updated left the roblox-player: frame uncancelled.");
    }

    // Only a frame that names an app's protocol is an app launch, so only it gets the launch message.
    [Theory]
    [InlineData("roblox-player:1+launchmode:play")]
    [InlineData("roblox:")]
    [InlineData("ms-settings:")]
    public void RefusedAppLaunchFrameIsCancelledBeforeTheWindowSaysWhy(string uri)
    {
        var calls = new List<string>();
        OwnedRobloxSignIn.GuardFrameNavigation(uri, () => calls.Add("cancel"), text => calls.Add(text));
        Assert.Equal(new[] { "cancel", OwnedRobloxSignIn.ExternalLaunchRefused }, calls);
    }

    // A blocked web page, local file, browser page or script frame opens no app, so the window does not say it refused one.
    [Theory]
    [InlineData("http://www.roblox.com/")]
    [InlineData("HTTP://www.roblox.com/")]
    [InlineData("file:///C:/Windows/test.html")]
    [InlineData("about:settings")]
    [InlineData("javascript:x")]
    [InlineData("JavaScript:x")]
    [InlineData("edge://settings")]
    [InlineData("not a uri")]
    [InlineData(null)]
    public void RefusedWebOrDocumentFrameIsCancelledBeforeTheWindowSaysItWasBlocked(string? uri)
    {
        var calls = new List<string>();
        OwnedRobloxSignIn.GuardFrameNavigation(uri, () => calls.Add("cancel"), text => calls.Add(text));
        Assert.Equal(new[] { "cancel", OwnedRobloxSignIn.FrameRefused }, calls);
        Assert.DoesNotContain("apps", OwnedRobloxSignIn.FrameRefused);
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html,synthetic")]
    [InlineData("https://www.roblox.com/games/1")]
    [InlineData("https://challenge.example.invalid/frame")]
    public void AllowedFrameIsNeitherCancelledNorReported(string uri)
    {
        var calls = new List<string>();
        OwnedRobloxSignIn.GuardFrameNavigation(uri, () => calls.Add("cancel"), text => calls.Add(text));
        Assert.Empty(calls);
    }

    [Fact]
    public void OwnedPrivateDirectoryRejectsAnActiveOwnerAndCanBeReusedAfterCleanup()
    {
        var id = Guid.NewGuid(); var first = SignInProfileDirectory.Create(_root, id, default);
        Assert.Throws<SignInDirectoryInUseException>(() => SignInProfileDirectory.Create(_root, id, default));
        Assert.StartsWith(Path.GetFullPath(_root), first.Path);
        Directory.CreateDirectory(Path.Combine(first.Path, "cache")); File.WriteAllText(Path.Combine(first.Path, "cache", "synthetic"), "test only");
        first.Remove(default); Assert.False(Directory.Exists(first.Path));
        var second = SignInProfileDirectory.Create(_root, id, default); Assert.NotEqual(first.Path, second.Path);
        second.Remove(default);
    }
    [Fact]
    public void RecoverableMarkedOldFolderIsCleanedBeforeAnotherIsAllocated()
    {
        var old = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default); old.ReleaseOwnership();
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(Directory.Exists(old.Path)); Assert.True(Directory.Exists(next.Path)); next.Remove(default);
    }
    [Fact]
    public void UnknownOldDirectoryBlocksCleanupAndIsPreserved()
    {
        var old = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default); old.Remove(default);
        string unknown=Path.Combine(_root,"signin-browser-data","unknown"); Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown,"keep.txt"),"keep");
        Assert.Throws<SignInDirectoryNeedsReviewException>(()=>SignInProfileDirectory.Create(_root,Guid.NewGuid(),default)); Assert.True(File.Exists(Path.Combine(unknown,"keep.txt")));
    }
    [Fact]
    public void FailedCleanupKeepsOwnershipMarkersAndRecoversAfterRelease()
    {
        var d = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        string nested = Path.Combine(d.Path, "EBWebView", "Default"); Directory.CreateDirectory(nested);
        string locked = Path.Combine(nested, "locked"); File.WriteAllText(locked, "test only");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => d.Remove(default));
            Assert.True(File.Exists(Path.Combine(d.Path, ".zenith-private-signin")), "A failed cleanup deleted the ownership marker.");
            Assert.True(File.Exists(Path.Combine(d.Path, "owner.lock")), "A failed cleanup deleted the owner lock.");
            Assert.Throws<SignInDirectoryInUseException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
        }
        d.ReleaseOwnership();
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(Directory.Exists(d.Path)); next.Remove(default);
    }
    // An owner closes its lock file before it can delete it, so for a moment its emptied folder looks like a leftover. An
    // admission that recovered it in that moment removed it under the owner, whose own deletes then failed: the sign-in
    // cleanup latched 'did not finish cleanup. Close Zenith Sessions' although nothing was left on disk.
    [Fact]
    public void AdmissionDuringAnOwnersFinalReleaseNeitherFailsThatCleanupNorFailsItself()
    {
        var owner = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Directory.CreateDirectory(Path.Combine(owner.Path, "EBWebView")); File.WriteAllText(Path.Combine(owner.Path, "EBWebView", "synthetic"), "test only");
        SignInProfileDirectory? admitted = null; Exception? admissionFailure = null;
        var admission = new Thread(() =>
        {
            try { admitted = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default); }
            catch (Exception ex) { admissionFailure = ex; }
        });
        owner.LeaseReleasedForTest = () =>
        {
            admission.Start();
            // Let the admission run until it finishes or waits for this owner to finish releasing its folder.
            SpinWait.SpinUntil(() => !admission.IsAlive || (admission.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10));
        };
        owner.Remove(default);
        Assert.True(admission.Join(TimeSpan.FromSeconds(10)), "The admission never finished after the owner released its folder.");
        Assert.Null(admissionFailure);
        Assert.False(Directory.Exists(owner.Path));
        Assert.NotNull(admitted); Assert.True(Directory.Exists(admitted!.Path));
        admitted.Remove(default);
    }
    [Fact]
    public void RecoveryAcceptsMarkedFolderWhoseLockIsMissing()
    {
        var old = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default); old.ReleaseOwnership();
        File.Delete(Path.Combine(old.Path, "owner.lock"));
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(Directory.Exists(old.Path)); next.Remove(default);
    }
    [Fact]
    public void RecoveryRemovesOwnedFoldersThatHoldNoBrowserDataButPreservesUnmarkedData()
    {
        string browserRoot = Path.Combine(_root, "signin-browser-data");
        string OwnedName() => Path.Combine(browserRoot, Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        string empty = OwnedName(); Directory.CreateDirectory(empty);
        string shortMarker = OwnedName(); Directory.CreateDirectory(shortMarker); File.WriteAllText(Path.Combine(shortMarker, ".zenith-private-signin"), "");
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(Directory.Exists(empty)); Assert.False(Directory.Exists(shortMarker)); next.Remove(default);
        string unmarked = OwnedName(); Directory.CreateDirectory(unmarked); File.WriteAllText(Path.Combine(unmarked, "keep.txt"), "keep");
        Assert.Throws<SignInDirectoryNeedsReviewException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
        Assert.True(File.Exists(Path.Combine(unmarked, "keep.txt")));
    }
    [Fact]
    public Task SignInCleanupRemovesFolderAfterConfirmedExitDespiteCookieFailureAndClearsLatchOnLateExit() => OnUi(async () =>
    {
        // The sign-in's own root holds unrecognized data, so an attempt admitted past the latch stops
        // at folder admission with that IOException instead of opening a browser.
        string admissionRoot = Path.Combine(_root, "admission");
        Directory.CreateDirectory(Path.Combine(admissionRoot, "signin-browser-data", "unknown"));
        await using var signIn = new OwnedRobloxSignIn(admissionRoot, Dispatcher.CurrentDispatcher);
        // (i) Cookie deletion failed, but the browser's exit was confirmed: removing the folder deletes those cookies too.
        var deleted = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.True(await signIn.SettleCleanupAsync(OwnedRobloxSignIn.FinishCleanupAsync(Task.CompletedTask, true, true, deleted, () => { }), deleted, TimeSpan.FromSeconds(5)),
            "A confirmed browser exit after a cookie-delete failure skipped folder removal.");
        Assert.False(Directory.Exists(deleted.Path)); Assert.False(signIn.CleanupPending);
        // (ii) The exit arrives after the observation window: refused while pending, admitted once that same task removes the folder.
        var late = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); bool detached = false;
        Assert.False(await signIn.SettleCleanupAsync(OwnedRobloxSignIn.FinishCleanupAsync(exit.Task, true, false, late, () => detached = true), late, TimeSpan.Zero));
        Assert.True(signIn.CleanupPending); Assert.True(Directory.Exists(late.Path)); Assert.False(detached);
        var pending = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
        Assert.Contains("still closing", pending.Message);
        exit.SetResult(); await signIn.CleanupSettled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(signIn.CleanupPending, "A late confirmed cleanup did not clear the sign-in latch.");
        Assert.False(Directory.Exists(late.Path)); Assert.True(detached);
        var admitted = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
        Assert.Contains("needs review", admitted.Message);
        // (iii) No exit was awaited after an earlier problem: the folder is preserved and the latch stays until restart.
        var kept = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(await signIn.SettleCleanupAsync(OwnedRobloxSignIn.FinishCleanupAsync(Task.CompletedTask, false, true, kept, () => { }), kept, TimeSpan.FromSeconds(5)));
        await signIn.CleanupSettled;
        Assert.True(signIn.CleanupPending); Assert.True(Directory.Exists(kept.Path));
        var failed = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
        Assert.Contains("Close Zenith Sessions", failed.Message);
    });
    // Owner-level tests: the real OwnedRobloxSignIn admits its folder, then waits behind a gated UI thread. Every
    // case ends before Show or WebView2 initialization, so no browser, network, account or runtime is needed.
    [Fact]
    public async Task SignInCancelledBeforeItsWindowOpensRemovesItsFolderAndLeavesNoLatch()
    {
        await using var ui = await GatedUiThread.StartAsync();
        await using var signIn = new OwnedRobloxSignIn(_root, ui.Dispatcher);
        using var stop = new CancellationTokenSource();
        var running = signIn.SignInAsync(Guid.NewGuid(), stop.Token);
        string folder = await GatedUiThread.WaitForOwnedFolderAsync(_root);
        var second = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
        Assert.Contains("already open", second.Message);
        stop.Cancel(); ui.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(Directory.Exists(folder), "A cancelled sign-in left its private folder behind.");
        Assert.False(signIn.CleanupPending);
        // Not latched: the next attempt reaches folder admission, where unrecognized data stops it before any browser.
        Directory.CreateDirectory(Path.Combine(_root, "signin-browser-data", "unknown"));
        var admitted = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
        Assert.Contains("needs review", admitted.Message);
    }
    [Fact]
    public async Task SignInWhoseFolderCannotBeRemovedRefusesTheNextAttemptAndKeepsTheFolderForRecovery()
    {
        await using var ui = await GatedUiThread.StartAsync();
        var signIn = new OwnedRobloxSignIn(_root, ui.Dispatcher);
        using var stop = new CancellationTokenSource();
        var running = signIn.SignInAsync(Guid.NewGuid(), stop.Token);
        string folder = await GatedUiThread.WaitForOwnedFolderAsync(_root);
        string cache = Directory.CreateDirectory(Path.Combine(folder, "cache")).FullName;
        using (new FileStream(Path.Combine(cache, "held"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            stop.Cancel(); ui.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
            await signIn.CleanupSettled.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(signIn.CleanupPending, "A failed folder cleanup did not latch the sign-in owner.");
            var refused = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
            Assert.Contains("did not finish cleanup", refused.Message);
            await signIn.DisposeAsync();
        }
        Assert.True(File.Exists(Path.Combine(folder, ".zenith-private-signin")), "The retained folder lost its ownership marker.");
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(Directory.Exists(folder), "The next admission did not recover the retained folder.");
        next.Remove(default);
    }
    [Fact]
    public async Task SignInWhoseUiThreadHasStoppedRemovesTheFolderItAdmitted()
    {
        var ui = await GatedUiThread.StartAsync(); await ui.DisposeAsync();
        await using var signIn = new OwnedRobloxSignIn(_root, ui.Dispatcher);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn.SignInAsync(Guid.NewGuid(), default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "signin-browser-data")));
        Assert.False(signIn.CleanupPending);
    }
    [Fact]
    public async Task SignInDeadlineIsReportedAsATimeoutAndRemovesItsFolder()
    {
        await using var ui = await GatedUiThread.StartAsync();
        var deadline = new ManualDeadline();
        await using var signIn = new OwnedRobloxSignIn(_root, ui.Dispatcher, deadline);
        var running = signIn.SignInAsync(Guid.NewGuid(), default);
        string folder = await GatedUiThread.WaitForOwnedFolderAsync(_root);
        Assert.Equal(TimeSpan.FromMinutes(15), deadline.Requested);
        await deadline.FireAsync(); ui.Release();
        // A plain cancellation here left the shell still asking the user to finish signing in after the window closed.
        var expired = await Assert.ThrowsAsync<AccountLinkException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("timed out after 15 minutes", expired.Message);
        Assert.False(Directory.Exists(folder), "An expired sign-in left its private folder behind.");
        Assert.False(signIn.CleanupPending);
    }
    // Folder admission stops a sign-in before any browser opens when a leftover folder is unrecognized or still held.
    // The raw IOException reached the shell's generic "check the data folder permissions" notice, which named
    // neither the folder nor the way to recover.
    [Fact]
    public Task ShellSignInBlockedByUnrecognizedFolderNamesThatFolderInsteadOfPermissions() => OnUi(async () =>
    {
        string unknown = Directory.CreateDirectory(Path.Combine(_root, "signin-browser-data", "unknown")).FullName;
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "keep");
        // No sign-in is injected: the shell owns the production OwnedRobloxSignIn, which stops at folder admission.
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), sessionClient: new Client());
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
        await shell.SaveAccountAsync(AccountProfile.Create("My test alias"));
        shell.SelectedAccount = Assert.Single(shell.Accounts);
        shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution;
        Assert.Contains("signin-browser-data", shell.ErrorNotice);
        Assert.Contains("Close Zenith Sessions", shell.ErrorNotice);
        Assert.DoesNotContain("permissions", shell.ErrorNotice);
        Assert.True(File.Exists(Path.Combine(unknown, "keep.txt")), "Unrecognized data was not preserved for review.");
        Assert.Null(Assert.Single(shell.Accounts).KnownUserId);
    });
    [Fact]
    public Task SignInBlockedByAnotherOwnersHeldFolderSaysItIsStillInUse() => OnUi(async () =>
    {
        // Stands in for a handoff check whose browser is still closing, or one that kept its folder after a cleanup problem.
        var held = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        try
        {
            await using var signIn = new OwnedRobloxSignIn(_root, Dispatcher.CurrentDispatcher);
            var refused = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
            Assert.Contains("still holds", refused.Message); Assert.Contains("signin-browser-data", refused.Message);
            Assert.Contains("close Zenith Sessions", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_root, refused.Message);
            Assert.True(Directory.Exists(held.Path), "Another owner's held sign-in folder was removed.");
            Assert.False(signIn.CleanupPending, "A refusal that created nothing latched the sign-in owner.");
        }
        finally { held.Remove(default); }
    });
    // A crash leaves its private sign-in folder behind. It was removed only when the next sign-in or handoff browser
    // was created, so its browser data stayed on disk until then. Startup now removes leftovers no browser holds and
    // keeps, untouched, any folder that is held, unrecognized, redirected or cannot be removed, and says so.
    [Fact]
    public Task StartupRemovesUnheldLeftoverSignInFoldersAndReportsTheOnesItKeeps() => OnUi(async () =>
    {
        string browserRoot = Directory.CreateDirectory(Path.Combine(_root, "signin-browser-data")).FullName;
        string crashed = Leftover(browserRoot, true), held = Leftover(browserRoot, true), lockedFile = Leftover(browserRoot, true), unmarked = Leftover(browserRoot, false);
        string outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName; File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        string redirected = Path.Combine(browserRoot, OwnedFolderName());
        await CreateJunctionAsync(redirected, outside);
        try
        {
            // Stands in for another owner's live lease, and for a browser file still open during removal.
            using (new FileStream(Path.Combine(held, "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (new FileStream(Path.Combine(lockedFile, "EBWebView", "Default", "synthetic"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), sessionClient: new Client());
                await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
                Assert.False(Directory.Exists(crashed), "Startup left an unheld leftover sign-in folder for the next sign-in to remove.");
                Assert.True(File.Exists(Path.Combine(held, "EBWebView", "Default", "synthetic")), "Startup removed a folder another owner still holds.");
                Assert.True(File.Exists(Path.Combine(lockedFile, ".zenith-private-signin")), "A folder startup could not remove lost its ownership marker.");
                Assert.True(File.Exists(Path.Combine(lockedFile, "owner.lock")), "A folder startup could not remove lost its owner lock.");
                Assert.True(File.Exists(Path.Combine(unmarked, "EBWebView", "Default", "synthetic")), "Startup removed an unrecognized folder.");
                Assert.True(Directory.Exists(redirected), "Startup removed a redirected folder.");
                Assert.True(File.Exists(Path.Combine(outside, "keep.txt")), "Startup followed a redirected folder.");
                Assert.Contains("4 leftover private sign-in folder(s) in signin-browser-data were kept", shell.CredentialCleanupStatus);
                Assert.DoesNotContain(_root, shell.CredentialCleanupStatus);
                Assert.Contains(shell.Events, line => line.Contains("Removed 1 leftover private sign-in folder", StringComparison.Ordinal));
                Assert.Contains(shell.Events, line => line.Contains("kept for review", StringComparison.Ordinal));
            }
        }
        finally { if (Directory.Exists(redirected)) Directory.Delete(redirected, false); }
    });
    [Fact]
    public Task StartupLeavesARedirectedSignInStorageRootUnchangedAndSaysSo() => OnUi(async () =>
    {
        string target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        string inside = Leftover(target, true);
        string browserRoot = Path.Combine(_root, "signin-browser-data");
        await CreateJunctionAsync(browserRoot, target);
        try
        {
            await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), sessionClient: new Client());
            await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
            Assert.True(File.Exists(Path.Combine(inside, "EBWebView", "Default", "synthetic")), "Startup followed a redirected signin-browser-data folder.");
            Assert.Contains("could not be checked at startup, so signin-browser-data was left unchanged", shell.CredentialCleanupStatus);
            Assert.DoesNotContain(_root, shell.CredentialCleanupStatus);
        }
        finally { if (Directory.Exists(browserRoot)) Directory.Delete(browserRoot, false); }
    });
    [Fact]
    public void StartupSweepCreatesNothingWhenNoSignInFolderExists()
    {
        Assert.Equal(default, SignInProfileDirectory.SweepOwned(_root, default));
        Assert.False(Directory.Exists(_root));
    }
    [Fact]
    public void StartupSweepOpensNoMoreFoldersThanAdmissionAndLeavesTheRestForTheNextRun()
    {
        string browserRoot = Path.Combine(_root, "signin-browser-data");
        for (int i = 0; i < 10; i++) Leftover(browserRoot, true);
        // The two folders past the budget were not opened: they are left for the next run, not kept for review.
        Assert.Equal(new SignInSweepResult(8, 0, 2), SignInProfileDirectory.SweepOwned(_root, default));
        Assert.Equal(2, Directory.EnumerateDirectories(browserRoot).Count());
        Assert.Equal(new SignInSweepResult(2, 0), SignInProfileDirectory.SweepOwned(_root, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(browserRoot));
    }
    [Fact]
    public void LeftoverKeptForReviewIsRememberedUntilAnAdmissionOrSweepFindsTheRootClean()
    {
        string browserRoot = Path.Combine(_root, "signin-browser-data");
        string held = Leftover(browserRoot, true);
        // A folder another owner still holds is busy, not a folder to review.
        using (new FileStream(Path.Combine(held, "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(new SignInSweepResult(0, 1), SignInProfileDirectory.SweepOwned(_root, default));
        Assert.False(SignInProfileDirectory.LeftoverNeedsReview(_root));
        string unmarked = Leftover(browserRoot, false);
        Assert.Equal(new SignInSweepResult(1, 1), SignInProfileDirectory.SweepOwned(_root, default));
        Assert.True(SignInProfileDirectory.LeftoverNeedsReview(_root));
        Assert.False(SignInProfileDirectory.LeftoverNeedsReview(_root + "-other"));
        // Admission reaches the same folder and keeps the reminder.
        Assert.Throws<SignInDirectoryNeedsReviewException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
        Assert.True(SignInProfileDirectory.LeftoverNeedsReview(_root));
        // Reviewed by hand while running: the next admission finds the root clean and clears it.
        Directory.Delete(unmarked, true);
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(SignInProfileDirectory.LeftoverNeedsReview(_root));
        next.Remove(default);
        // Admission alone also remembers a leftover it cannot recover, and a clean sweep clears it.
        unmarked = Leftover(browserRoot, false);
        Assert.Throws<SignInDirectoryNeedsReviewException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
        Assert.True(SignInProfileDirectory.LeftoverNeedsReview(_root));
        Directory.Delete(unmarked, true);
        Assert.Equal(default, SignInProfileDirectory.SweepOwned(_root, default));
        Assert.False(SignInProfileDirectory.LeftoverNeedsReview(_root));
    }
    [Fact]
    public void SweepRemembersReviewOnlyWhenMoreFoldersRemainThanOneAdmissionMayOpen()
    {
        string browserRoot = Path.Combine(_root, "signin-browser-data");
        for (int i = 0; i < 10; i++) Leftover(browserRoot, true);
        // The two folders left for the next run are recoverable and within one admission's budget.
        Assert.Equal(new SignInSweepResult(8, 0, 2), SignInProfileDirectory.SweepOwned(_root, default));
        Assert.False(SignInProfileDirectory.LeftoverNeedsReview(_root));
        for (int i = 0; i < 15; i++) Leftover(browserRoot, true);
        Assert.Equal(new SignInSweepResult(8, 0, 9), SignInProfileDirectory.SweepOwned(_root, default));
        Assert.True(SignInProfileDirectory.LeftoverNeedsReview(_root));
    }
    // What a sweep kept, left for later or could not check may stay on disk until a sweep or an admission finds the root
    // clean; the shell's startup note about those folders lasts only that long.
    [Fact]
    public void SweptLeftoversAreRememberedUntilASweepOrAnAdmissionFindsTheRootClean()
    {
        string browserRoot = Path.Combine(_root, "signin-browser-data");
        Assert.Equal(default, SignInProfileDirectory.SweepOwned(_root, default));
        Assert.False(SignInProfileDirectory.SweptLeftoversMayRemain(_root));
        string busy = Leftover(browserRoot, true);
        using (new FileStream(Path.Combine(busy, "EBWebView", "Default", "synthetic"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(new SignInSweepResult(0, 1), SignInProfileDirectory.SweepOwned(_root, default));
        Assert.True(SignInProfileDirectory.SweptLeftoversMayRemain(_root));
        Assert.False(SignInProfileDirectory.SweptLeftoversMayRemain(_root + "-other"));
        // An admission removes every leftover before it creates its own folder.
        SignInProfileDirectory.Create(_root, Guid.NewGuid(), default).Remove(default);
        Assert.False(Directory.Exists(busy));
        Assert.False(SignInProfileDirectory.SweptLeftoversMayRemain(_root));
        // Folders past the budget stay on disk too, until a sweep that leaves nothing behind.
        for (int i = 0; i < 9; i++) Leftover(browserRoot, true);
        Assert.Equal(new SignInSweepResult(8, 0, 1), SignInProfileDirectory.SweepOwned(_root, default));
        Assert.True(SignInProfileDirectory.SweptLeftoversMayRemain(_root));
        Assert.Equal(new SignInSweepResult(1, 0), SignInProfileDirectory.SweepOwned(_root, default));
        Assert.False(SignInProfileDirectory.SweptLeftoversMayRemain(_root));
    }
    // Folders past one sweep's budget are not opened, and the next sign-in or start removes them. They were described as
    // kept because they were in use, not recognized or could not be removed, and recorded as a sign-in failure.
    [Fact]
    public Task StartupLeavesFoldersPastItsBudgetForTheNextRunWithoutReportingAFailure() => OnUi(async () =>
    {
        string browserRoot = Path.Combine(_root, "signin-browser-data");
        for (int i = 0; i < 10; i++) Leftover(browserRoot, true);
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), sessionClient: new Client());
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
        Assert.Equal(2, Directory.EnumerateDirectories(browserRoot).Count());
        Assert.DoesNotContain("signin-browser-data", shell.CredentialCleanupStatus);
        Assert.Contains(shell.Events, line => line.Contains("Removed 8 leftover private sign-in folder", StringComparison.Ordinal));
        Assert.Contains(shell.Events, line => line.Contains("2 more leftover private sign-in folder(s) were left for the next sign-in or start", StringComparison.Ordinal));
        Assert.DoesNotContain(shell.Events, line => line.Contains("kept for review", StringComparison.Ordinal));
        shell.RefreshDiagnostics.Execute(null);
        Assert.DoesNotContain(shell.DiagnosticRows, row => row.Event.Kind == ManagerEventKind.OperationFailed && row.Event.Subsystem == ManagerSubsystem.SignIn);
    });
    // The startup note about kept folders was added to every later credential-cleanup status, so it stayed after a sign-in's
    // admission had removed those folders, and each Retry credential cleanup showed it again.
    [Fact]
    public Task StartupNoteAboutKeptSignInFoldersEndsOnceAnAdmissionRemovesThem() => OnUi(async () =>
    {
        string locked = Leftover(Path.Combine(_root, "signin-browser-data"), true);
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), sessionClient: new Client());
        // Stands in for a browser file another program still had open at startup.
        using (new FileStream(Path.Combine(locked, "EBWebView", "Default", "synthetic"), FileMode.Open, FileAccess.Read, FileShare.None))
        { await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice); }
        const string note = "1 leftover private sign-in folder(s) in signin-browser-data were kept";
        Assert.Contains(note, shell.CredentialCleanupStatus);
        // Nothing has removed the folder yet, so retrying still says so.
        shell.CleanupCredentials.Execute(null); await shell.CleanupCredentials.Execution;
        Assert.Contains(note, shell.CredentialCleanupStatus);
        Assert.True(Directory.Exists(locked));
        // A sign-in's admission removes every leftover before it opens its browser.
        SignInProfileDirectory.Create(_root, Guid.NewGuid(), default).Remove(default);
        Assert.False(Directory.Exists(locked));
        shell.CleanupCredentials.Execute(null); await shell.CleanupCredentials.Execution;
        Assert.Equal("No pending credential cleanup.", shell.CredentialCleanupStatus);
    });
    // Only leftover data that admission must not open is remembered for review. A leftover file another program (an
    // antivirus scan, or a browser a crash left running) still had open was remembered too, so every handoff check said
    // cleanup failed and native launch stopped until restart, although the next attempt could succeed.
    [Fact]
    public void OnlyLeftoverDataAdmissionMustNotOpenIsRememberedForReview()
    {
        string browserRoot = Path.Combine(_root, "signin-browser-data");
        string foreign = Directory.CreateDirectory(Path.Combine(browserRoot, "not-ours")).FullName;
        Assert.Throws<SignInDirectoryNeedsReviewException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
        Directory.Delete(foreign);
        string unmarked = Leftover(browserRoot, false);
        Assert.Throws<SignInDirectoryNeedsReviewException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
        Directory.Delete(unmarked, true);
        for (int i = 0; i < 9; i++) Leftover(browserRoot, true);
        Assert.Throws<SignInDirectoryNeedsReviewException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
        Assert.True(SignInProfileDirectory.LeftoverNeedsReview(_root));
        // The folder past the budget is recoverable: the next clean run removes it and clears the reminder.
        Assert.Equal(new SignInSweepResult(1, 0), SignInProfileDirectory.SweepOwned(_root, default));
        Assert.False(SignInProfileDirectory.LeftoverNeedsReview(_root));
        string busy = Leftover(browserRoot, true);
        using (new FileStream(Path.Combine(busy, "EBWebView", "Default", "synthetic"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var refused = Assert.ThrowsAny<IOException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
            Assert.IsNotType<SignInDirectoryNeedsReviewException>(refused); Assert.IsNotType<SignInDirectoryInUseException>(refused);
            Assert.False(SignInProfileDirectory.LeftoverNeedsReview(_root), "Admission remembered a file still open as data to review.");
            Assert.Equal(new SignInSweepResult(0, 1), SignInProfileDirectory.SweepOwned(_root, default));
            Assert.False(SignInProfileDirectory.LeftoverNeedsReview(_root), "The sweep remembered a file still open as data to review.");
            Assert.True(File.Exists(Path.Combine(busy, ".zenith-private-signin")), "A folder cleanup could not finish lost its ownership marker.");
        }
        // Once the other program lets go, the next admission removes the leftover.
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(Directory.Exists(busy));
        next.Remove(default);
    }
    // A remote, redirected or unwritable data folder, or a leftover file another program still has open, says nothing
    // about leftover folders to review. Each said "a leftover folder ... was not recognized or could not be removed" and
    // was remembered for review, and a real permissions problem lost the advice it needed.
    [Fact]
    public Task SignInBlockedByStorageRatherThanLeftoverDataAsksForUsableStorageAndLatchesNothing() => OnUi(async () =>
    {
        string real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName, alias = Path.Combine(_root, "alias");
        await CreateJunctionAsync(alias, real);
        var denied = Directory.CreateDirectory(Path.Combine(_root, "denied"));
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateDirectories, AccessControlType.Deny);
        var acl = denied.GetAccessControl(); acl.AddAccessRule(deny); denied.SetAccessControl(acl);
        string busyRoot = Path.Combine(_root, "busy"), busy = Leftover(Path.Combine(busyRoot, "signin-browser-data"), true);
        try
        {
            using (new FileStream(Path.Combine(busy, "EBWebView", "Default", "synthetic"), FileMode.Open, FileAccess.Read, FileShare.None))
                foreach (string dataRoot in new[] { @"\\zenith-test.invalid\share\data", Path.Combine(alias, "data"), denied.FullName, busyRoot })
                {
                    await using var signIn = new OwnedRobloxSignIn(dataRoot, Dispatcher.CurrentDispatcher);
                    var refused = await Assert.ThrowsAsync<AccountLinkException>(() => signIn.SignInAsync(Guid.NewGuid(), default));
                    Assert.Contains("storage could not be prepared", refused.Message);
                    Assert.Contains("permission", refused.Message);
                    Assert.DoesNotContain("leftover", refused.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(_root, refused.Message);
                    Assert.False(signIn.CleanupPending, "A refusal that created nothing latched the sign-in owner.");
                    Assert.False(SignInProfileDirectory.LeftoverNeedsReview(dataRoot), "A storage failure was remembered as leftover data to review.");
                }
            Assert.Empty(Directory.EnumerateFileSystemEntries(real));
            Assert.False(Directory.Exists(Path.Combine(denied.FullName, "signin-browser-data")));
        }
        finally
        {
            acl.RemoveAccessRuleSpecific(deny); denied.SetAccessControl(acl);
            if (Directory.Exists(alias)) Directory.Delete(alias, false);
        }
    });
    private static string OwnedFolderName() => Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N");
    /// <summary>A folder shaped like one a crashed sign-in left: owned name, optional marker and lock, synthetic browser data.</summary>
    private static string Leftover(string browserRoot, bool marked)
    {
        string folder = Directory.CreateDirectory(Path.Combine(browserRoot, OwnedFolderName())).FullName;
        if (marked)
        {
            File.WriteAllText(Path.Combine(folder, ".zenith-private-signin"), "Zenith Sessions private sign-in profile v1");
            File.WriteAllText(Path.Combine(folder, "owner.lock"), "");
        }
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "EBWebView", "Default")).FullName, "synthetic"), "test only");
        return folder;
    }
    // A directory junction exercises real redirection without symbolic-link privilege or developer mode.
    private static async Task CreateJunctionAsync(string alias, string target)
    {
        static string Quote(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";
        string script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(alias) + " -Target " + Quote(target) + " | Out-Null";
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var process = System.Diagnostics.Process.Start(start)!; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); Assert.Equal(0, process.ExitCode); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); } }
        Assert.True((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0, "The test junction was not created.");
    }
    [Fact]
    public void CancelledDirectoryAdmissionDoesNotCreatePrivateData()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), stop.Token));
        Assert.False(Directory.Exists(_root));
    }
    [Fact]
    public Task ShellSavedSignInUsesReturnedRobloxIdentityWithoutOAuthSetup() => OnUi(async () =>
    {
        var signin = new SignIn(); var client = new Client();
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), signIn: signin, sessionClient: client);
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
        await shell.SaveAccountAsync(AccountProfile.Create("My test alias") with { Notes = "Keep this note" });
        shell.SelectedAccount = Assert.Single(shell.Accounts);
        Assert.Equal("", shell.OAuthClientId);
        shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution;
        var linked = Assert.Single(shell.Accounts);
        Assert.Equal("verified_player", linked.Label); Assert.Equal("@verified_player", linked.Username);
        Assert.Equal(456, linked.KnownUserId); Assert.Equal("My test alias", linked.Alias);
        Assert.Equal(1, signin.Calls); Assert.Equal(1, client.Calls); Assert.Contains("Saved sign-in verified", shell.Notice);
        Assert.DoesNotContain("synthetic-private-session", shell.Notice); Assert.DoesNotContain("synthetic-private-session", linked.LinkedAccount);
        using var vault = new ProtectedCredentialVault(_root);
        var saved = (await vault.ReadAsync(linked.Profile.Id, default))!;
        Assert.Equal(RobloxSessionIdentity.Provider, saved.Provider); Assert.Equal("synthetic-private-session", saved.SessionCookie);
    });
    [Fact]
    public Task ShellLinkReportsProviderCancellationInsteadOfStalePrompt() => OnUi(async () =>
    {
        // The sign-in ended on its own side (for example its UI thread stopped) while the user's stop was never used.
        // The link command discards cancellations, so the shell kept asking the user to finish in a window that had closed.
        var signin = new ProviderCancelledSignIn();
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), signIn: signin, sessionClient: new Client());
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
        await shell.SaveAccountAsync(AccountProfile.Create("My test alias"));
        shell.SelectedAccount = Assert.Single(shell.Accounts);
        shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution;
        Assert.Equal(1, signin.Calls);
        Assert.DoesNotContain("Complete sign-in on Roblox", shell.Notice);
        Assert.Contains("Roblox sign-in ended before a session was saved", shell.Notice);
        Assert.Contains("Existing profiles were preserved", shell.Notice);
        Assert.Contains(shell.Events, line => line.Contains("Roblox sign-in ended before a session was saved", StringComparison.Ordinal));
        Assert.False(shell.IsAuthorizing);
        Assert.Null(Assert.Single(shell.Accounts).KnownUserId);
    });
    [Fact]
    public Task ShellSavedSignInCheckReportsProviderCancellationInsteadOfStalePrompt() => OnUi(async () =>
    {
        var client = new CancelsAfterFirstClient();
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), signIn: new SignIn(), sessionClient: client);
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
        await shell.SaveAccountAsync(AccountProfile.Create("My test alias"));
        shell.SelectedAccount = Assert.Single(shell.Accounts);
        shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution;
        Assert.Contains("Saved sign-in verified", shell.Notice);
        shell.SelectedAccount = Assert.Single(shell.Accounts);
        shell.RefreshAuthorization.Execute(null); await shell.RefreshAuthorization.Execution;
        Assert.Equal(2, client.Calls);
        Assert.DoesNotContain("Checking the selected saved Roblox sign-in", shell.Notice);
        Assert.Contains("The saved sign-in check ended before it finished", shell.Notice);
        Assert.Contains(shell.Events, line => line.Contains("Saved sign-in check ended before it finished", StringComparison.Ordinal));
        Assert.False(shell.IsAuthorizing);
    });
    [Fact]
    public void ProductionSettingsNoLongerRequestsOAuthRegistration()
    {
        using var stream = typeof(RobloxSignInPresentationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        using var reader = new StreamReader(stream); string xaml = reader.ReadToEnd();
        Assert.DoesNotContain("Binding OAuthClientId", xaml); Assert.DoesNotContain("Binding CallbackPort", xaml);
        Assert.Contains("Saved Roblox sign-ins", xaml); Assert.Contains("Check saved sign-in", xaml);
    }
    private sealed class SignIn : IRobloxSessionSignIn
    {
        public int Calls;
        public Task<RobloxSessionCredential> SignInAsync(Guid id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new RobloxSessionCredential("synthetic-private-session")); }
    }
    private sealed class ProviderCancelledSignIn : IRobloxSessionSignIn
    {
        public int Calls;
        public Task<RobloxSessionCredential> SignInAsync(Guid id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromException<RobloxSessionCredential>(new OperationCanceledException()); }
    }
    private sealed class Client : IRobloxSessionClient
    {
        public int Calls;
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new RobloxSessionValidation(new(456, "verified_player", RobloxSessionIdentity.Issuer, "456"))); }
    }
    /// <summary>Verifies the first sign-in, then ends the next check with a cancellation nobody requested.</summary>
    private sealed class CancelsAfterFirstClient : IRobloxSessionClient
    {
        public int Calls;
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ++Calls == 1 ? Task.FromResult(new RobloxSessionValidation(new(456, "verified_player", RobloxSessionIdentity.Issuer, "456")))
                : Task.FromException<RobloxSessionValidation>(new OperationCanceledException());
        }
    }
    private sealed class Monitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) => Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32*CapacityPolicy.GiB, 20*CapacityPolicy.GiB, 64*CapacityPolicy.GiB, 20*CapacityPolicy.GiB, 10), []));
        public Task<bool> FocusAsync(SessionObservation s, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation s, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
    }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } finally { dispatcher.InvokeShutdown(); } });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
