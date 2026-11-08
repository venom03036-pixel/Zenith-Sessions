using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>One visible, app-owned private Roblox sign-in. Only an explicit Save reads its own cookie manager.</summary>
public sealed class OwnedRobloxSignIn(string dataRoot, Dispatcher dispatcher) : IRobloxSessionSignIn, IAsyncDisposable
{
    private static readonly TimeSpan CleanupObservation = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SignInDeadline = TimeSpan.FromMinutes(15);
    private const string ExternalLaunchRefused = "This sign-in window never opens Roblox or other apps. Save the sign-in, then launch from Zenith.";
    private const string FolderInUse = "Another private browser still holds its folder in signin-browser-data. Finish or cancel that window, then sign in again; if this repeats, close Zenith Sessions and retry. Nothing was saved.";
    private const string FolderKept = "A previous private browser's cleanup failed, and its folder in signin-browser-data is kept until Zenith Sessions closes, so no sign-in can start before then. Close Zenith Sessions and retry; if this repeats, review signin-browser-data in the Zenith data folder. Nothing was saved.";
    private const string FolderNeedsReview = "Private sign-in storage needs review: a leftover folder in signin-browser-data was not recognized or could not be removed, and it was preserved. Close Zenith Sessions and retry; if this repeats, review signin-browser-data in the Zenith data folder. Nothing was saved.";
    private const string StorageUnavailable = "Private sign-in storage could not be prepared. The Zenith data folder must be on a local drive that is not redirected, with free space and write permission for your Windows account, and no other program may be using signin-browser-data. Fix that, then sign in again. Nothing was saved.";
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly object _state = new();
    private int _active;
    // Guarded by _state. Set while a sign-in folder is not confirmed removed. A cleanup still waiting
    // for its browser to exit clears it when that same tracked task removes the folder; a failed
    // cleanup keeps it, and the folder's lease, until disposal.
    private bool _cleanupFailed;
    private SignInProfileDirectory? _incompleteDirectory;
    private Task _cleanup = Task.CompletedTask;

    /// <summary>Tests fire the unchanged 15-minute deadline through <paramref name="time"/> instead of waiting for it.</summary>
    internal OwnedRobloxSignIn(string dataRoot, Dispatcher dispatcher, TimeProvider time) : this(dataRoot, dispatcher) => _time = time;

