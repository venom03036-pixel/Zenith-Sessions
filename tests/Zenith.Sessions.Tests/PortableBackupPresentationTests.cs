using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests
{
    [Fact]
    public Task PortableBackupConsentIsOneUseAndPanicInvalidatesUnstartedReviews() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); string path = _root + ".zenithbackup";
        try
        {
            Assert.True(shell.CanUsePortableBackup);
            var stale = shell.ReviewPortableBackup(PortableBackupAction.Export, path)!;
            shell.Panic.Execute(null);
            Assert.False(await shell.ApplyPortableBackupAsync(stale, FixtureVaultPassword)); Assert.False(File.Exists(path));
            var current = shell.ReviewPortableBackup(PortableBackupAction.Export, path)!;
            Assert.True(await shell.ApplyPortableBackupAsync(current, FixtureVaultPassword)); Assert.True(File.Exists(path));
            Assert.False(await shell.ApplyPortableBackupAsync(current, FixtureVaultPassword));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    });

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("cancel")]
    public Task PortableBackupCancellationDrainsTheOwnedCaptureAndCannotWriteLate(string action) => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); string path = _root + ".zenithbackup";
        try
        {
            using var held = new FileStream(Path.Combine(_root, "accounts.vault", ProtectedCredentialVault.WriterLockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var capture = shell.ApplyPortableBackupAsync(shell.ReviewPortableBackup(PortableBackupAction.Export, path)!, FixtureVaultPassword);
            Assert.True(shell.PortableBackupBusy); Assert.False(shell.CanUseVaultPasswordPrimary);
            if (action == "panic") shell.Panic.Execute(null); else if (action == "lock") shell.LockDashboard(); else shell.CancelPortableBackup();
            held.Dispose(); Assert.False(await capture); Assert.False(shell.PortableBackupBusy); Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    });

    [Fact]
    public Task PortableBackupNotificationPanicCannotStartExport() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); string path = _root + ".zenithbackup";
        shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.PortableBackupBusy) && shell.PortableBackupBusy) shell.Panic.Execute(null); };
        Assert.False(await shell.ApplyPortableBackupAsync(shell.ReviewPortableBackup(PortableBackupAction.Export, path)!, FixtureVaultPassword));
        Assert.False(File.Exists(path));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PortableImportRequiresDecodedContentConfirmationAndRefencesAfterIt(bool panicAtConfirmation) => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); string path = _root + ".zenithbackup";
        try
        {
            using var fixture = PortableMetadataFixture(); File.WriteAllBytes(path, PortableBackupCodec.Seal(fixture, FixtureVaultPassword, default));
            bool shown = false;
            bool result = await shell.ApplyPortableBackupAsync(shell.ReviewPortableBackup(PortableBackupAction.Import, path)!, FixtureVaultPassword,
                summary => { shown = true; Assert.Equal(1, summary.Profiles); if (panicAtConfirmation) shell.Panic.Execute(null); return panicAtConfirmation; });
            Assert.False(result); Assert.True(shown); Assert.Empty(shell.Accounts);
            using var repository = new AccountRepository(_root); Assert.Equal(0, (await repository.ReadAsync(new(), default)).Total);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    });

    [Fact]
    public Task PortableImportPublishesAllowedPreferencesAndRetainsUnlockedMasterProtection() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); string path = _root + ".zenithbackup";
        try
        {
            Assert.True(await shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.EnableOrResume)!, FixtureVaultPassword));
            using var fixture = PortableMetadataFixture(); File.WriteAllBytes(path, PortableBackupCodec.Seal(fixture, FixtureVaultPassword, default));
            Assert.True(await shell.ApplyPortableBackupAsync(shell.ReviewPortableBackup(PortableBackupAction.Import, path, true)!, FixtureVaultPassword, _ => true));
            Assert.Single(shell.Accounts); Assert.True(shell.Dense); Assert.True(shell.CanDisableVaultPassword);
            Assert.Contains("paused", shell.PortableBackupStatus); Assert.Contains("paused", shell.QueueStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    });

    [Fact]
    public Task PortableBackupUnavailableInSampleAndLockedShells() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); shell.LockDashboard();
        Assert.False(shell.CanUsePortableBackup); Assert.Null(shell.ReviewPortableBackup(PortableBackupAction.Export, _root + ".zenithbackup"));
        await using var sample = new ShellViewModel(Path.Combine(_root, "sample"), true, Dispatcher.CurrentDispatcher, new PriorityDesktopMonitor(0),
            new NoPublicDirectory(), new NoServerDirectory(), previewProvider: new PreviewProvider());
        await sample.InitializeAsync(); Assert.False(sample.CanUsePortableBackup);
    });

    private static PortableBackupDocument PortableMetadataFixture() => new(1, [AccountProfile.Create("Portable metadata fixture")], [], [],
        PortableBackupPreferences.From(new SessionsSettings(DenseAccounts: true).Validated()), []);
}
