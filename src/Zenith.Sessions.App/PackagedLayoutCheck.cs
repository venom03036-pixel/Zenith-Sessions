using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

// Only --self-check uses this isolated STA. Resolve the actual shipped BAML,
// application resources and bindings, without showing a native window or input.
internal static class PackagedLayoutCheck
{
    public static Task VerifyAsync(string root, CancellationToken ct) =>
        RunOnStaAsync(() => new App { Options = new(root, false, true), ShutdownMode = ShutdownMode.OnExplicitShutdown },
            app => VerifyOnDispatcherAsync(app, root, ct), app => app.Shutdown(), ct);

    // create runs on the new STA thread before its dispatcher pumps, body runs on that
    // dispatcher, and cleanup runs on the same thread after the dispatcher stops.
    internal static Task RunOnStaAsync<TState>(Func<TState> create, Func<TState, Task> body, Action<TState> cleanup, CancellationToken ct)
        where TState : class
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            TState? state = null;
            Exception? failure = null;
            try
            {
                var created = state = create();
                var operation = dispatcher.InvokeAsync(() => body(created)).Task.Unwrap();
                var shutdown = operation.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Dispatcher.Run();
                shutdown.GetAwaiter().GetResult(); operation.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                // Shutdown only after the tracked operation completes. Shutting down
                // inside its callback can retire the dispatcher before its Task completes.
                try { if (state is not null) cleanup(state); }
                catch (Exception ex) { failure ??= ex; }
                finally { if (!dispatcher.HasShutdownStarted) dispatcher.InvokeShutdown(); }
            }
            if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
        }) { IsBackground = true, Name = "Zenith packaged layout check" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        // The caller's deadline must end the stage even when body never completes (a token-less
        // await, a modal loop). The dispatcher cannot be stopped early without the retirement race
        // above, so a stalled one is abandoned: the thread is background and ends with the process.
        return done.Task.WaitAsync(ct);
    }

    private static async Task VerifyOnDispatcherAsync(App app, string root, CancellationToken ct)
    {
        ShellViewModel? vm = null; MainWindow? window = null; Exception? failure = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            app.InitializeComponent();
            ThemeManager.Apply(app.Resources, ThemeCatalog.DefaultId);
            vm = new ShellViewModel(root, true, Dispatcher.CurrentDispatcher);
            window = MainWindow.CreateForLayoutCheck(vm);
            RequireShellShortcuts(window, vm.Panic, vm.Navigate, vm.ShowWindowsOverview);
            window.Measure(new Size(1320, 840));
            window.Arrange(new Rect(0, 0, 1320, 840)); window.UpdateLayout();
            vm.Navigate.Execute("JoinGame");
            window.UpdateLayout();
            if (!vm.IsJoinGame || !vm.IsSessionWorkspace || vm.IsSessions || vm.IsAutomation
                || window.FindName("JoinGamePage") is not FrameworkElement { Visibility: Visibility.Visible }
                || window.FindName("DestinationInput") is not FrameworkElement
                || window.FindName("DestinationSearchPanel") is not FrameworkElement
                || window.FindName("NativeLaunchStages") is not System.Windows.Controls.Expander)
                throw new InvalidOperationException("The packaged Sessions join route lost its production controls or workspace state.");
            // Ancestor-relative DataContexts (the private-server card) resolve at DataBind priority in a never-shown window.
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            RequireJoinGameSources(window, vm);
            var diagnosticId = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
            var diagnostic = new NativeLaunchDiagnosticSnapshot(diagnosticId, 1, at, at,
                NativeLaunchDiagnosticStage.Finished, NativeLaunchCleanupStatus.Complete,
                TrustStatus: ClientExecutableTrustStatus.Rejected, TrustReason: ClientExecutableTrustReason.Unsigned,
                ExecutableObservedUtc: at, CleanupObservedUtc: at).Validated();
            var presentation = NativeLaunchDiagnosticPresentation.Project(diagnosticId, diagnostic);
            if (!presentation.Available || presentation.Snapshot?.OperationId != diagnosticId
                || presentation.AccountVerified || presentation.ArrivalVerified || presentation.InputAuthorized
                || !presentation.Trust.Contains("signature", StringComparison.OrdinalIgnoreCase)
                || !NativeLaunchDiagnosticPresentation.Unavailable(diagnosticId).Summary.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || NativeLaunchDiagnosticPresentation.Project(Guid.NewGuid(), diagnostic).Available)
                throw new InvalidOperationException("The packaged fixed diagnostic presentation lost its exact-operation or unavailable boundary.");
            vm.Navigate.Execute("Automation"); window.UpdateLayout();
            if (!vm.IsAutomation || vm.IsSessionWorkspace || vm.IsJoinGame
                || window.FindName("JoinGamePage") is not FrameworkElement { Visibility: Visibility.Collapsed })
                throw new InvalidOperationException("The packaged macro workspace did not hide joining controls.");
            vm.Navigate.Execute("Sessions"); window.UpdateLayout();
            if (!vm.IsSessions || !vm.IsSessionWorkspace || vm.IsJoinGame)
                throw new InvalidOperationException("The packaged client-session route did not retain its distinct meaning.");
            if (window.FindName("ClientPriorityTools") is not System.Windows.Controls.Expander priorityTools)
                throw new InvalidOperationException("The packaged priority tools are missing.");
            priorityTools.IsExpanded = true;
            if (window.FindName("ClientMemoryTrimTools") is not System.Windows.Controls.Expander trimTools)
                throw new InvalidOperationException("The packaged memory trim tools are missing.");
            trimTools.IsExpanded = true;
            if (window.FindName("ClientSoundTools") is not System.Windows.Controls.Expander soundTools)
                throw new InvalidOperationException("The packaged client sound tools are missing.");
            soundTools.IsExpanded = true;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            // A never-shown Window can retain its disconnected layout. Measure the
            // production content itself to exercise the expanded panel offscreen.
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1320, 840));
            content.Arrange(new Rect(0, 0, 1320, 840));
            content.UpdateLayout();
            if (window.FindName("SetClientPriorityBelowNormal") is not System.Windows.Controls.Button priorityButton
                || priorityButton.Command != vm.SetClientsBelowNormalPriority || priorityButton.IsEnabled
                || vm.IsChangingClientPriority || vm.PendingPriorityRestoreCount != 0)
                throw new InvalidOperationException("The packaged priority controls lost their command binding or sample-mode boundary.");
            if (window.FindName("SetClientEfficiencyMode") is not System.Windows.Controls.Button efficiencyButton
                || efficiencyButton.Command != vm.SetClientsEfficiencyMode || efficiencyButton.IsEnabled)
                throw new InvalidOperationException("The packaged efficiency mode control lost its command binding or sample-mode boundary.");
            if (priorityTools.ActualHeight <= 0)
                throw new InvalidOperationException("The packaged priority controls could not be laid out.");
            if (window.FindName("TrimClientsNowButton") is not System.Windows.Controls.Button trimButton
                || trimButton.Command != vm.TrimClientsNow || trimButton.IsEnabled || vm.IsTrimmingClients || vm.CanTrimClients)
                throw new InvalidOperationException("The packaged memory trim controls lost their command binding or sample-mode boundary.");
            if (trimTools.ActualHeight <= 0)
                throw new InvalidOperationException("The packaged memory trim controls could not be laid out.");
            RequireSearchReachesSessionsLayout(window, vm);
            RequireEveryThemeLaysOut(app.Resources, content, SessionsThemes.PackagedPalettes());
            if (window.FindName("MuteClientsButton") is not System.Windows.Controls.Button muteButton
                || muteButton.Command != vm.MuteClients || muteButton.IsEnabled || vm.IsChangingClientSound || vm.CanChangeClientSound
                || window.FindName("MuteArrivingClientsOption") is not System.Windows.Controls.CheckBox { IsChecked: false }
                || window.FindName("ResumeArrivalMuteButton") is not System.Windows.Controls.Button resumeButton
                || resumeButton.Command != vm.ResumeArrivalMute || resumeButton.IsEnabled || vm.CanResumeArrivalMute)
                throw new InvalidOperationException("The packaged client sound controls lost their command binding, sample-mode boundary or off default.");
            if (soundTools.ActualHeight <= 0)
                throw new InvalidOperationException("The packaged client sound controls could not be laid out.");
            if (window.FindName("SettingsPublicNetworkGuard") is not System.Windows.Controls.CheckBox { IsChecked: false } || vm.IsPublicNetworkHeld)
                throw new InvalidOperationException("The packaged public-network guard control lost its binding or its off default.");
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { if (vm is not null) await vm.DisposeAsync(); }
            catch (Exception ex) { failure ??= ex; }
            try { window?.Close(); }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Every palette Settings can show, and High Contrast from this computer's SystemColors, must fill every key the shipped window
    // reads, keep its text readable and lay the window out. The default palette is restored afterwards, whatever happened.
    internal static void RequireEveryThemeLaysOut(ResourceDictionary resources, FrameworkElement content, IEnumerable<ThemePreset> palettes)
    {
        try
        {
            foreach (var palette in palettes)
            {
                if (SessionsThemes.ReadabilityProblem(palette) is { } problem)
                    throw new InvalidOperationException($"The packaged {palette.Name} theme is not readable: {problem}.");
                SessionsThemes.Apply(resources, palette);
                content.Measure(new Size(1320, 840)); content.Arrange(new Rect(0, 0, 1320, 840)); content.UpdateLayout();
            }
        }
        finally { ThemeManager.Apply(resources, ThemeCatalog.DefaultId); }
    }

    // Settings search must reach a preference on another page through the shipped window: open Sessions and the collapsed
    // tile layout section, and change no draft. This window is never shown, so keyboard focus is expected to be refused.
    private static void RequireSearchReachesSessionsLayout(Window window, ShellViewModel vm)
    {
        vm.Navigate.Execute("Settings"); window.UpdateLayout();
        if (window.FindName("ClientLayoutPreferences") is not System.Windows.Controls.Expander { IsExpanded: false } layout)
            throw new InvalidOperationException("The packaged tile layout section is missing or already open.");
        string columns = vm.TileColumns, saveStatus = vm.SaveStatus;
        vm.SettingsSearchQuery = "tile columns";
        var result = vm.SettingsSearchResults.FirstOrDefault(entry => entry.Id == "sessions-layout")
            ?? throw new InvalidOperationException("The packaged Settings search no longer finds the tile layout.");
        vm.OpenSettingSearchResult.Execute(result);
        if (!vm.IsSessions || !layout.IsExpanded || vm.TileColumns != columns || vm.SaveStatus != saveStatus
            || !vm.Notice.Contains("did not change", StringComparison.Ordinal))
            throw new InvalidOperationException("The packaged Settings search did not open the tile layout on Sessions without changing the draft.");
        vm.ClearSettingsSearch.Execute(null);
    }

    // Loading the BAML alone does not notice a renamed or removed control or a lost DataContext. Require the
    // private-server box and both game-source chip lists by name, each bound to its own view model.
    internal static void RequireJoinGameSources(FrameworkElement scope, ShellViewModel vm)
    {
        if (scope.FindName("ShareLinkInput") is not TextBox shareLink || !ReferenceEquals(shareLink.DataContext, vm.ShareLink)
            || BindingOperations.GetBindingExpression(shareLink, TextBox.TextProperty) is not { } linkInput
            || !ReferenceEquals(linkInput.ResolvedSource, vm.ShareLink) || linkInput.ResolvedSourcePropertyName != nameof(ShareLinkViewModel.LinkInput)
            || scope.FindName("RobloxFavoriteChips") is not ItemsControl favorites || !ReferenceEquals(favorites.DataContext, vm.RobloxFavorites)
            || !ReferenceEquals(favorites.ItemsSource, vm.RobloxFavorites.Chips)
            || scope.FindName("RecentlyPlayedChips") is not ItemsControl recent || !ReferenceEquals(recent.DataContext, vm.RecentlyPlayed)
            || !ReferenceEquals(recent.ItemsSource, vm.RecentlyPlayed.Chips))
            throw new InvalidOperationException("The packaged join route lost its private-server or game-source controls.");
    }

    // The window builds its key bindings from ShellShortcuts, which the Guide lists. Require exactly one binding per row, in
    // table order, on the command its sidebar button uses, and a named search box for every tab Ctrl+F reaches.
    internal static void RequireShellShortcuts(FrameworkElement window, ICommand panic, ICommand navigate, ICommand showWindowsOverview)
    {
        var rows = ShellShortcuts.All; var bindings = window.InputBindings;
        bool matches = bindings.Count == rows.Count;
        for (int i = 0; matches && i < rows.Count; i++)
        {
            var row = rows[i];
            matches = bindings[i] is KeyBinding { Gesture: KeyGesture gesture } binding && gesture.Key == row.Key && gesture.Modifiers == row.Modifiers
                && Equals(binding.CommandParameter, row.Page) && binding.Command is { } command && row.Action switch
                {
                    ShellShortcutAction.Panic => ReferenceEquals(command, panic),
                    ShellShortcutAction.Navigate => ReferenceEquals(command, navigate),
                    ShellShortcutAction.ShowWindowsOverview => ReferenceEquals(command, showWindowsOverview),
                    _ => !ReferenceEquals(command, panic) && !ReferenceEquals(command, navigate) && !ReferenceEquals(command, showWindowsOverview),
                };
        }
        if (!matches || ShellShortcuts.SearchBoxes.Values.Any(name => window.FindName(name) is not TextBox))
            throw new InvalidOperationException("The packaged window's keyboard shortcuts do not match the shortcut table the Guide lists.");
    }
}
