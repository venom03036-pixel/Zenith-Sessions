using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MacroSuite.App.Themes;
using Microsoft.Win32;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Forms = System.Windows.Forms;

namespace Zenith.Sessions.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly Forms.NotifyIcon? _tray;
    private bool _exiting, _exitStarted;
    private HwndSource? _source;
    private readonly GlobalPanicRegistration _panicKey=new();
    public MainWindow(ShellViewModel vm) : this(vm, false) { }
    // Resolve the shipped view without creating a tray owner or registering shortcuts.
    internal static MainWindow CreateForLayoutCheck(ShellViewModel vm) => new(vm, true);
    private MainWindow(ShellViewModel vm, bool layoutCheckOnly)
    {
        _vm = vm; DataContext = vm; InitializeComponent();
        if (layoutCheckOnly) return;
        vm.SettingsSearchNavigationRequested+=OpenSearchedSetting;
        PreviewKeyDown+=SettingsPagePreviewKeyDown;
        IsVisibleChanged+=(_,_)=>UpdatePreviewVisibility();
        StateChanged+=(_,_)=>UpdatePreviewVisibility();
        UpdatePreviewVisibility();
        vm.ThemeChanged += SetTheme;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Zenith_Macros_Teal.ico")).Stream;
        using var original = new System.Drawing.Icon(stream);
        // Tray Panic, Lock and Exit act on this manager only, so its icon names it (personal, sample preview or another library).
        _tray = new Forms.NotifyIcon { Text = vm.InstanceTitle, Icon = (System.Drawing.Icon)original.Clone(), Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Zenith Sessions", null, (_, _) => Dispatcher.Invoke(Restore));
        menu.Items.Add("Panic stop", null, (_, _) => Dispatcher.Invoke(() => vm.Panic.Execute(null)));
        menu.Items.Add("Lock controls", null, (_, _) => Dispatcher.Invoke(vm.LockDashboard));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await Dispatcher.InvokeAsync(RequestExitAsync).Task.Unwrap());
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => Dispatcher.Invoke(Restore);
        Loaded += async (_, _) => await vm.InitializeAsync();
        Closing += OnClosing;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle; _source = HwndSource.FromHwnd(handle); _source.AddHook(OnMessage);
            // Isolated sample previews must not take a real desktop transport shortcut.
            vm.SetPanicRegistration(!vm.IsSample && _panicKey.RegisterF12(handle));
        };
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }
    private void ContentSizeChanged(object sender,SizeChangedEventArgs e)
    {
        // Base density on the usable page, including the permanent navigation rail.
        // Changing height or moving between monitors does not rebuild the editor or query storage.
        if(!e.WidthChanged) return;
        _vm.SetAccountsViewportWidth(e.NewSize.Width);
        bool compact=e.NewSize.Width<1132;
        if(_vm.CompactLayout!=compact) _vm.CompactLayout=compact;
    }
    private void SetTheme(ThemePreset theme) => ThemeManager.Apply(Application.Current.Resources, theme);
    private void UpdatePreviewVisibility()=>_vm.SetPreviewWindowVisible(IsVisible && WindowState!=WindowState.Minimized);
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    { if (e.Reason == SessionSwitchReason.SessionLock) Dispatcher.BeginInvoke(_vm.LockDashboard); }
    private nint OnMessage(nint window, int message, nint w, nint l, ref bool handled)
    {
        if (message == GlobalPanicRegistration.Message && w == GlobalPanicRegistration.Identifier) { _vm.Panic.Execute(null); handled = true; }
        return 0;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    { if (!_exiting) { e.Cancel = true; Hide(); } }
    protected override void OnClosed(EventArgs e)
    {
        _panicKey.Dispose();
        _source?.RemoveHook(OnMessage); SystemEvents.SessionSwitch -= OnSessionSwitch; _vm.ThemeChanged -= SetTheme;
        _vm.SettingsSearchNavigationRequested-=OpenSearchedSetting;PreviewKeyDown-=SettingsPagePreviewKeyDown;
        if (_tray is not null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Icon?.Dispose(); _tray.Dispose(); } base.OnClosed(e);
    }
    public async Task RequestExitAsync()
    {
        if (_exitStarted) return; _exitStarted = true;
        try
        {
            await _vm.DisposeAsync();
            if (_vm.PendingPriorityRestoreCount > 0)
                MessageBox.Show(this, "Windows could not confirm restoration of " + _vm.PendingPriorityRestoreCount
                    + " original client priorities. Review those clients in Task Manager. The settings last until you change them or the clients exit.",
                    "Client priority restoration", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (((App)Application.Current).Coordinator is { } coordinator) await coordinator.StopAsync();
        }
        finally { _exiting = true; Close(); }
    }
    private void Restore() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; if (!Activate()) _vm.Inform("Window restored. Windows denied activation; select it from the taskbar."); }
    public void RestoreFromLaunch() => Restore();
    private void DragHeader(object sender, MouseButtonEventArgs e)
    { if (e.ChangedButton == MouseButton.Left) { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else DragMove(); } }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseToTray(object sender, RoutedEventArgs e) => Close();
    private void StartNativeTrialClick(object sender,RoutedEventArgs e)
    {
        var review = _vm.CaptureNativeTrialReview();
        if (review is null) return;
        try
        {
            var answer = MessageBox.Show(this, review.SelectionSummary + "\n\n" + review.InstallationSummary
                + "\n\nContinue to the selected account's Roblox game page. Choosing Play there may start a real Roblox client.",
                "Confirm native account trial", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK) _vm.StartNativeTrial.Execute(review);
        }
        finally { _vm.DiscardNativeTrialReview(review); }
    }
    private void SettingsPagePreviewKeyDown(object sender,KeyEventArgs e)
    {
        if(!_vm.IsSettings||_vm.IsLocked||Keyboard.Modifiers!=ModifierKeys.Control||e.Key!=Key.F)return;
        SettingsSearchBox.Focus();SettingsSearchBox.SelectAll();e.Handled=true;
    }
    private void SettingsSearchKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape){_vm.ClearSettingsSearch.Execute(null);e.Handled=true;return;}
        if(e.Key==Key.Down&&_vm.HasSettingsSearchQuery&&_vm.SettingsSearchResults.Count>0)
        {SettingsSearchList.SelectedIndex=0;SettingsSearchList.Focus();e.Handled=true;}
    }
    private void SettingsResultsKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape){_vm.ClearSettingsSearch.Execute(null);SettingsSearchBox.Focus();e.Handled=true;return;}
        if(e.Key==Key.Enter&&SettingsSearchList.SelectedItem is SettingsSearchEntry entry)
        {_vm.OpenSettingSearchResult.Execute(entry);e.Handled=true;}
    }
    private void OpenSearchedSetting(SettingsSearchEntry entry)
    {
        if(!_vm.IsSettings||_vm.IsLocked)return;
        bool fallback=entry.Target==SettingsSearchTarget.CustomTiming&&!_vm.IsCustomPerformance;
        FrameworkElement anchor=entry.Target switch
        {
            SettingsSearchTarget.Appearance=>SettingsAppearanceAnchor,
            SettingsSearchTarget.Performance or SettingsSearchTarget.CustomTiming=>SettingsPerformanceAnchor,
            SettingsSearchTarget.Memory=>SettingsMemoryAnchor,
            SettingsSearchTarget.Installations=>SettingsInstallationsAnchor,
            SettingsSearchTarget.SignIn=>SettingsSignInAnchor,
            SettingsSearchTarget.Save=>SettingsSaveButton,
            SettingsSearchTarget.Recovery=>SettingsRecoveryAnchor,
            _=>SettingsSearchBox
        };
        FrameworkElement focus=entry.Target switch
        {
            SettingsSearchTarget.Performance=>SettingsPerformanceProfile,
            SettingsSearchTarget.CustomTiming=>fallback?SettingsPerformanceProfile:SettingsCustomTiming,
            SettingsSearchTarget.Memory=>SettingsSpareRam,
            SettingsSearchTarget.Recovery=>SettingsRecoveryButton,
            _=>anchor
        };
        if(!fallback)
            focus=entry.Id switch
            {
                "dashboard-timing"=>SettingsDashboardTiming,
                "launch-spacing"=>SettingsLaunchSpacing,
                "stabilization-timing"=>SettingsStabilizationTiming,
                "physical-per-client"=>SettingsPhysicalPerClient,
                "commit-per-client"=>SettingsCommitPerClient,
                "maximum-clients"=>SettingsMaximumClients,
                _=>focus
            };
        if(entry.Id=="performance-transfer")
        {
            SettingsPerformanceTransfer.IsExpanded=true;anchor=SettingsPerformanceTransfer;
            focus=SettingsPerformanceTransferInput.IsEnabled?SettingsPerformanceTransferInput:SettingsPerformanceTransfer;
        }
        anchor.BringIntoView();focus.BringIntoView();bool focused=focus.Focus();_vm.ReportSettingSearchNavigation(entry,fallback,focused);
    }
    private async void ExitManager(object sender, RoutedEventArgs e) => await RequestExitAsync();
    private void OpenDataFolder(object sender, RoutedEventArgs e) => _vm.OpenFolder.Execute(null);
    private void CopyVisibleDiagnostics(object sender,RoutedEventArgs e)
    {try {string text=_vm.BuildVisibleDiagnosticText();if(text.Length==0){_vm.Inform("No visible diagnostic events match the current filters.");return;}Clipboard.SetText(text);_vm.Inform("Visible diagnostic events copied.");}catch(Exception ex) when(ex is System.Runtime.InteropServices.COMException or InvalidOperationException){ClipboardUnavailable(ex);}}
    private void CopyDiagnosticReport(object sender,RoutedEventArgs e)
    {try {Clipboard.SetText(_vm.BuildDiagnosticReport());_vm.Inform("Redacted diagnostic report copied. Nothing was uploaded.");}catch(Exception ex) when(ex is System.Runtime.InteropServices.COMException or InvalidOperationException){ClipboardUnavailable(ex);}}
    private void CopyRequestedServerLink(object sender,RoutedEventArgs e)
    {
        var link=_vm.BuildRequestedDestinationLink();
        if(link is null) {_vm.Inform("Use or discard the pending destination, then choose a request to copy.");return;}
        try {Clipboard.SetText(link);_vm.Inform("Requested destination link copied. No account was launched or joined.");}
        catch(Exception ex) when(ex is System.Runtime.InteropServices.COMException or InvalidOperationException) {ClipboardUnavailable(ex);}
    }
    private void CopyReportedServerLink(object sender,RoutedEventArgs e)
    {
        if(sender is not FrameworkElement {DataContext:SessionRow row}) return;
        var link=_vm.BuildReportedDestinationLink(row);
        if(link is null) {_vm.Inform("This client has no current diagnostic destination. Refresh observations; stale or ambiguous values are not copied.");return;}
        try {Clipboard.SetText(link);_vm.Inform("Unverified reported destination link copied. Access and arrival remain unverified.");}
        catch(Exception ex) when(ex is System.Runtime.InteropServices.COMException or InvalidOperationException) {ClipboardUnavailable(ex);}
    }
    private void OpenGamePageClick(object sender,RoutedEventArgs e)
    {
        var link=_vm.BuildRequestedGamePageLink();
        if(link is null) {_vm.Inform("Choose a game first. A pending draft must be used or discarded before its page can open.");return;}
        // Only the public game page ever leaves the app: never a server, share or private code.
        if(!link.StartsWith("https://www.roblox.com/games/",StringComparison.Ordinal)) return;
        try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) {UseShellExecute=true});_vm.Inform("Opened the public game page in your browser. No account was launched or signed in.");}
        catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException) {_vm.Fail(ex);_vm.Inform("The browser could not be opened. Use Copy link instead.");}
    }
    private void CopyGamePageLinkClick(object sender,RoutedEventArgs e)
    {
        var link=_vm.BuildRequestedGamePageLink();
        if(link is null) {_vm.Inform("Choose a game first. A pending draft must be used or discarded before its link can be copied.");return;}
        try {Clipboard.SetText(link);_vm.Inform("Game page link copied. IDs only; no account was launched or joined.");}
        catch(Exception ex) when(ex is System.Runtime.InteropServices.COMException or InvalidOperationException) {ClipboardUnavailable(ex);}
    }
    private void JoinSameGameClick(object sender,RoutedEventArgs e)
    {
        if(sender is not FrameworkElement {DataContext:SessionRow row}) return;
        if(!_vm.JoinSameAsReported(row,sameServer:false)) {_vm.Inform("This client has no current reported game. Refresh observations; stale or ambiguous values are not used.");return;}
        JoinGamePage.ScrollToTop();
    }
    private void ReviewReportedServerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionRow row }) return;
        if (!_vm.JoinSameAsReported(row, sameServer: true)) { _vm.Inform("This client has no current reported server. Refresh observations; stale or ambiguous values are not used."); return; }
        JoinGamePage.ScrollToTop();
    }
    private void ClipboardUnavailable(Exception error)
    { _vm.Fail(error);_vm.Inform("The clipboard is busy or unavailable. Try copying again."); }
    private void CopyPerformanceShareCode(object sender, RoutedEventArgs e)
    {
        try
        {
            var code = _vm.BuildPerformanceShareCode();
            if (code is null) return;
            Clipboard.SetText(code);
            _vm.Inform("Manager performance code copied. Accounts, sign-in and local safety settings were excluded.");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { ClipboardUnavailable(error); }
        catch (ArgumentException error) { _vm.Fail(error); }
        catch (PerformanceSettingsTransferException error) { _vm.Fail(error); }
    }
    private void ImportPerformanceFile(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanTransferPerformance) return;
        var dialog = new OpenFileDialog { Title = "Review manager performance settings", Filter = "Zenith performance settings (*.json)|*.json", Multiselect = false };
        if (dialog.ShowDialog(this) == true && _vm.CanTransferPerformance) _vm.ImportPerformanceSettings.Execute(dialog.FileName);
    }
    private void ExportPerformanceFile(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanTransferPerformance) return;
        var dialog = new SaveFileDialog { Title = "Save manager performance settings", Filter = "Zenith performance settings (*.json)|*.json", DefaultExt = ".json", AddExtension = true, FileName = "Zenith-performance.json" };
        if (dialog.ShowDialog(this) == true && _vm.CanTransferPerformance) _vm.ExportPerformanceSettings.Execute(dialog.FileName);
    }
    private void DeletePrivateServer(object sender,RoutedEventArgs e)
    {
        var shareLink=_vm.ShareLink;
        if(_vm.IsLocked || !shareLink.CanRemoveSaved || shareLink.SelectedSaved is not { } selected) return;
        if(MessageBox.Show(this,$"Remove the saved private server “{selected.Name}”? Its link code will be deleted from this PC. No account or game client will be changed.","Remove saved private server",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            shareLink.Remove.Execute(null);
    }
    private void DeleteFavoriteDestination(object sender,RoutedEventArgs e)
    {
        var favorites=_vm.DestinationFavorites;
        if(_vm.IsLocked || !favorites.CanUseFavorite || favorites.SelectedFavorite is not { } selected) return;
        if(MessageBox.Show(this,$"Remove the saved destination “{selected.Name}”? No account or game client will be changed.","Remove favorite destination",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            favorites.Delete.Execute(null);
    }
    private void SaveDiagnosticReport(object sender,RoutedEventArgs e)
    {
        if(_vm.IsLocked || !_vm.ExportDiagnostics.CanExecute(null)) return;
        var dialog=new SaveFileDialog {Filter="Redacted diagnostics (*.json)|*.json",FileName="Zenith-Sessions-Diagnostics.json",AddExtension=true,DefaultExt=".json"};
        if(dialog.ShowDialog(this)==true) _vm.ExportDiagnostics.Execute(dialog.FileName);
    }
    private void CopyLaunchEvidence(object sender,RoutedEventArgs e)
    {
        if(!_vm.HasLaunchEvidence)return;
        try{Clipboard.SetText(_vm.BuildNativeTrialEvidenceReport());_vm.Inform("Unverified launch evidence copied. Player/place/server IDs are included; no credentials or raw logs.");}
        catch(Exception error)when(error is System.Runtime.InteropServices.COMException or InvalidOperationException){ClipboardUnavailable(error);}
    }
    private void SaveLaunchEvidence(object sender,RoutedEventArgs e)
    {
        if(!_vm.HasLaunchEvidence||!_vm.ExportLaunchEvidence.CanExecute(null))return;
        var dialog=new SaveFileDialog{Filter="Launch evidence (*.json)|*.json",FileName="Zenith-Launch-Evidence.json",AddExtension=true,DefaultExt=".json"};
        if(dialog.ShowDialog(this)==true&&_vm.HasLaunchEvidence)_vm.ExportLaunchEvidence.Execute(dialog.FileName);
    }
    private void OpenMoreOptions(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; }
    }
    private void AddProfile(object sender, RoutedEventArgs e)
    { if (!_vm.IsLocked) new ProfileDialog(AccountProfile.Create(""), _vm.SaveAccountAsync,_vm.HasGroupCatalog?_vm.GroupCatalogChoices:null) { Owner = this }.ShowDialog(); }
    private void AddCheckedToGroup(object sender, RoutedEventArgs e)
    {
        if (_vm.IsLocked || !_vm.IsReady) return;
        var profiles = _vm.Accounts.Where(row => row.IsChecked).Select(row => row.Profile).ToArray();
        if (profiles.Length == 0)
        { MessageBox.Show(this, "Check the accounts you want to add, or use Select page first.", "Choose accounts", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var groups = _vm.Groups.Where(group => group.Value is not null).Select(group => group.Value!).ToArray();
        new AccountGroupDialog(profiles.Length, groups, name => _vm.AddAccountsToGroupAsync(profiles, name),_vm.HasGroupCatalog?_vm.GroupCatalogChoices:null) { Owner = this }.ShowDialog();
    }
    private void ManageGroups(object sender, RoutedEventArgs e)
    { if (_vm.IsReady && !_vm.IsLocked) new AccountGroupManagerDialog(_vm) { Owner = this }.ShowDialog(); }
    private void EditProfile(object sender, RoutedEventArgs e)
    {
        if(e is System.Windows.Input.MouseButtonEventArgs && IsInteractiveRowOrigin(e.OriginalSource as DependencyObject)) return;
        if (!_vm.IsLocked && _vm.SelectedAccount is { } row) new ProfileDialog(row.Profile, _vm.SaveAccountAsync,_vm.HasGroupCatalog?_vm.GroupCatalogChoices:null) { Owner = this }.ShowDialog();
    }
    private static bool IsInteractiveRowOrigin(DependencyObject? element)
    {
        while(element is not null)
        {
            if(element is System.Windows.Controls.Primitives.ButtonBase) return true;
            element=element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D?
                System.Windows.Media.VisualTreeHelper.GetParent(element):LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
    private void AccountRowMenu(object sender,RoutedEventArgs e)
    {
        if(_vm.IsLocked || sender is not System.Windows.Controls.MenuItem {DataContext:AccountRow row,Tag:string action} || !_vm.Accounts.Contains(row)) return;
        _vm.SelectedAccount=row;
        switch(action)
        {
            case "Edit":EditProfile(sender,e);break;
            case "Avatar":_vm.RefreshPublicAccount.Execute(row);break;
            case "Preferred":_vm.UseSelectedPreferredPlace.Execute(null);break;
            case "Disconnect":DisconnectAccount(sender,e);break;
            case "Remove":RemoveProfile(sender,e);break;
        }
    }
    private void DisconnectAccount(object sender,RoutedEventArgs e)
    {
        if(_vm.IsLocked || _vm.SelectedAccount is not { } selected) return;
        if(MessageBox.Show(this,$"Remove Zenith’s saved sign-in for “{selected.Label}”? The profile and running clients will remain. This does not sign out Roblox on other devices.","Remove saved sign-in",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            _vm.DisconnectAccount.Execute(null);
    }
    private void SelectPage(object sender, RoutedEventArgs e)
    { bool select = _vm.Accounts.Any(a => !a.IsChecked); foreach (var row in _vm.Accounts) row.IsChecked = select; }
    private void DeleteRecipientBundle(object sender,RoutedEventArgs e)
    {
        if(!_vm.RecipientBundles.CanUseSelected||_vm.RecipientBundles.SelectedBundle is not{ } bundle)return;
        if(MessageBox.Show(this,$"Remove the saved bundle “{bundle.Name}”? Saved accounts, queued work and running clients remain.","Remove account bundle",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            _vm.RecipientBundles.Delete.Execute(null);
    }
    private void ReplaceRecipientBundleMembers(object sender,RoutedEventArgs e)
    {
        if(!_vm.RecipientBundles.CanUseSelected||_vm.RecipientBundles.SelectedBundle is not{ } bundle)return;
        int count=_vm.Accounts.Count(row=>row.IsChecked);
        if(count==0){_vm.Inform("Check the replacement accounts in the library first.");return;}
        if(MessageBox.Show(this,$"Replace the {bundle.ProfileIds.Count} saved recipients in “{bundle.Name}” with the {count} checked accounts on this library page? Existing queued work keeps its original recipients.","Replace bundle recipients",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            _vm.RecipientBundles.ReplaceRecipients.Execute(null);
    }
    private async void RemoveProfile(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedAccount is not { } row || _vm.IsLocked) return;
        if (MessageBox.Show(this, $"Remove the saved profile “{row.Label}”? Its running client will remain open.", "Remove profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await _vm.DeleteAccountsAsync([row]); } catch (Exception ex) { _vm.Fail(ex); }
    }
    private async void RemoveSelected(object sender, RoutedEventArgs e)
    {
        var rows = _vm.Accounts.Where(a => a.IsChecked).ToArray(); if (_vm.IsLocked) return;
        if (rows.Length == 0) { _vm.Inform("Select profiles using their checkboxes before removing them."); return; }
        if (MessageBox.Show(this, $"Remove {rows.Length} selected profiles? Running clients will remain open.", "Remove selected profiles", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await _vm.DeleteAccountsAsync(rows); } catch (Exception ex) { _vm.Fail(ex); }
    }
    private async void ImportProfiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Zenith profile metadata (*.json)|*.json", CheckFileExists = true };
        if (!_vm.IsLocked && dialog.ShowDialog(this) == true) try { await _vm.ImportAsync(dialog.FileName); } catch (Exception ex) { _vm.Fail(ex); }
    }
    private async void ExportProfiles(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Zenith profile metadata (*.json)|*.json", FileName = "Zenith-Sessions-Profiles.json", AddExtension = true };
        if (!_vm.IsLocked && dialog.ShowDialog(this) == true) try { await _vm.ExportAsync(dialog.FileName); } catch (Exception ex) { _vm.Fail(ex); }
    }
    private void ClearRecentClientHistory(object sender,RoutedEventArgs e)
    {
        if(!_vm.RecentClients.CanClear) return;
        if(MessageBox.Show(this,"Clear the recent-client history? This removes historical presence records only and leaves running Roblox clients and saved accounts unchanged.","Clear recent history",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            _vm.RecentClients.Clear.Execute(null);
    }
    private async void CloseSession(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionRow row }) return;
        var session = row.Observation;
        if (MessageBox.Show(this, $"Ask Roblox process {session.ProcessId} to close? Unsaved game activity may be lost. Account identity is unverified.", "Close selected client", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await _vm.RequestCloseAsync(session); } catch (Exception ex) { _vm.Fail(ex); }
    }
    private async void ReviewLaunch(object sender,RoutedEventArgs e)
    {
        if(_vm.IsLocked || sender is not FrameworkElement { DataContext:LaunchRow row }) return;
        if(!row.CanReview)
        { _vm.Inform("Use Cancel for a pending or blocked request. Review is for an uncertain, interrupted or previously arrived client.");return; }
        if(MessageBox.Show(this,"Confirm that this account has no remaining client or startup attempt. This releases its launch reservation; it does not close any game or automatically retry. If you are unsure, choose No.","Review interrupted launch",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
        try {await _vm.ConfirmLaunchClientClosedAsync(row.Id);}catch(Exception ex) {_vm.Fail(ex);}
    }
}