    public async Task<RobloxSessionCredential> SignInAsync(Guid profileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new AccountLinkException("A Roblox sign-in window is already open.");
        try
        {
            lock (_state)
                if (_cleanupFailed) throw new AccountLinkException(_cleanup.IsCompleted
                    ? "The previous sign-in browser did not finish cleanup. Close Zenith Sessions before trying again; its private data was preserved for cleanup."
                    : "The previous sign-in browser is still closing. Try again in a few seconds; its private data is removed once it closes.");
            using var deadline = new CancellationTokenSource(SignInDeadline, _time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            try
            {
                SignInProfileDirectory directory;
                // Admission refused before anything was created or opened, so nothing is latched. Fixed text only: no path or
                // inner exception. Only leftover data kept for review names that folder; any other storage failure (remote,
                // redirected, unwritable or full storage, or a file another program has open) asks for usable storage. A folder
                // another owner kept after its cleanup failed is held until Zenith closes, so it asks for a restart, not a wait.
                try { directory = await Task.Run(() => SignInProfileDirectory.Create(dataRoot, profileId, timeout.Token), timeout.Token).ConfigureAwait(false); }
                catch (SignInDirectoryInUseException ex) when (ex.KeptAfterFailedCleanup) { throw new AccountLinkException(FolderKept); }
                catch (SignInDirectoryInUseException) { throw new AccountLinkException(FolderInUse); }
                catch (SignInDirectoryNeedsReviewException) { throw new AccountLinkException(FolderNeedsReview); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new AccountLinkException(StorageUnavailable); }
                bool started = false;
                try { return await dispatcher.InvokeAsync(() => RunAsync(directory, timeout.Token, () => started = true), DispatcherPriority.Normal, CancellationToken.None).Task.Unwrap().ConfigureAwait(false); }
                finally
                {
                    if (!started)
                    {
                        try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await Task.Run(() => directory.Remove(cleanup.Token)).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                        { directory.KeepAfterFailedCleanup(); lock (_state) { _cleanupFailed = true; _incompleteDirectory = directory; } }
                    }
                }
            }
            // Only the deadline, not the caller, ended this sign-in. A bare cancellation closed the window but left the
            // shell's link prompt asking the user to finish signing in there (or reported a cancel nobody chose).
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            { throw new AccountLinkException("Roblox sign-in timed out after 15 minutes. Nothing was saved; existing saved accounts were preserved. Start sign-in again when you are ready."); }
        }
        finally { Volatile.Write(ref _active, 0); }
    }

    /// <summary>Calls <paramref name="ownCleanup"/> once its window and handlers exist; from then on its own finally,
    /// not <see cref="SignInAsync"/>, removes the folder.</summary>
    private async Task<RobloxSessionCredential> RunAsync(SignInProfileDirectory directory, CancellationToken ct, Action ownCleanup)
    {
        var browser = new WebView2();
        var status = new TextBlock { Text = "Opening Roblox’s own sign-in page…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var save = new Button { Content = "Save this Roblox sign-in", MinHeight = 36, Padding = new Thickness(16, 4, 16, 4), IsEnabled = false };
        var cancel = new Button { Content = "Cancel", MinHeight = 36, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 4, 16, 4) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(save); actions.Children.Add(cancel);
        var footer = new StackPanel { Margin = new Thickness(16) }; footer.Children.Add(actions); footer.Children.Add(status);
        var address = new TextBlock { Text = "https://www.roblox.com/login · private sign-in for this account", Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
        var content = new DockPanel(); DockPanel.SetDock(address, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(address); content.Children.Add(footer); content.Children.Add(browser);
        var window = new Window { Title = "Sign in with Roblox — Zenith Sessions", Width = 700, Height = 820, MinWidth = 480, MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content, Background = Brushes.White, Foreground = Brushes.Black };
        window.SetResourceReference(Window.BackgroundProperty, "CardBackground");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimary");
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        save.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        if (Application.Current?.MainWindow is { IsVisible: true } owner) window.Owner = owner;
        var completion = new TaskCompletionSource<RobloxSessionCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CoreWebView2Environment? environment = null;
        CoreWebView2? core = null;
        Task? saveTask = null;
        bool closing = false, initializationStarted = false, controllerReady = false, externalInstalled = false, framesInstalled = false, navigationInstalled = false;
        EventHandler<CoreWebView2BrowserProcessExitedEventArgs> exited = (_, _) => browserExited.TrySetResult();
        void HidePending() { if (!closing) { window.IsEnabled = false; window.Hide(); } }
        CancelEventHandler close = (_, e) => { if (!closing) { e.Cancel = true; HidePending(); completion.TrySetCanceled(ct.IsCancellationRequested ? ct : new CancellationToken(true)); } };
        RoutedEventHandler saveClick = (_, _) =>
        {
            if (saveTask is { IsCompleted: false } || completion.Task.IsCompleted || !controllerReady) return;
            saveTask = ReadExplicitlyAsync();
        };
        RoutedEventHandler cancelClick = (_, _) => { HidePending(); completion.TrySetCanceled(new CancellationToken(true)); };
        window.Closing += close; save.Click += saveClick; cancel.Click += cancelClick;
        using var registration = ct.Register(() => { completion.TrySetCanceled(ct); dispatcher.BeginInvoke(HidePending, DispatcherPriority.Send); });
        RobloxSessionCredential? accepted = null;
        bool cleaned = false;
        ownCleanup();
        try
        {
            ct.ThrowIfCancellationRequested();
            window.Show();
            // Environment overrides can redirect a custom profile or enable a debugging endpoint.
            // Fail closed rather than mutate process-wide settings or attach to another browser.
            if (Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(key => key.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)))
                throw new AccountLinkException("WebView2 environment overrides are active. Start Zenith Sessions without browser overrides before saving a private sign-in.");
            // Do not attach cancellation to COM initialization and then abandon it. Keep ownership
            // until the actual initialization task finishes, including cancellation during startup.
            environment = await CoreWebView2Environment.CreateAsync(null, directory.Path, new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false });
            environment.BrowserProcessExited += exited;
            if (!string.Equals(Path.GetFullPath(environment.UserDataFolder), Path.GetFullPath(directory.Path), StringComparison.OrdinalIgnoreCase))
                throw new AccountLinkException("WebView2 redirected the private sign-in folder. No browser sign-in was opened or saved.");
            ct.ThrowIfCancellationRequested();
            var options = environment.CreateCoreWebView2ControllerOptions(); options.IsInPrivateModeEnabled = true;
            options.ProfileName = "ZenithSignIn";
            initializationStarted = true;
            await browser.EnsureCoreWebView2Async(environment, options);
            controllerReady = true;
            // Captured once: after a browser crash the control's getter throws, and teardown must not depend on it.
            core = browser.CoreWebView2;
            ct.ThrowIfCancellationRequested();
            // Install the launch guards before any page opens. Roblox's Play button starts roblox-player: from a hidden
            // child frame that NavigationStarting never sees. A runtime that cannot install them fails closed below.
            core.LaunchingExternalUriScheme += ExternalScheme; externalInstalled = true;
            core.FrameNavigationStarting += FrameNavigationStarting; framesInstalled = true;
            core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = true;
            core.NavigationStarting += NavigationStarting;
            core.NewWindowRequested += NewWindowRequested;
            core.DownloadStarting += DownloadStarting;
            core.PermissionRequested += PermissionRequested;
            core.ProcessFailed += ProcessFailed; navigationInstalled = true;
            core.Navigate("https://www.roblox.com/login");
            save.IsEnabled = true;
            status.Text = "Complete sign-in on Roblox, including any security challenge. Then choose Save this Roblox sign-in. Zenith saves the session with Windows protection, never your password.";
            accepted = await completion.Task;
        }
        catch (WebView2RuntimeNotFoundException)
        { throw new AccountLinkException("Microsoft Edge WebView2 Runtime is required for Roblox sign-in. Install Microsoft’s Evergreen Runtime, then try again. Your saved accounts were not changed."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AccountLinkException("Roblox sign-in was cancelled. Existing saved accounts were preserved."); }
        catch (Exception ex) when (ex is not OperationCanceledException and not AccountLinkException)
        { throw new AccountLinkException("The private Roblox sign-in window could not complete. Check WebView2 Runtime and local storage access, then try again. Existing saved accounts were preserved."); }
        finally
        {
            HidePending();
            save.IsEnabled = false;
            if (saveTask is not null) await saveTask;
            // The cleanup stays tracked past this observation: a late browser exit still removes the folder and clears the latch.
            // Every guard stays installed through stop, cookie deletion and controller shutdown, until the browser's exit.
            var live = core; var initialized = initializationStarted ? environment : null;
            cleaned = await SettleCleanupAsync(TeardownAsync(
                live is null ? null : () => { live.Stop(); live.CookieManager.DeleteAllCookies(); },
                browser.Dispose,
                () => { closing = true; window.Closing -= close; save.Click -= saveClick; cancel.Click -= cancelClick; window.Close(); },
                controllerReady, initialized is null ? null : () => initialized.GetProcessInfos().Count, browserExited.Task, directory,
                () => { DetachGuards(); if (environment is not null) environment.BrowserProcessExited -= exited; }), directory, CleanupObservation);
            if (!cleaned) accepted?.Dispose();
            if (completion.Task.IsCompletedSuccessfully && !ReferenceEquals(accepted, completion.Task.Result)) completion.Task.Result.Dispose();
        }
        if (!cleaned) throw new AccountLinkException(CleanupFailedUntilRestart
            ? "The private sign-in browser did not finish cleanup. This sign-in was not saved. Close Zenith Sessions before trying again; private files were preserved for cleanup."
            : "The private sign-in browser is still closing. This sign-in was not saved. Try again in a few seconds; its private data is removed once it closes.");
        return accepted ?? throw new AccountLinkException("No Roblox sign-in was selected.");

        async Task ReadExplicitlyAsync()
        {
            save.IsEnabled = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!RobloxSignInNavigation.IsAllowed(browser.Source?.AbsoluteUri)) throw new AccountLinkException("Return to Roblox’s own sign-in page before saving.");
                var credential = await OwnedRobloxSessionCookie.ReadAsync(browser.CoreWebView2.CookieManager, ct);
                if (!completion.TrySetResult(credential)) credential.Dispose();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception ex) when (ex is ArgumentException or AccountLinkException)
            { status.Text = ex is AccountLinkException ? ex.Message : "Roblox returned an invalid session. Sign in again."; }
            catch (Exception) { status.Text = "The private sign-in could not be read. Retry sign-in; no saved account was changed."; }
            finally { save.IsEnabled = !completion.Task.IsCompleted && !ct.IsCancellationRequested; }
        }
        void NavigationStarting(object? _, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!RobloxSignInNavigation.IsAllowed(e.Uri)) { e.Cancel = true; status.Text = "This window stays on Roblox’s own HTTPS pages. Use a Roblox passkey, password, or supported sign-in option on this page."; }
        }
        void ExternalScheme(object? _, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
        {
            e.Cancel = true; // First and unconditional: this window never forwards a launch to Roblox or any other app.
            status.Text = ExternalLaunchRefused;
        }
        void FrameNavigationStarting(object? _, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!RobloxSignInNavigation.IsAllowedFrame(e.Uri)) { e.Cancel = true; status.Text = ExternalLaunchRefused; }
        }
        void DetachGuards()
        {
            if (core is null) return;
            try
            {
                if (externalInstalled) core.LaunchingExternalUriScheme -= ExternalScheme;
                if (framesInstalled) core.FrameNavigationStarting -= FrameNavigationStarting;
                if (navigationInstalled)
                {
                    core.NavigationStarting -= NavigationStarting; core.NewWindowRequested -= NewWindowRequested;
                    core.DownloadStarting -= DownloadStarting; core.PermissionRequested -= PermissionRequested; core.ProcessFailed -= ProcessFailed;
                }
            }
            // The browser's exit is already confirmed: a closed controller may reject removal, but nothing live is left unguarded.
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }
        void NewWindowRequested(object? _, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; status.Text = "Pop-up navigation is disabled in this private sign-in. Use an option on Roblox’s page."; }
        void DownloadStarting(object? _, CoreWebView2DownloadStartingEventArgs e) { e.Cancel = true; }
        void PermissionRequested(object? _, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; }
        void ProcessFailed(object? _, CoreWebView2ProcessFailedEventArgs e)
        {
            // A helper or subframe process WebView2 restarts by itself keeps the sign-in, its challenge progress and its guards.
            if (!WebView2ProcessFailurePolicy.EndsSession(e.ProcessFailedKind)) { status.Text = WebView2ProcessFailurePolicy.RecoveredNotice; return; }
            completion.TrySetException(new AccountLinkException("The private Roblox browser stopped. Retry sign-in; existing saved accounts were preserved."));
        }
    }
    internal bool CleanupPending { get { lock (_state) return _cleanupFailed; } }
    internal Task CleanupSettled { get { lock (_state) return _cleanup; } }
    private bool CleanupFailedUntilRestart { get { lock (_state) return _cleanupFailed && _cleanup.IsCompleted; } }

