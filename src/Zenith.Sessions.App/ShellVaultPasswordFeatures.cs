using System;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public enum VaultPasswordAction { EnableOrResume, Unlock, Disable, ForgetSignIns }

public sealed partial class ShellViewModel
{
    private readonly object _vaultPasswordReviewOwner = new();
    private VaultPasswordMode? _vaultPasswordMode;
    private bool _vaultPasswordBusy, _vaultPasswordUnavailable, _stoppingVaultPassword;
    private long _vaultPasswordEpoch;
    private CancellationTokenSource? _vaultPasswordStop;
    private Task _vaultPasswordWork = Task.CompletedTask;
    private string _vaultPasswordStatus = "Checking saved-sign-in protection…";
    public string VaultPasswordStatusText { get => _vaultPasswordStatus; private set => Set(ref _vaultPasswordStatus, value); }
    public bool VaultPasswordBusy => _vaultPasswordBusy;
    public string VaultPasswordPrimaryLabel => _vaultPasswordMode == VaultPasswordMode.MigrationRequired ? "Resume password encryption…"
        : _vaultPasswordMode == VaultPasswordMode.Locked ? "Unlock saved sign-ins…" : "Enable master password…";
    public bool CanUseVaultPasswordPrimary => !_vaultPasswordUnavailable && !_vaultPasswordBusy && !_portableBusy && _ready && !_shuttingDown && !IsSample
        && _vaultPasswordMode is VaultPasswordMode.Disabled or VaultPasswordMode.Locked or VaultPasswordMode.MigrationRequired;
    public bool CanDisableVaultPassword => !_vaultPasswordUnavailable && !_vaultPasswordBusy && !_portableBusy && _ready && !_shuttingDown && !IsSample && !IsLocked && _vaultPasswordMode == VaultPasswordMode.Unlocked;
    public bool CanForgetProtectedSignIns => !_vaultPasswordBusy && !_portableBusy && _ready && !_shuttingDown && !IsSample
        && (_vaultPasswordUnavailable || _vaultPasswordMode is VaultPasswordMode.Locked or VaultPasswordMode.Unlocked or VaultPasswordMode.MigrationRequired);
    public event Action<VaultPasswordAction>? VaultPasswordPromptRequested;
    private void InitializeVaultPasswordFeatures() => PropertyChanged += VaultPasswordOwnerChanged;
    private void VaultPasswordOwnerChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(IsReady) or nameof(IsLocked)) RaiseVaultPasswordProperties(); }

    public sealed class VaultPasswordReview
    {
        internal readonly object Owner;
        internal readonly long Epoch;
        internal readonly VaultPasswordAction Action;
        private int _consumed;
        internal VaultPasswordReview(object owner, long epoch, VaultPasswordAction action) { Owner = owner; Epoch = epoch; Action = action; }
        internal bool Consume() => Interlocked.Exchange(ref _consumed, 1) == 0;
    }
    public VaultPasswordAction PrimaryVaultPasswordAction => _vaultPasswordMode == VaultPasswordMode.Locked ? VaultPasswordAction.Unlock : VaultPasswordAction.EnableOrResume;
    public VaultPasswordReview? ReviewVaultPassword(VaultPasswordAction action) => VaultPasswordAllowed(action)
        ? new(_vaultPasswordReviewOwner, _vaultPasswordEpoch, action) : null;
    private bool VaultPasswordAllowed(VaultPasswordAction action) => action switch
    {
        VaultPasswordAction.Disable => CanDisableVaultPassword,
        VaultPasswordAction.ForgetSignIns => CanForgetProtectedSignIns,
        VaultPasswordAction.Unlock => CanUseVaultPasswordPrimary && _vaultPasswordMode == VaultPasswordMode.Locked,
        VaultPasswordAction.EnableOrResume => CanUseVaultPasswordPrimary && _vaultPasswordMode is VaultPasswordMode.Disabled or VaultPasswordMode.MigrationRequired,
        _ => false
    };

    public Task<bool> ApplyVaultPasswordAsync(VaultPasswordReview review, ReadOnlyMemory<byte> password)
    {
        if (review.Action != VaultPasswordAction.ForgetSignIns && (password.Length is < 1 or > 1024)) return Task.FromResult(false);
        if (!ReferenceEquals(review.Owner, _vaultPasswordReviewOwner) || review.Epoch != _vaultPasswordEpoch
            || !VaultPasswordAllowed(review.Action) || !review.Consume()) return Task.FromResult(false);
        if (review.Action == VaultPasswordAction.ForgetSignIns) StopOperations("Saved-sign-in recovery: automation stopped; clients remain open.");
        long epoch = _vaultPasswordEpoch;
        byte[] ownedPassword = review.Action == VaultPasswordAction.ForgetSignIns ? [] : password.ToArray();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _vaultPasswordStop = stop; _vaultPasswordBusy = true;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = RunVaultPasswordAsync(review.Action, ownedPassword, epoch, stop, published.Task);
        _vaultPasswordWork = task;
        try { VaultPasswordStatusText = "Updating saved-sign-in protection…"; RaiseVaultPasswordProperties(); }
        finally { published.TrySetResult(); }
        return task;
    }
    private async Task<bool> RunVaultPasswordAsync(VaultPasswordAction action, byte[] password, long epoch, CancellationTokenSource stop, Task published)
    {
        await published;
        bool completed = false;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            switch (action)
            {
                case VaultPasswordAction.EnableOrResume: await _vault.EnableMasterPasswordAsync(password, stop.Token); break;
                case VaultPasswordAction.Unlock: await _vault.UnlockMasterPasswordAsync(password, stop.Token); break;
                case VaultPasswordAction.Disable: await _vault.DisableMasterPasswordAsync(password, stop.Token); break;
                case VaultPasswordAction.ForgetSignIns: await _vault.ForgetMasterProtectedSignInsAsync(stop.Token); break;
            }
            if (epoch != _vaultPasswordEpoch || stop.IsCancellationRequested || _shuttingDown) return false;
            await RefreshVaultPasswordStateAsync();
            if (epoch != _vaultPasswordEpoch || stop.IsCancellationRequested || _shuttingDown) return false;
            if (action is VaultPasswordAction.Unlock or VaultPasswordAction.EnableOrResume)
            {
                // Locked startup preserves projection journals; replay only after explicit authentication.
                await Task.Run(() => _identityProjection.ReplayAsync(stop.Token), stop.Token);
                if (epoch != _vaultPasswordEpoch || stop.IsCancellationRequested || _shuttingDown) return false;
                await LoadAccountsAsync(stop.Token);
            }
            if (epoch != _vaultPasswordEpoch || stop.IsCancellationRequested || _shuttingDown) return false;
            if (IsLocked && action is VaultPasswordAction.Unlock or VaultPasswordAction.EnableOrResume or VaultPasswordAction.ForgetSignIns)
                CompleteDashboardUnlock();
            completed = true;
            if (action == VaultPasswordAction.ForgetSignIns)
            {
                await LoadAccountsAsync(_lifetime.Token);
                if (epoch == _vaultPasswordEpoch && !_shuttingDown) Notice = "Saved sign-ins removed. Profiles and running clients remain; sign in again before launching.";
            }
            return true;
        }
        catch (OperationCanceledException) { VaultPasswordStatusText = "Password operation stopped. Any completed protection changes remain; check the current vault state before retrying."; return false; }
        catch (Exception ex)
        {
            _diagnostics.RecordFailure(ManagerSubsystem.Vault, ex.GetType());
            VaultPasswordStatusText = ex switch
            {
                CryptographicException => "The password was not accepted or protected control data is damaged. Originals were preserved; no fallback key was used.",
                ArgumentException => "Use a password of at least 12 UTF-8 bytes and at most 1,024 bytes when enabling protection.",
                VaultPasswordMigrationException => "Encryption is incomplete. Resume with the same password; originals are preserved.",
                VaultPasswordLegacyMigrationException => "An older saved sign-in already needs reauthentication. Sign in again or explicitly remove that old sign-in before enabling a master password; its original file was kept.",
                _ => "The password operation could not finish. Keep the vault for recovery; some records may already have been converted."
            };
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password); _vaultPasswordStop = null; stop.Dispose(); _vaultPasswordBusy = false;
            if (!completed && !_shuttingDown) await RefreshVaultPasswordStateAsync(preserveMessage: true);
            RaiseVaultPasswordProperties();
        }
    }
    private async Task RefreshVaultPasswordStateAsync(bool preserveMessage = false)
    {
        try
        {
            _vaultPasswordMode = (await _vault.GetPasswordStatusAsync(_lifetime.Token)).Mode; _vaultPasswordUnavailable = false;
            if (!preserveMessage) VaultPasswordStatusText = _vaultPasswordMode switch
            {
                VaultPasswordMode.Disabled => "Master password is off. Saved sign-ins use Windows DPAPI.",
                VaultPasswordMode.Locked => "Master-password vault is locked. Account metadata remains available; unlock before using saved sign-ins.",
                VaultPasswordMode.Unlocked => "Master-password vault is unlocked. Manual, Windows and idle lock clear its in-memory key.",
                _ => "Password encryption was interrupted. Resume with the same password; saved sign-ins cannot be used until it finishes."
            };
        }
        catch (Exception ex)
        {
            _vaultPasswordUnavailable = true;
            if (ex is not OperationCanceledException) _diagnostics.RecordFailure(ManagerSubsystem.Vault, ex.GetType());
            if (!preserveMessage) VaultPasswordStatusText = "Saved-sign-in protection is unavailable. Keep the vault for recovery, or explicitly forget all saved sign-ins and sign in again.";
        }
        RaiseVaultPasswordProperties();
    }
    private void RaiseVaultPasswordProperties()
    {
        Raise(nameof(VaultPasswordBusy)); Raise(nameof(VaultPasswordPrimaryLabel)); Raise(nameof(CanUseVaultPasswordPrimary));
        Raise(nameof(CanDisableVaultPassword)); Raise(nameof(CanForgetProtectedSignIns));
    }
    public void CancelVaultPasswordOperation() => StopVaultPasswordWork();
    private void StopVaultPasswordWork()
    {
        if (_stoppingVaultPassword) return;
        _stoppingVaultPassword = true;
        try
        {
        _vaultPasswordEpoch++;
        try { _vaultPasswordStop?.Cancel(); }
        finally
        {
            _vault.LockMasterPassword();
            if (_vaultPasswordMode == VaultPasswordMode.Unlocked) _vaultPasswordMode = VaultPasswordMode.Locked;
            if (_vaultPasswordMode == VaultPasswordMode.Locked) VaultPasswordStatusText = "Master-password vault is locked. Unlock before using saved sign-ins.";
            RaiseVaultPasswordProperties();
        }
        }
        finally { _stoppingVaultPassword = false; }
    }
    private void RequestDashboardUnlock()
    {
        if (_vaultPasswordMode is VaultPasswordMode.Locked or VaultPasswordMode.MigrationRequired)
        { VaultPasswordPromptRequested?.Invoke(PrimaryVaultPasswordAction); return; }
        if (_vaultPasswordMode == VaultPasswordMode.Unlocked || _vaultPasswordMode == VaultPasswordMode.Disabled || _vaultPasswordUnavailable)
            CompleteDashboardUnlock();
    }
    private void CompleteDashboardUnlock()
    {
        bool wasLocked = IsLocked; IsLocked = false;
        if (wasLocked) _diagnostics.Record(ManagerEventKind.ControlsUnlocked);
        ReloadFavoritesAfterUnlock(); Notice = "Dashboard unlocked. Automation remains stopped until explicitly started.";
        RestartIdleLock(); ApplySavedClientUpdateCheck(restart:true); LoadClientDisplaysOnce(); RaiseVaultPasswordProperties();
    }
    private async Task DisposeVaultPasswordAsync()
    {
        StopShutdownStep(StopVaultPasswordWork);
        try { await _vaultPasswordWork; }
        finally { PropertyChanged -= VaultPasswordOwnerChanged; }
    }
}
