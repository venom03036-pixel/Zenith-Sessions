using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests
{
    private static readonly byte[] FixtureVaultPassword = "synthetic shell password 3"u8.ToArray();

    [Fact]
    public Task MasterPasswordRequiresCurrentConsentAndUnlockCannotSkipAuthentication() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell();
        var stale = shell.ReviewVaultPassword(VaultPasswordAction.EnableOrResume)!;
        shell.Panic.Execute(null);
        Assert.False(await shell.ApplyVaultPasswordAsync(stale, FixtureVaultPassword));
        var accepted = shell.ReviewVaultPassword(VaultPasswordAction.EnableOrResume)!;
        Assert.True(await shell.ApplyVaultPasswordAsync(accepted, FixtureVaultPassword));
        Assert.False(await shell.ApplyVaultPasswordAsync(accepted, FixtureVaultPassword));
        Assert.True(shell.CanDisableVaultPassword);
        shell.LockDashboard(); Assert.True(shell.IsLocked);
        VaultPasswordAction? prompt = null; shell.VaultPasswordPromptRequested += action => prompt = action;
        shell.Unlock.Execute(null); await shell.Unlock.Execution; Assert.True(shell.IsLocked); Assert.Equal(VaultPasswordAction.Unlock, prompt);
        Assert.False(await shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.Unlock)!, "wrong fixture password"u8.ToArray()));
        Assert.True(shell.IsLocked);
        Assert.True(await shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.Unlock)!, FixtureVaultPassword));
        Assert.False(shell.IsLocked); Assert.Contains("unlocked", shell.VaultPasswordStatusText);
    });

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("cancel")]
    public Task MasterPasswordStopFencesLateUnlockAndRetainsTheOwnedWorker(string action) => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell();
        Assert.True(await shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.EnableOrResume)!, FixtureVaultPassword));
        shell.LockDashboard();
        using var held = new FileStream(Path.Combine(_root, "accounts.vault", ProtectedCredentialVault.WriterLockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Task<bool> unlock = shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.Unlock)!, FixtureVaultPassword);
        Assert.True(shell.VaultPasswordBusy);
        if (action == "panic") shell.Panic.Execute(null); else if (action == "lock") shell.LockDashboard(); else shell.CancelVaultPasswordOperation();
        Assert.True(shell.VaultPasswordBusy); held.Dispose();
        Assert.False(await unlock); Assert.True(shell.IsLocked); Assert.False(shell.VaultPasswordBusy);
        Assert.Equal("Unlock saved sign-ins…", shell.VaultPasswordPrimaryLabel);
    });

    [Fact]
    public Task MasterPasswordForgottenRecoveryHasItsOwnConsentAndLeavesAutomationStopped() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell();
        Assert.True(await shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.EnableOrResume)!, FixtureVaultPassword));
        shell.LockDashboard(); Assert.True(shell.CanForgetProtectedSignIns);
        var review = shell.ReviewVaultPassword(VaultPasswordAction.ForgetSignIns)!;
        Assert.True(await shell.ApplyVaultPasswordAsync(review, ReadOnlyMemory<byte>.Empty));
        Assert.False(shell.IsLocked); Assert.False(shell.CanForgetProtectedSignIns);
        Assert.Contains("removed", shell.Notice); Assert.Contains("off", shell.VaultPasswordStatusText);
        Assert.False(await shell.ApplyVaultPasswordAsync(review, ReadOnlyMemory<byte>.Empty));
    });

    [Fact]
    public Task MasterPasswordPanicDuringNotificationCannotStartProtection() => OnUi(async () =>
    {
        await using var shell = await ReadyPasswordShell();
        var review = shell.ReviewVaultPassword(VaultPasswordAction.EnableOrResume)!;
        shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.VaultPasswordBusy) && shell.VaultPasswordBusy) shell.Panic.Execute(null); };
        Assert.False(await shell.ApplyVaultPasswordAsync(review, FixtureVaultPassword));
        Assert.False(File.Exists(Path.Combine(_root, "accounts.vault", ProtectedCredentialVault.PasswordControlName)));
    });

    [Fact]
    public Task MasterPasswordCorruptControlStillOffersExplicitRecoveryAfterStartup() => OnUi(async () =>
    {
        using (var vault = new ProtectedCredentialVault(_root)) await vault.EnableMasterPasswordAsync(FixtureVaultPassword, default);
        File.WriteAllBytes(Path.Combine(_root, "accounts.vault", ProtectedCredentialVault.PasswordControlName), new byte[20]);
        await using var shell = await ReadyPasswordShell();
        Assert.True(shell.CanForgetProtectedSignIns); Assert.False(shell.CanUseVaultPasswordPrimary);
        Assert.True(await shell.ApplyVaultPasswordAsync(shell.ReviewVaultPassword(VaultPasswordAction.ForgetSignIns)!, ReadOnlyMemory<byte>.Empty));
        Assert.Contains("off", shell.VaultPasswordStatusText);
    });

    private async Task<ShellViewModel> ReadyPasswordShell()
    {
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new PriorityDesktopMonitor(0),
            new NoPublicDirectory(), new NoServerDirectory(), previewProvider: new PreviewProvider());
        try { await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice); return shell; }
        catch { await shell.DisposeAsync(); throw; }
    }
}
