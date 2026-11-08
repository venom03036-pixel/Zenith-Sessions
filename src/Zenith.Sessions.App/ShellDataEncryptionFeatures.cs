using System.ComponentModel;
using System;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private IDataFolderEncryption _dataEncryption = null!;
    private bool _dataEncryptionPermitted, _encryptingData;
    private long _dataEncryptionGeneration;
    private readonly object _dataEncryptionReviewOwner = new();
    private CancellationTokenSource? _dataEncryptionStop;
    private Task _dataEncryptionWork = Task.CompletedTask;
    private string _dataEncryptionStatus = "Optional Windows EFS is not checked. Saved sign-ins already use Windows DPAPI.";
    public string DataEncryptionStatus { get => _dataEncryptionStatus; private set => Set(ref _dataEncryptionStatus, value); }
    public bool CanEncryptDataFolder => _dataEncryptionPermitted && CanUseDirectories() && !_encryptingData;
    public bool IsEncryptingDataFolder => _encryptingData;
    public AsyncCommand EncryptDataFolder { get; private set; } = null!;
    public Command CancelDataEncryption { get; private set; } = null!;
    public sealed class DataEncryptionReview
    {
        internal long Generation { get; }
        private readonly object _owner;
        private int _consumed;
        internal DataEncryptionReview(object owner, long generation) { _owner = owner; Generation = generation; }
        internal bool Consume(object owner) => ReferenceEquals(owner, _owner) && Interlocked.Exchange(ref _consumed, 1) == 0;
    }
    public const string DataEncryptionConfirmation = "Encrypt this library's data folder with Windows EFS?\n\n"
        + "This opt-in operation protects file contents using your Windows EFS certificate. Windows edition, filesystem and policy support are required. "
        + "Back up your EFS certificate using Windows and keep that backup safe: losing its private key can make the whole library unreadable, including account metadata. "
        + "Existing DPAPI sign-in protection stays in place.\n\n"
        + "Applications running as you can read these files. Copies and exports made while signed in may be decrypted; this is not a portable encrypted backup. "
        + "Other certificate holders and configured recovery agents may have access.\n\n"
        + "The operation can finish partially if files are open. Cancel, Panic or Lock stops between files; completed encryption is kept. "
        + "No automatic decryption or rollback is provided. Continue?";

    private void InitializeDataEncryption(IDataFolderEncryption? encryption)
    {
        _dataEncryptionPermitted = !IsSample || encryption is not null;
        _dataEncryption = encryption ?? new WindowsDataFolderEncryption(DataRoot);
        EncryptDataFolder = new(EncryptDataFolderAsync, ex => Fail(ex, ManagerSubsystem.Library));
        CancelDataEncryption = new(_ => StopDataEncryption());
        PropertyChanged += DataEncryptionOwnerChanged;
    }
    private void DataEncryptionOwnerChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(IsReady) or nameof(IsLocked)) Raise(nameof(CanEncryptDataFolder)); }
    public DataEncryptionReview? ReviewDataEncryption() => CanEncryptDataFolder ? new(_dataEncryptionReviewOwner, _dataEncryptionGeneration) : null;

    private Task EncryptDataFolderAsync(object? parameter)
    {
        if (parameter is not DataEncryptionReview review || review.Generation != _dataEncryptionGeneration || !CanEncryptDataFolder
            || !review.Consume(_dataEncryptionReviewOwner))
            return Task.CompletedTask;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _dataEncryptionStop = stop;
        _encryptingData = true;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dataEncryptionWork = RunDataEncryptionAsync(review.Generation, stop, published.Task);
        try
        {
            DataEncryptionStatus = "Applying Windows EFS and checking files. Cancel stops after the current Windows call.";
            Raise(nameof(CanEncryptDataFolder)); Raise(nameof(IsEncryptingDataFolder));
        }
        finally { published.TrySetResult(); }
        return _dataEncryptionWork;
    }
    private async Task RunDataEncryptionAsync(long generation, CancellationTokenSource stop, Task published)
    {
        await published;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            var result = await _dataEncryption.EncryptAsync(stop.Token);
            if (stop.IsCancellationRequested || generation != _dataEncryptionGeneration || !CanUseDirectories()) return;
            DataEncryptionStatus = DescribeDataEncryption(result);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _diagnostics.RecordFailure(ManagerSubsystem.Library, ex.GetType());
            if (generation == _dataEncryptionGeneration && CanUseDirectories())
                DataEncryptionStatus = "Windows EFS could not finish. Some files may be encrypted. Keep the folder and retry after checking Windows EFS support and access.";
        }
        finally
        {
            bool stopped = stop.IsCancellationRequested;
            _dataEncryptionStop = null;
            stop.Dispose(); _encryptingData = false;
            if (stopped) DataEncryptionStatus = DescribeDataEncryption(new(DataFolderEncryptionState.Stopped, 0, 0, DataFolderEncryptionProblem.None));
            Raise(nameof(CanEncryptDataFolder)); Raise(nameof(IsEncryptingDataFolder));
        }
    }
    internal static string DescribeDataEncryption(DataFolderEncryptionResult result) => result.State switch
    {
        DataFolderEncryptionState.Verified => $"Windows EFS verified on {result.VerifiedEntries} files/folders at this check. Future copies, exports and externally replaced files need their own protection check.",
        DataFolderEncryptionState.Partial => $"Windows EFS is incomplete: {result.VerifiedEntries} entries verified, {result.FailedEntries} checks failed. Encrypted files are kept. Close other users of this folder and retry; Windows edition, filesystem or policy may prevent completion.",
        DataFolderEncryptionState.Stopped => "Windows EFS stopped. Some files may already be encrypted; nothing was decrypted. Retry to complete and check the folder.",
        DataFolderEncryptionState.Busy => "A Windows EFS operation is already running. Wait for it to finish.",
        DataFolderEncryptionState.Rejected => "Windows EFS was not applied: the folder contains an unsafe linked path or exceeds the inspection limit. Keep the folder for review.",
        _ => "Windows EFS is unavailable for this folder. Check that your Windows edition, filesystem and policies support EFS and that files are accessible. Saved sign-ins still use DPAPI."
    };
    private void StopDataEncryption()
    {
        _dataEncryptionGeneration++;
        _dataEncryptionStop?.Cancel();
        if (_encryptingData) DataEncryptionStatus = "Windows EFS is stopping after the current Windows call. Some files may be encrypted; nothing is decrypted. Retry only after it finishes.";
    }
    private async Task DisposeDataEncryptionAsync()
    {
        StopDataEncryption();
        await _dataEncryptionWork;
        PropertyChanged -= DataEncryptionOwnerChanged;
    }
}
