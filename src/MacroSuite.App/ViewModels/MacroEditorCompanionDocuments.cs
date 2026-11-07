#if DEVELOPER_MODE
using MacroSuite.Core.Storage;
using Zenith.MacroHost.Execution;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel : ISavedMacroDocumentSource, IAsyncDisposable
{
    private Task? _companionReadTask;
    private object? _companionReadOwner;
    private CancellationTokenSource? _companionReadCancellation;

    /// <summary>Host-local exact-document reads. This source is never exposed through the metadata pipe.</summary>
    public ISavedMacroDocumentSource CompanionDocuments => this;

    /// <summary>Async owners must join document I/O before disposing shared services. Dispose alone only fences it.</summary>
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        Dispose();
    }

    Task<SavedMacroDocumentReadResult> ISavedMacroDocumentSource.ReadAsync(SavedMacroReference reference, CancellationToken ct)
    {
        lock (_companionCatalogSync)
        {
            SavedMacroDocumentReadStatus? failure = _companionClosed ? SavedMacroDocumentReadStatus.Closed
                : ct.IsCancellationRequested ? SavedMacroDocumentReadStatus.Cancelled
                : !reference.IsValid || reference.HostSessionId != _companionCatalog.HostSessionId ? SavedMacroDocumentReadStatus.Changed
                : _libraryLoadDeferred || !_libraryInitialized || _companionLoad is not null ? SavedMacroDocumentReadStatus.Starting
                : _companionResolutionQuarantined || _companionLoadFailed ? SavedMacroDocumentReadStatus.Unavailable
                : _companionWrites != 0 || _companionPending != 0 || _companionReadOwner is not null
                    || _companionReadTask is { IsCompleted: false } ? SavedMacroDocumentReadStatus.Busy
                : null;
            if (failure is { } status) return Task.FromResult(new SavedMacroDocumentReadResult(status));
            if (!_companionSaved.TryGetValue(reference.MacroId, out var entry))
                return Task.FromResult(new SavedMacroDocumentReadResult(SavedMacroDocumentReadStatus.NotFound));
            if (entry.Descriptor.SavedRevision != reference.SavedRevision)
                return Task.FromResult(new SavedMacroDocumentReadResult(SavedMacroDocumentReadStatus.Changed));
            if (entry.Path is null || entry.Receipt is null)
                return Task.FromResult(new SavedMacroDocumentReadResult(SavedMacroDocumentReadStatus.Unavailable));

            var owner = new object();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _companionReadOwner = owner; _companionReadCancellation = stop;
            var work = ReadCompanionDocumentAsync(reference, entry, _companionEpoch, _companionMutation, owner, stop, start.Task);
            // Track actual completion without retaining the returned pin/body in the source.
            var completion = JoinCompanionReadAsync(work);
            _companionReadTask = completion;
            var published = PublishJoinedCompanionReadAsync(work, completion);
            start.SetResult();
            return published;
        }
    }

    private bool CompanionEntryCurrentLocked(CompanionSavedEntry entry, Guid epoch, Guid mutation)
        => !_companionClosed && !_companionResolutionQuarantined && !_companionLoadFailed && _companionLoad is null
            && _companionEpoch == epoch && _companionMutation == mutation && _companionWrites == 0 && _companionPending == 0
            && _companionSaved.TryGetValue(entry.Descriptor.Id, out var current) && ReferenceEquals(entry, current);

    private async Task<SavedMacroDocumentReadResult> ReadCompanionDocumentAsync(SavedMacroReference reference,
        CompanionSavedEntry entry, Guid epoch, Guid mutation, object owner, CancellationTokenSource stop, Task start)
    {
        await start.ConfigureAwait(false);
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            // Cancellation fences publication, but never abandons the actual noncancellable document I/O.
            MacroDocumentLoadResult loaded = await Task.Run(() =>
            {
                stop.Token.ThrowIfCancellationRequested();
                return _documentStore.Load(entry.Path!);
            }, CancellationToken.None).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            if (loaded.IsLegacy || loaded.Models.Count != 1 || loaded.Model.Id != reference.MacroId
                || loaded.Receipt is null || loaded.Receipt.Validated() != entry.Receipt
                || !System.IO.Path.GetFullPath(loaded.SourcePath).Equals(entry.Path, StringComparison.OrdinalIgnoreCase))
                return new(SavedMacroDocumentReadStatus.Changed);
            lock (_companionCatalogSync)
            {
                if (_companionClosed) return new(SavedMacroDocumentReadStatus.Closed);
                stop.Token.ThrowIfCancellationRequested();
                if (!CompanionEntryCurrentLocked(entry, epoch, mutation)) return new(SavedMacroDocumentReadStatus.Changed);
                var pin = SavedMacroDocumentPin.Create(reference, new(loaded.Model, loaded.Receipt), () =>
                {
                    lock (_companionCatalogSync) return CompanionEntryCurrentLocked(entry, epoch, mutation);
                });
                return new(SavedMacroDocumentReadStatus.Ready, pin);
            }
        }
        catch (OperationCanceledException) { return new(SavedMacroDocumentReadStatus.Cancelled); }
        catch (Exception) { return new(SavedMacroDocumentReadStatus.Unavailable); }
        finally
        {
            stop.Dispose();
            lock (_companionCatalogSync)
            {
                if (ReferenceEquals(owner, _companionReadOwner))
                { _companionReadOwner = null; _companionReadCancellation = null; }
            }
        }
    }

    private static async Task JoinCompanionReadAsync(Task<SavedMacroDocumentReadResult>? work)
    {
        try { await work!.ConfigureAwait(false); }
        finally { work = null; } // The completion handle keeps no decrypted result alive after the caller releases it.
    }

    private static async Task<SavedMacroDocumentReadResult> PublishJoinedCompanionReadAsync(
        Task<SavedMacroDocumentReadResult> work, Task completion)
    {
        var result = await work.ConfigureAwait(false);
        await completion.ConfigureAwait(false);
        return result;
    }

    partial void ObserveCompanionRecovery(Guid id, string path, ref Task? read)
    {
        lock (_companionCatalogSync)
        {
            if (_companionClosed || _companionResolutionQuarantined || _companionLoadFailed || _companionLoad is not null
                || _companionWrites != 0 || _companionPending != 0 || _companionReadOwner is not null
                || _companionReadTask is { IsCompleted: false }) return;
            var owner = new object();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _companionReadOwner = owner;
            var work = ReadRecoveredCompanionDocumentAsync(id, path, _companionEpoch, _companionMutation, owner, start.Task);
            _companionReadTask = work; read = work;
            start.SetResult();
        }
    }

    private async Task ReadRecoveredCompanionDocumentAsync(Guid id, string path, Guid epoch, Guid mutation, object owner, Task start)
    {
        await start.ConfigureAwait(false);
        try
        {
            MacroDocumentLoadResult loaded = await Task.Run(() => _documentStore.Load(path), CancellationToken.None).ConfigureAwait(false);
            if (loaded.Models.Count != 1 || loaded.Model.Id != id
                || !System.IO.Path.GetFullPath(loaded.SourcePath).Equals(System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                throw new System.IO.InvalidDataException("The recovered saved document identity changed.");
            lock (_companionCatalogSync)
            {
                if (_companionClosed || _companionEpoch != epoch || _companionMutation != mutation
                    || _companionWrites != 0 || _companionPending != 0 || _companionResolutionQuarantined
                    || _companionLoadFailed || _companionLoad is not null) return;
                if (_companionCatalog.BeginSave(loaded.Model.Id) is not { } token) return;
                var entry = CompanionEntry(loaded.Model, token.SavedRevision, loaded.SourcePath, loaded.Receipt);
                if (_companionCatalog.PublishSaved(token, entry.Descriptor)) _companionSaved[loaded.Model.Id] = entry;
                _companionMutation = Guid.NewGuid();
            }
        }
        catch (Exception)
        {
            lock (_companionCatalogSync)
                if (!_companionClosed && _companionEpoch == epoch && _companionMutation == mutation) CompanionUnavailableLocked();
        }
        finally
        {
            lock (_companionCatalogSync)
                if (ReferenceEquals(owner, _companionReadOwner)) _companionReadOwner = null;
        }
    }

    partial void DrainCompanionDocuments(ref Task? read)
    {
        lock (_companionCatalogSync) read = _companionReadTask;
    }

    private void CancelCompanionDocumentRead()
    {
        CancellationTokenSource? stop;
        lock (_companionCatalogSync) stop = _companionReadCancellation;
        try { stop?.Cancel(); }
        catch (ObjectDisposedException) { } // The joined owner completed between capture and cancellation.
    }
}
#endif