    /// <summary>Tears one sign-in browser down in the handoff probe's order. After a browser crash the control and its
    /// closed controller can reject every native step, so each failure is recorded and the later steps still run: the
    /// window always closes and the folder's lease always reaches the tracked cleanup. Initialization that failed after
    /// it started may have left a browser using the folder, so its exit is awaited too. The guards stay installed
    /// until that exit is confirmed; a confirmed exit removes the folder despite an earlier problem.</summary>
    internal static async Task TeardownAsync(Action? stopAndClearCookies, Action dispose, Action close, bool controllerReady,
        Func<int>? runningBrowserProcesses, Task browserExited, SignInProfileDirectory directory, Action detachGuards)
    {
        bool problem = false;
        try { stopAndClearCookies?.Invoke(); } catch (Exception) { problem = true; }
        try { dispose(); } catch (Exception) { problem = true; }
        try { close(); } catch (Exception) { problem = true; }
        bool awaitBrowserExit = controllerReady;
        if (!awaitBrowserExit && runningBrowserProcesses is not null)
            try { awaitBrowserExit = runningBrowserProcesses() != 0; }
            // Unknown is treated as running: removing a folder a live browser still uses deletes some files and fails on others.
            catch (Exception) { awaitBrowserExit = true; problem = true; }
        await FinishCleanupAsync(browserExited, awaitBrowserExit, problem, directory, detachGuards);
    }

