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
    private int _active;
    private bool _cleanupFailed;
    private SignInProfileDirectory? _incompleteDirectory;

    public async Task<RobloxSessionCredential> SignInAsync(Guid profileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new AccountLinkException("A Roblox sign-in window is already open.");
        try
        {
            if (_cleanupFailed) throw new AccountLinkException("The previous sign-in browser did not finish cleanup. Close Zenith Sessions before trying again; its private data was preserved for cleanup.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var directory = await Task.Run(() => SignInProfileDirectory.Create(dataRoot, profileId, timeout.Token), timeout.Token).ConfigureAwait(false);
            bool started = false;
            try { return await dispatcher.InvokeAsync(() => { started = true; return RunAsync(directory, timeout.Token); }, DispatcherPriority.Normal, CancellationToken.None).Task.Unwrap().ConfigureAwait(false); }
            finally
            {
                if (!started)
                {
                    try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await Task.Run(() => directory.Remove(cleanup.Token)).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                    { _cleanupFailed = true; _incompleteDirectory = directory; }
                }
            }
        }
        finally { Volatile.Write(ref _active, 0); }
    }

    private async Task<RobloxSessionCredential> RunAsync(SignInProfileDirectory directory, CancellationToken ct)
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
        Task? saveTask = null;
        bool closing = false, controllerReady = false;
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
        Exception? cleanupProblem = null;
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
            await browser.EnsureCoreWebView2Async(environment, options);
            controllerReady = true;
            ct.ThrowIfCancellationRequested();
            var core = browser.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = true;
            core.NavigationStarting += NavigationStarting;
            core.NewWindowRequested += NewWindowRequested;
            core.DownloadStarting += DownloadStarting;
            core.PermissionRequested += PermissionRequested;
            core.ProcessFailed += ProcessFailed;
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
            if (controllerReady)
            {
                var core = browser.CoreWebView2;
                core.NavigationStarting -= NavigationStarting; core.NewWindowRequested -= NewWindowRequested;
                core.DownloadStarting -= DownloadStarting; core.PermissionRequested -= PermissionRequested; core.ProcessFailed -= ProcessFailed;
                try { core.CookieManager.DeleteAllCookies(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { cleanupProblem = ex; }
            }
            browser.Dispose();
            closing = true; window.Closing -= close; save.Click -= saveClick; cancel.Click -= cancelClick; window.Close();
            if (controllerReady)
            {
                try { await browserExited.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException ex) { cleanupProblem ??= ex; }
            }
            if (environment is not null) environment.BrowserProcessExited -= exited;
            if (cleanupProblem is null)
            {
                try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await Task.Run(() => directory.Remove(cleanup.Token)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { cleanupProblem = ex; }
            }
            if (cleanupProblem is not null)
            {
                _cleanupFailed = true; _incompleteDirectory = directory; accepted?.Dispose();
            }
            if (completion.Task.IsCompletedSuccessfully && !ReferenceEquals(accepted, completion.Task.Result)) completion.Task.Result.Dispose();
        }
        if (cleanupProblem is not null) throw new AccountLinkException("The private sign-in browser did not finish cleanup. This sign-in was not saved. Close Zenith Sessions before trying again; private files were preserved for cleanup.");
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
        void NewWindowRequested(object? _, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; status.Text = "Pop-up navigation is disabled in this private sign-in. Use an option on Roblox’s page."; }
        void DownloadStarting(object? _, CoreWebView2DownloadStartingEventArgs e) { e.Cancel = true; }
        void PermissionRequested(object? _, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; }
        void ProcessFailed(object? _, CoreWebView2ProcessFailedEventArgs e) { completion.TrySetException(new AccountLinkException("The private Roblox browser stopped. Retry sign-in; existing saved accounts were preserved.")); }
    }
    public ValueTask DisposeAsync() { _incompleteDirectory?.ReleaseOwnership(); _incompleteDirectory = null; return ValueTask.CompletedTask; }
}
