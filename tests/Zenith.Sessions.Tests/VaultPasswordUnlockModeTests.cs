using System.IO;
using System.Security.Cryptography;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests
{
    [Fact]
    public Task DashboardUnlockRefreshesPasswordEnabledByAnotherVaultOwner() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell();
        using (var other = new ProtectedCredentialVault(_root))
            await other.EnableMasterPasswordAsync(FixtureVaultPassword, default);
        shell.LockDashboard();
        VaultPasswordAction? prompt = null;
        shell.VaultPasswordPromptRequested += action =>
        {
            Assert.False(shell.VaultPasswordBusy);
            Assert.NotNull(shell.ReviewVaultPassword(action)); // MainWindow needs admission at delivery.
            prompt = action;
        };
        await ExecuteDashboardUnlock(shell);
        Assert.True(shell.IsLocked);
        Assert.Equal(VaultPasswordAction.Unlock, prompt);
        Assert.Equal("Unlock saved sign-ins…", shell.VaultPasswordPrimaryLabel);
        Assert.True(await shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.Unlock)!, FixtureVaultPassword));
        Assert.False(shell.IsLocked);
    });

    [Fact]
    public Task DashboardUnlockRefreshesPasswordDisabledByAnotherVaultOwner() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell();
        Assert.True(await shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.EnableOrResume)!, FixtureVaultPassword));
        shell.LockDashboard();
        using (var other = new ProtectedCredentialVault(_root))
        {
            await other.UnlockMasterPasswordAsync(FixtureVaultPassword, default);
            await other.DisableMasterPasswordAsync(FixtureVaultPassword, default);
        }
        bool prompted = false; shell.VaultPasswordPromptRequested += _ => prompted = true;
        await ExecuteDashboardUnlock(shell);
        Assert.False(shell.IsLocked); Assert.False(prompted);
        Assert.Contains("off", shell.VaultPasswordStatusText);
    });

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("cancel")]
    public Task DashboardUnlockStopAtCommandNotificationRejectsTheOldClick(string action) => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); shell.LockDashboard();
        bool stopped = false, prompted = false;
        shell.VaultPasswordPromptRequested += _ => prompted = true;
        shell.Unlock.CanExecuteChanged += (_, _) =>
        {
            if (shell.Unlock.CanExecute(null) || stopped) return;
            stopped = true;
            if (action == "panic") shell.Panic.Execute(null);
            else if (action == "lock") shell.LockDashboard();
            else shell.CancelVaultPasswordOperation();
        };
        await ExecuteDashboardUnlock(shell);
        Assert.True(stopped); Assert.True(shell.IsLocked); Assert.False(prompted); Assert.False(shell.VaultPasswordBusy);
    });

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("cancel")]
    public Task DashboardUnlockModeReadStoppedDuringPublicationCannotReopenControls(string action) => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); shell.LockDashboard();
        bool stopped = false, prompted = false;
        shell.VaultPasswordPromptRequested += _ => prompted = true;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ShellViewModel.VaultPasswordBusy) || shell.VaultPasswordBusy || stopped) return;
            stopped = true;
            if (action == "panic") shell.Panic.Execute(null);
            else if (action == "lock") shell.LockDashboard();
            else shell.CancelVaultPasswordOperation();
        };
        await ExecuteDashboardUnlock(shell);
        Assert.True(stopped); Assert.True(shell.IsLocked); Assert.False(prompted);
    });

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("cancel")]
    public Task DashboardUnlockModeReadHasOneCancellableOwnerWhileVaultLeaseIsHeld(string action) => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); shell.LockDashboard();
        using var held = new FileStream(Path.Combine(_root, "accounts.vault", ProtectedCredentialVault.WriterLockFileName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        bool prompted = false; shell.VaultPasswordPromptRequested += _ => prompted = true;
        Task unlock = ExecuteDashboardUnlock(shell);
        try
        {
            Assert.True(shell.VaultPasswordBusy); Assert.False(unlock.IsCompleted);
            Assert.Null(shell.ReviewVaultPassword(VaultPasswordAction.EnableOrResume));
            shell.Unlock.Execute(null); // A second click must not replace the owner.
            if (action == "panic") shell.Panic.Execute(null);
            else if (action == "lock") shell.LockDashboard();
            else shell.CancelVaultPasswordOperation();
        }
        finally { held.Dispose(); await unlock; }
        Assert.True(shell.IsLocked); Assert.False(prompted); Assert.False(shell.VaultPasswordBusy);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DashboardUnlockUnavailableControlStillOffersMetadataRecovery(bool previouslyProtected) => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell();
        if (previouslyProtected)
            Assert.True(await shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.EnableOrResume)!, FixtureVaultPassword));
        shell.LockDashboard();
        File.WriteAllBytes(Path.Combine(_root, "accounts.vault", ProtectedCredentialVault.PasswordControlName), new byte[20]);
        await ExecuteDashboardUnlock(shell);
        Assert.False(shell.IsLocked); Assert.True(shell.CanForgetProtectedSignIns);
        Assert.False(shell.CanUseVaultPasswordPrimary); Assert.Contains("unavailable", shell.VaultPasswordStatusText);
        using var other = new ProtectedCredentialVault(_root);
        await Assert.ThrowsAsync<CryptographicException>(() => other.GetPasswordStatusAsync(default));
    });

    [Fact]
    public Task DashboardUnlockModeReadIsCancelledAndJoinedOnExit() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); shell.LockDashboard();
        using var held = new FileStream(Path.Combine(_root, "accounts.vault", ProtectedCredentialVault.WriterLockFileName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        bool prompted = false; shell.VaultPasswordPromptRequested += _ => prompted = true;
        Task unlock = ExecuteDashboardUnlock(shell); Task? closing = null;
        try
        {
            Assert.True(shell.VaultPasswordBusy); Assert.False(unlock.IsCompleted);
            closing = shell.DisposeAsync().AsTask();
        }
        finally { held.Dispose(); await unlock; if (closing is not null) await closing; }
        Assert.True(shell.IsLocked); Assert.False(prompted); Assert.False(shell.VaultPasswordBusy);
    });

    [Fact]
    public Task DashboardUnlockPublicationCannotReplaceANewerPasswordOwner() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell(); shell.LockDashboard();
        bool started = false; FileStream? held = null; Task<bool>? protection = null;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ShellViewModel.VaultPasswordBusy) || shell.VaultPasswordBusy || started) return;
            started = true;
            held = new FileStream(Path.Combine(_root, "accounts.vault", ProtectedCredentialVault.WriterLockFileName),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            protection = shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.EnableOrResume)!, FixtureVaultPassword);
        };
        try
        {
            await ExecuteDashboardUnlock(shell);
            Assert.NotNull(protection); Assert.True(shell.VaultPasswordBusy); Assert.True(shell.IsLocked);
            Assert.False(protection.IsCompleted);
        }
        finally
        {
            shell.CancelVaultPasswordOperation(); held?.Dispose();
            if (protection is not null) Assert.False(await protection);
        }
        Assert.True(shell.IsLocked); Assert.False(shell.VaultPasswordBusy);
    });

    // Allows the same regression to execute against the old synchronous Command before the repair.
    private static async Task ExecuteDashboardUnlock(ShellViewModel shell)
    {
        shell.Unlock.Execute(null);
        if ((object)shell.Unlock is AsyncCommand command) await command.Execution;
    }
}
