using Microsoft.Win32;
using System.Windows;

namespace Zenith.Sessions.App;

public partial class MainWindow
{
    private void ExportProtectedBackup(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanUsePortableBackup) return;
        var file = new SaveFileDialog { Filter = "Protected backup — contains saved sign-ins (*.zenithbackup)|*.zenithbackup",
            FileName = "Zenith-Sessions-Saved-SignIns.zenithbackup", AddExtension = true };
        if (file.ShowDialog(this) != true) return;
        var review = _vm.ReviewPortableBackup(PortableBackupAction.Export, file.FileName);
        if (review is null || MessageBox.Show(this,
            "Export profiles, groups, favorite games, allowed preferences and reusable saved Roblox sign-ins to this file?\n\nAnyone with the file and its backup password can use those sign-ins. Keep the password separately. This does not replace the metadata-only export.\n\n" + file.FileName,
            "Export backup containing sign-ins", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ShowPortablePassword(review, PortableBackupAction.Export, file.FileName, false);
    }
    private void ImportProtectedBackup(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanUsePortableBackup) return;
        var file = new OpenFileDialog { Filter = "Protected Zenith backup (*.zenithbackup)|*.zenithbackup", CheckFileExists = true };
        if (file.ShowDialog(this) != true) return;
        var choice = MessageBox.Show(this,
            "Also apply the backup's theme, account density, capacity/performance preferences and tile layout?\n\nYes applies those preferences. No keeps current preferences. Local launch approvals, executable choices, security settings, automation and Discord settings are always kept.\n\nYou will review the backup's contents after entering its password.",
            "Import preferences", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        if (choice == MessageBoxResult.Cancel) return;
        bool preferences = choice == MessageBoxResult.Yes;
        var review = _vm.ReviewPortableBackup(PortableBackupAction.Import, file.FileName, preferences);
        if (review is not null) ShowPortablePassword(review, PortableBackupAction.Import, file.FileName, preferences);
    }
    private void ShowPortablePassword(ShellViewModel.PortableBackupReview initial, PortableBackupAction action, string path, bool preferences)
    {
        ShellViewModel.PortableBackupReview? review = initial;
        bool export = action == PortableBackupAction.Export;
        var dialog = new VaultPasswordDialog(export ? "Protect exported sign-ins" : "Open protected backup",
            export ? "Choose a separate backup password of at least 12 characters. It protects this portable file; losing it makes the backup unusable."
                : "Enter this file's backup password. It may be different from the current library's master password. Nothing is imported until you review the contents.",
            "Backup password", export, export ? "Export sign-ins" : "Open and review", async password =>
            {
                var attempt = review ?? _vm.ReviewPortableBackup(action, path, preferences); review = null;
                return attempt is not null && await _vm.ApplyPortableBackupAsync(attempt, password, summary =>
                    MessageBox.Show(this, $"Import into this library?\n\n{summary.Profiles} profiles · {summary.Groups} groups · {summary.Favorites} favorite games · {summary.SignIns} proposed sign-ins\n\nPreferences: {(summary.ApplyPreferences ? "apply allowed preferences" : "keep current preferences")}\n\nNew sign-ins are checked with Roblox for the exact exported player. Existing local sign-ins are kept. Launches and automation are paused. Stopping can leave completed imports in the library; there is no rollback to old sign-ins.",
                        "Review protected import", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
            }, () => _vm.PortableBackupStatus, _vm.CancelPortableBackup) { Owner = this };
        _vm.PanicRequested += dialog.RequestStop;
        try { dialog.ShowDialog(); }
        finally { _vm.PanicRequested -= dialog.RequestStop; }
    }
}
