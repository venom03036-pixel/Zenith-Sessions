using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public enum PortableBackupAction { Export, Import }
public sealed record PortableBackupSummary(int Profiles, int Groups, int Favorites, int SignIns, bool ApplyPreferences);

public sealed partial class ShellViewModel
{
    private readonly object _portableReviewOwner = new();
    private PortableBackupService _portableBackup = null!;
    private bool _portableBusy, _stoppingPortable;
    private long _portableEpoch;
    private CancellationTokenSource? _portableStop;
    private Task _portableWork = Task.CompletedTask;
    private string _portableStatus = "Protected backups contain reusable saved sign-ins. Profile metadata export remains available separately.";
    public string PortableBackupStatus { get => _portableStatus; private set => Set(ref _portableStatus, value); }
    public bool PortableBackupBusy => _portableBusy;
    public bool CanUsePortableBackup => _ready && !_shuttingDown && !IsSample && !IsLocked && !_portableBusy
        && !_vaultPasswordBusy && !_vaultPasswordUnavailable && !IsAuthorizing && !_nativeLaunch.IsBusy && _pendingSaves == 0
        && _vaultPasswordMode is VaultPasswordMode.Disabled or VaultPasswordMode.Unlocked;
    private void InitializePortableBackupFeatures()
    {
        _portableBackup = new(_accounts, _vault, _linkService, _favoriteStore, _settingsStore);
        PropertyChanged += PortableBackupOwnerChanged;
    }
    private void PortableBackupOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IsReady) or nameof(IsLocked) or nameof(VaultPasswordBusy) or nameof(VaultPasswordStatusText) or nameof(IsAuthorizing) or nameof(SaveStatus))
            Raise(nameof(CanUsePortableBackup));
    }
    public sealed class PortableBackupReview
    {
        internal readonly object Owner;
        internal readonly long Epoch;
        internal readonly PortableBackupAction Action;
        internal readonly string Path;
        internal readonly bool ApplyPreferences;
        private int _consumed;
        internal PortableBackupReview(object owner, long epoch, PortableBackupAction action, string path, bool applyPreferences)
        { Owner = owner; Epoch = epoch; Action = action; Path = path; ApplyPreferences = applyPreferences; }
        internal bool Consume() => Interlocked.Exchange(ref _consumed, 1) == 0;
    }
    public PortableBackupReview? ReviewPortableBackup(PortableBackupAction action, string path, bool applyPreferences = false) =>
        CanUsePortableBackup && Enum.IsDefined(action) && !string.IsNullOrWhiteSpace(path)
        ? new(_portableReviewOwner, _portableEpoch, action, path, applyPreferences) : null;

    public Task<bool> ApplyPortableBackupAsync(PortableBackupReview review, ReadOnlyMemory<byte> password,
        Func<PortableBackupSummary, bool>? confirmImport = null)
    {
        if (!ReferenceEquals(review.Owner, _portableReviewOwner) || review.Epoch != _portableEpoch || !CanUsePortableBackup
            || password.Length is < 1 or > 1024 || review.Action == PortableBackupAction.Import && confirmImport is null || !review.Consume())
            return Task.FromResult(false);
        byte[] ownedPassword = password.ToArray();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _portableStop = stop; _portableBusy = true;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = RunPortableBackupAsync(review, ownedPassword, confirmImport, stop, published.Task); _portableWork = task;
        try { PortableBackupStatus = "Opening the protected backup operation…"; RaisePortableBackupProperties(); }
        finally { published.TrySetResult(); }
        return task;
    }
    private async Task<bool> RunPortableBackupAsync(PortableBackupReview review, byte[] password,
        Func<PortableBackupSummary, bool>? confirmImport, CancellationTokenSource stop, Task published)
    {
        await published;
        bool writerHeld = false, importStarted = false;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (review.Action == PortableBackupAction.Export)
            {
                await Task.Run(() => _portableBackup.ExportAsync(review.Path, password, DataRoot, AppContext.BaseDirectory, stop.Token), stop.Token);
                if (!PortableOwnerCurrent(review, stop)) return false;
                PortableBackupStatus = "Protected backup saved. Keep its password separately; the file contains reusable saved sign-ins.";
                Notice = PortableBackupStatus;
                return true;
            }
            using var document = await Task.Run(() => PortableBackupCodec.ReadAsync(review.Path, password, stop.Token), stop.Token);
            if (!PortableOwnerCurrent(review, stop)) return false;
            // Decryption is not consent to contact Roblox or change the library. Review decoded counts first.
            if (!confirmImport!(new(document.Profiles.Length, document.Groups.Length, document.Favorites.Length, document.SignIns.Length, review.ApplyPreferences)))
            { PortableBackupStatus = "Import cancelled before any library changes or sign-in checks."; return false; }
            if (!PortableOwnerCurrent(review, stop)) return false;
            PauseWorkForPortableImport();
            if (!PortableOwnerCurrent(review, stop)) return false;
            await _preferencesWriter.WaitAsync(stop.Token); writerHeld = true;
            importStarted = true;
            var result = await Task.Run(() => _portableBackup.ImportAsync(document, review.ApplyPreferences, stop.Token), stop.Token);
            if (!PortableOwnerCurrent(review, stop)) return false;
            await LoadAccountsAsync(stop.Token); await DestinationFavorites.LoadAsync(stop.Token);
            if (!PortableOwnerCurrent(review, stop)) return false;
            PortableBackupStatus = $"Confirmed: {result.ProfilesAdded} profiles added, {result.GroupsAdded} groups added, {result.FavoritesAdded} favorites added, {result.SignInsImported} sign-ins verified; {result.ExistingSignInsKept} existing sign-ins kept and {result.SignInsRejected} require sign-in."
                + (result.Complete ? " Import finished. Automation remains paused." : " Import stopped or only partly finished. Completed changes remain; review the library before retrying.");
            Notice = PortableBackupStatus;
            return result.Complete;
        }
        catch (OperationCanceledException)
        { if (!_shuttingDown) PortableBackupStatus = "Backup operation stopped. Any completed imports remain; review the library before retrying."; return false; }
        catch (Exception ex)
        {
            _diagnostics.RecordFailure(ManagerSubsystem.Vault, ex.GetType());
            if (!_shuttingDown) PortableBackupStatus = ex is CryptographicException
                ? "The backup password was not accepted or the file was altered. No fallback was used."
                : "The protected backup operation could not finish. Existing sign-ins were not replaced; any completed imports remain. Review the library before retrying.";
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            // A cancellation can arrive after the settings rename committed. Reconcile only the persisted allowlist
            // before releasing the settings writer, including that partial outcome; never replay imported automation.
            if (writerHeld)
            {
                try
                {
                    if (importStarted && review.ApplyPreferences && !_shuttingDown)
                        PublishPortablePreferences((await _settingsStore.LoadAsync(_lifetime.Token)).Settings);
                }
                catch (Exception ex) { _diagnostics.RecordFailure(ManagerSubsystem.Settings, ex.GetType()); PortableBackupStatus = "Import may be partial. Restart Zenith Sessions to reload its saved preferences before saving settings again."; }
                finally { _preferencesWriter.Release(); }
            }
            _portableStop = null; stop.Dispose(); _portableBusy = false; RaisePortableBackupProperties();
        }
    }
    private bool PortableOwnerCurrent(PortableBackupReview review, CancellationTokenSource stop) => review.Epoch == _portableEpoch && !stop.IsCancellationRequested && !_shuttingDown && !IsLocked;
    private void PauseWorkForPortableImport()
    {
        // This is an import pause, not global Panic: do not relock the authenticated destination vault.
        List<Exception>? failed = null;
        void Stop(Action action) { try { action(); } catch (Exception ex) { (failed ??= []).Add(ex); } }
        Stop(HoldQueuePresentation); Stop(_launchQueue.SignalPanic); Stop(MacroAssignments.CancelWork); Stop(MacroHostCatalog.CancelWork);
        Stop(StopKeepAwake); Stop(StopClientArrivalLayout);
        _queuePaused = true; Raise(nameof(QueuePauseLabel)); QueueStatus = "Queue paused for protected import. Resume only after reviewing the library.";
        if (failed is not null) throw new AggregateException("Import could not pause all owned work.", failed);
    }
    private void PublishPortablePreferences(SessionsSettings stored)
    {
        // The import service kept all local trust/security settings. Apply only its explicitly allowed preferences.
        _savedSettings = stored;
        _settings = _settings with { Theme = stored.Theme, DenseAccounts = stored.DenseAccounts, Capacity = stored.Capacity,
            Performance = stored.Performance, WindowLayout = stored.WindowLayout, CloseButtonExits = stored.CloseButtonExits };
        var limits = stored.Capacity!;
        _spareRam = limits.SparePhysicalGiB.ToString(CultureInfo.CurrentCulture);
        _physicalPerClient = limits.NewClientPhysicalGiB.ToString(CultureInfo.CurrentCulture);
        _commitPerClient = limits.NewClientCommitGiB.ToString(CultureInfo.CurrentCulture);
        _maximumClients = limits.UserMaximum?.ToString(CultureInfo.CurrentCulture) ?? ""; _closeButtonExits = stored.CloseButtonExits;
        LoadPerformanceDraft(stored.Performance!); ApplyPerformance(stored.Performance!); LoadClientLayoutDraft(stored.WindowLayout!); ApplyEffectiveTheme();
        foreach (string name in new[] { nameof(Dense), nameof(ThemeName), nameof(SpareRam), nameof(PhysicalPerClient), nameof(CommitPerClient), nameof(MaximumClients), nameof(CloseButtonExits), nameof(CloseButtonLabel) }) Raise(name);
        PreferencesEdited();
    }
    private void RaisePortableBackupProperties() { Raise(nameof(PortableBackupBusy)); Raise(nameof(CanUsePortableBackup)); RaiseVaultPasswordProperties(); }
    public void CancelPortableBackup() => StopPortableBackup();
    private void StopPortableBackup()
    {
        if (_stoppingPortable) return;
        _stoppingPortable = true;
        try { _portableEpoch++; _portableStop?.Cancel(); }
        finally { _stoppingPortable = false; }
    }
    private async Task DisposePortableBackupAsync()
    {
        StopShutdownStep(StopPortableBackup);
        try { await _portableWork; }
        finally { PropertyChanged -= PortableBackupOwnerChanged; }
    }
}