    /// <summary>Removes the private folder after the browser's real exit, even when cookie deletion failed:
    /// removing the folder deletes those cookies too. Without an awaited exit, an earlier problem preserves it.
    /// A failure, even one that outlives the observation, keeps the folder and its lease until disposal, so it is marked
    /// kept: other owners then report a cleanup that needs a restart instead of a browser that is still closing.</summary>
    internal static async Task FinishCleanupAsync(Task browserExited, bool awaitBrowserExit, bool priorProblem, SignInProfileDirectory directory, Action detach)
    {
        try
        {
            if (awaitBrowserExit) await browserExited;
            if (priorProblem && !awaitBrowserExit) throw new IOException("Private sign-in cleanup needs review; its files were preserved.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.Run(() => directory.Remove(deadline.Token));
        }
        catch (Exception) { directory.KeepAfterFailedCleanup(); throw; }
        finally { detach(); }
    }

    /// <summary>Watches a tracked folder cleanup for a bounded time. A cleanup still waiting for its browser stays
    /// owned past the observation, and the latch clears only when that same task later removes the folder.</summary>
    internal async Task<bool> SettleCleanupAsync(Task cleanup, SignInProfileDirectory directory, TimeSpan observation)
    {
        var tracked = ClearLatchWhenRemovedAsync(cleanup, directory);
        lock (_state) _cleanup = tracked;
        try { await cleanup.WaitAsync(observation); return true; }
        catch (Exception)
        {
            // A timeout or failed removal keeps this folder's lease and refuses new sign-ins until the folder is gone.
            lock (_state)
            {
                if (cleanup.IsCompletedSuccessfully) return true;
                _cleanupFailed = true; _incompleteDirectory = directory;
            }
            return false;
        }
    }
    private async Task ClearLatchWhenRemovedAsync(Task cleanup, SignInProfileDirectory directory)
    {
        // A failed cleanup stays latched; SettleCleanupAsync observed and reported that same failure.
        await cleanup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!cleanup.IsCompletedSuccessfully) return;
        lock (_state)
            if (ReferenceEquals(_incompleteDirectory, directory)) { _incompleteDirectory = null; _cleanupFailed = false; }
    }

    /// <summary>Joins the tracked cleanup before releasing a retained lease. Remove disposes the lease itself, and
    /// releasing it mid-removal would let another owner recover a folder that is still being deleted.</summary>
    public async ValueTask DisposeAsync()
    {
        Task cleanup; lock (_state) cleanup = _cleanup;
        await cleanup.ConfigureAwait(false);
        SignInProfileDirectory? retained; lock (_state) { retained = _incompleteDirectory; _incompleteDirectory = null; }
        retained?.ReleaseOwnership();
    }
}
