using System.Collections.Specialized;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
#if DEVELOPER_MODE
using System.IO;
using MacroSuite.App.Common;
using Zenith.MacroHost.Contracts;
#endif

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    // Public builds erase companion calls and references; ordinary document saves remain unchanged.
    partial void BeginCompanionLibraryLoad();
    partial void CompleteCompanionLibraryLoad(bool success);
    partial void ObserveCompanionLoadedModel(MacroModel model, string path, MacroDocumentLoadResult loaded);
    partial void CaptureCompanionSave(MacroModel model, ref object? attempt);
    partial void WriteCompanionDocument(MacroModel model, string path, object? attempt, ref bool handled);
    partial void CompleteCompanionSave(MacroScriptItemViewModel? script, MacroModel? saved, object? attempt);
    partial void CaptureCompanionImport(MacroModel model, ref object? batch, ref object? attempt);
    partial void CompleteCompanionImport(object? batch, bool success);
    partial void CompanionScriptsChanged(NotifyCollectionChangedEventArgs change);
    partial void CaptureCompanionDeletedScript(Guid id);
    partial void RestoreCompanionDeletedScript(MacroScriptItemViewModel script, bool fileRestored);
    partial void BeginCompanionMutation(Guid? id, ref object? mutation);
    partial void CompleteCompanionMutation(object? mutation);
    partial void ObserveCompanionRecovery(Guid id, string path, ref Task? read);
    partial void CloseCompanionCatalog();
    partial void DrainCompanionDocuments(ref Task? read);

    private void SaveProtectedDocument(MacroModel model, string path, object? attempt)
    {
        bool handled = false;
        WriteCompanionDocument(model, path, attempt, ref handled);
        if (!handled) _documentStore.Save(model, path);
    }

#if DEVELOPER_MODE
    private const int MaximumCompanionWriters = 64;
    private readonly object _companionCatalogSync = new();
    private readonly SavedMacroCatalog _companionCatalog = new(BuildEnvironment.Version);
    // Only metadata, internal paths and ciphertext receipts live in this index. No document body is cached.
    private readonly Dictionary<Guid, CompanionSavedEntry> _companionSaved = new();
    private CompanionSavedEntry? _companionDeleted;
    private SavedMacroCatalogLoadAttempt? _companionLoad;
    private Guid _companionEpoch = Guid.NewGuid();
    private Guid _companionMutation = Guid.NewGuid();
    private int _companionWrites, _companionPending;
    private bool _companionResolutionQuarantined;
    private bool _companionLoadFailed, _companionClosed, _companionWarningReported;

    /// <summary>Saved display metadata only; catalog queries never read or decrypt documents.</summary>
    public IMacroCatalogSource CompanionCatalog => _companionCatalog;

    private sealed record CompanionSavedEntry(SavedMacroDescriptor Descriptor, string? Path, SavedMacroDocumentReceipt? Receipt);
    private sealed class CompanionSave(Guid epoch, SavedMacroSaveAttempt? token, SavedMacroDescriptor? descriptor, Guid id)
    {
        public Guid Epoch { get; } = epoch;
        public SavedMacroSaveAttempt? Token { get; } = token;
        public Guid Id { get; } = id;
        public CompanionSavedEntry? Entry { get; set; } = descriptor is null ? null : new(descriptor, null, null);
        public bool Completed { get; set; }
    }
    private sealed class CompanionMutationOwner { public bool Completed; }

    private static SavedMacroDescriptor CompanionDescriptor(MacroModel model, Guid revision) => new(
        model.Id, revision, model.Name, model.Settings?.Hotkey ?? "", model.Steps?.Count ?? 0,
        model.Settings?.IsActive ?? true, model.Settings?.Target?.Mode.ToString() ?? "Legacy");

    private CompanionSavedEntry CompanionEntry(MacroModel model, Guid revision, string path, SavedMacroDocumentReceipt? receipt)
    {
        var descriptor = CompanionDescriptor(model, revision).Validated();
        if (receipt is null) return new(descriptor, null, null);
        receipt.Validated();
        string ownedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_macrosDirectory, model.Id.ToString("N") + ".zmacro"));
        if (receipt.MacroId != model.Id || !System.IO.Path.GetFullPath(path).Equals(ownedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Saved macro evidence does not match its owned document.");
        return new(descriptor, ownedPath, receipt);
    }

    private void CompanionUnavailableLocked()
    {
        _companionLoadFailed = true;
        _companionResolutionQuarantined = true;
        _companionMutation = Guid.NewGuid();
        _companionSaved.Clear(); _companionDeleted = null;
        _companionCatalog.FailLoad(_companionLoad ?? _companionCatalog.BeginLoad());
        if (!_companionWarningReported)
        {
            _companionWarningReported = true;
            System.Diagnostics.Trace.TraceWarning("Saved macro metadata is unavailable; document save results are unchanged. Reload the library to retry the metadata link.");
        }
    }

    partial void BeginCompanionLibraryLoad()
    {
        if (!_isPersistenceEnabled) return;
        lock (_companionCatalogSync)
        {
            if (_companionClosed) return;
            try
            {
                _companionEpoch = Guid.NewGuid(); _companionMutation = Guid.NewGuid();
                _companionSaved.Clear(); _companionDeleted = null;
                // A reload cannot repair authority while an older actual owner can still replace bytes.
                _companionResolutionQuarantined = _companionWrites != 0 || _companionPending != 0;
                _companionLoadFailed = false; _companionWarningReported = false;
                _companionLoad = _companionCatalog.BeginLoad();
            }
            catch (Exception) { CompanionUnavailableLocked(); }
        }
    }

    partial void ObserveCompanionLoadedModel(MacroModel model, string path, MacroDocumentLoadResult loaded)
    {
        lock (_companionCatalogSync)
        {
            if (_companionClosed || _companionLoad is null || _companionLoadFailed) return;
            try
            {
                var entry = CompanionEntry(model, Guid.NewGuid(), path, loaded.Receipt);
                if (_companionSaved.Count >= MacroCatalogLimits.MaximumItems || !_companionSaved.TryAdd(model.Id, entry))
                    CompanionUnavailableLocked();
            }
            catch (Exception) { CompanionUnavailableLocked(); }
        }
    }

    partial void CompleteCompanionLibraryLoad(bool success)
    {
        lock (_companionCatalogSync)
        {
            if (_companionClosed || _companionLoad is not { } load) return;
            try
            {
                if (!success || _companionLoadFailed) CompanionUnavailableLocked();
                else if (!_companionCatalog.CompleteLoad(load, _companionSaved.Values.Select(entry => entry.Descriptor).ToArray()))
                    CompanionUnavailableLocked();
            }
            catch (Exception) { CompanionUnavailableLocked(); }
            finally { _companionLoad = null; }
        }
    }

    partial void CaptureCompanionSave(MacroModel model, ref object? attempt)
    {
        if (!_isPersistenceEnabled || _libraryLoadDeferred) return;
        lock (_companionCatalogSync)
        {
            if (_companionClosed) return;
            SavedMacroSaveAttempt? token = null;
            SavedMacroDescriptor? descriptor = null;
            try
            {
                token = _companionLoad is null ? _companionCatalog.BeginSave(model.Id) : null;
                descriptor = CompanionDescriptor(model, token?.SavedRevision ?? Guid.NewGuid()).Validated();
            }
            catch (Exception) { CompanionUnavailableLocked(); }
            _companionMutation = Guid.NewGuid();
            _companionPending++;
            if (_companionPending > MacroCatalogLimits.MaximumItems) _companionResolutionQuarantined = true;
            attempt = new CompanionSave(_companionEpoch, token, descriptor, model.Id);
        }
    }

    partial void WriteCompanionDocument(MacroModel model, string path, object? attempt, ref bool handled)
    {
        handled = true;
        lock (_companionCatalogSync)
        {
            _companionWrites++; _companionMutation = Guid.NewGuid();
            if (_companionWrites > MaximumCompanionWriters) _companionResolutionQuarantined = true;
        }
        try
        {
            if (_documentStore is ISavedMacroDocumentStore owned)
            {
                SavedMacroDocument saved = owned.SaveWithReceipt(model, path);
                // Extract only the actual prepared metadata and receipt. Never keep the returned body.
                lock (_companionCatalogSync)
                {
                    if (attempt is CompanionSave current && !current.Completed && current.Epoch == _companionEpoch && !_companionClosed)
                    {
                        try
                        {
                            if (saved.SavedModel.Id != current.Id) throw new InvalidDataException("The saved document identity changed.");
                            current.Entry = CompanionEntry(saved.SavedModel, current.Token?.SavedRevision ?? current.Entry?.Descriptor.SavedRevision ?? Guid.NewGuid(), path, saved.Receipt);
                        }
                        catch (Exception) { current.Entry = null; CompanionUnavailableLocked(); }
                    }
                }
            }
            else _documentStore.Save(model, path);
        }
        finally
        {
            lock (_companionCatalogSync) { _companionWrites--; _companionMutation = Guid.NewGuid(); }
        }
    }

    private void FinishCompanionAttemptLocked(CompanionSave attempt)
    {
        if (attempt.Completed) return;
        attempt.Completed = true; _companionPending--; _companionMutation = Guid.NewGuid();
    }

    private void PublishCompanionSaveLocked(CompanionSave owned)
    {
        if (owned.Entry is not { } entry || entry.Descriptor.Id != owned.Id) { CompanionUnavailableLocked(); return; }
        if (_companionLoad is not null && !_companionLoadFailed)
        {
            if (!_companionSaved.ContainsKey(owned.Id) && _companionSaved.Count >= MacroCatalogLimits.MaximumItems)
            { CompanionUnavailableLocked(); return; }
            _companionSaved[owned.Id] = entry;
        }
        else if (owned.Token is { } token && _companionCatalog.PublishSaved(token, entry.Descriptor))
            _companionSaved[owned.Id] = entry;
    }

    partial void CompleteCompanionSave(MacroScriptItemViewModel? script, MacroModel? saved, object? attempt)
    {
        lock (_companionCatalogSync)
        {
            if (attempt is not CompanionSave owned || owned.Completed) return;
            try
            {
                if (_companionClosed || owned.Epoch != _companionEpoch) return;
                if (saved is null)
                {
                    if (owned.Token is { } failed) _companionCatalog.FailSave(failed);
                    if (_companionLoad is not null) _companionLoadFailed = true;
                }
                else if (saved.Id != owned.Id) CompanionUnavailableLocked();
                else PublishCompanionSaveLocked(owned);
            }
            catch (Exception) { CompanionUnavailableLocked(); }
            finally { FinishCompanionAttemptLocked(owned); }
        }
    }

    partial void CaptureCompanionImport(MacroModel model, ref object? batch, ref object? attempt)
    {
        CaptureCompanionSave(model, ref attempt);
        if (attempt is not CompanionSave owned) return;
        var items = batch as List<CompanionSave> ?? new(); batch = items;
        if (items.Count < MacroCatalogLimits.MaximumItems) items.Add(owned);
        else lock (_companionCatalogSync) { FinishCompanionAttemptLocked(owned); CompanionUnavailableLocked(); }
    }

    partial void CompleteCompanionImport(object? batch, bool success)
    {
        if (batch is not List<CompanionSave> items) return;
        lock (_companionCatalogSync)
        {
            foreach (var owned in items)
            {
                try
                {
                    if (_companionClosed || owned.Epoch != _companionEpoch) continue;
                    if (!success)
                    {
                        if (_companionSaved.Remove(owned.Id)) _companionCatalog.Remove(owned.Id);
                        else if (owned.Token is { } failed) _companionCatalog.FailSave(failed);
                    }
                    else if (!owned.Completed) PublishCompanionSaveLocked(owned);
                }
                catch (Exception) { CompanionUnavailableLocked(); }
                finally { FinishCompanionAttemptLocked(owned); }
            }
        }
    }

    partial void BeginCompanionMutation(Guid? id, ref object? mutation)
    {
        lock (_companionCatalogSync)
        {
            _companionMutation = Guid.NewGuid(); _companionWrites++;
            if (_companionWrites > MaximumCompanionWriters) _companionResolutionQuarantined = true;
            // Existing evidence is only an expected candidate. Keep it after failed I/O;
            // every future pin still requires fresh authenticated bytes matching this receipt.
            mutation = new CompanionMutationOwner();
        }
    }

    partial void CompleteCompanionMutation(object? mutation)
    {
        lock (_companionCatalogSync)
        {
            if (mutation is not CompanionMutationOwner owner || owner.Completed) return;
            owner.Completed = true; _companionWrites--; _companionMutation = Guid.NewGuid();
        }
    }

    partial void CompanionScriptsChanged(NotifyCollectionChangedEventArgs change)
    {
        if (change.Action == NotifyCollectionChangedAction.Move) return;
        lock (_companionCatalogSync)
        {
            if (_companionClosed) return;
            try
            {
                _companionMutation = Guid.NewGuid();
                if (change.Action == NotifyCollectionChangedAction.Reset)
                {
                    _companionSaved.Clear(); _companionDeleted = null;
                    if (_companionLoad is null) { _companionEpoch = Guid.NewGuid(); _companionCatalog.Reset(); }
                }
                else if (change.OldItems is not null)
                {
                    foreach (MacroScriptItemViewModel script in change.OldItems)
                    { _companionSaved.Remove(script.Id); _companionCatalog.Remove(script.Id); }
                }
            }
            catch (Exception) { CompanionUnavailableLocked(); }
        }
    }

    partial void CaptureCompanionDeletedScript(Guid id)
    {
        lock (_companionCatalogSync)
            _companionDeleted = _companionSaved.GetValueOrDefault(id);
    }

    partial void RestoreCompanionDeletedScript(MacroScriptItemViewModel script, bool fileRestored)
    {
        lock (_companionCatalogSync)
        {
            var candidate = _companionDeleted; _companionDeleted = null;
            if (_companionClosed || !fileRestored || candidate is null || candidate.Descriptor.Id != script.Id) return;
            try
            {
                if (_companionCatalog.BeginSave(script.Id) is not { } token) return;
                // This is an unverified candidate only. A later explicit reader must authenticate
                // the restored bytes and match the old exact receipt before it may create a pin.
                var entry = candidate with { Descriptor = candidate.Descriptor with { SavedRevision = token.SavedRevision } };
                if (_companionCatalog.PublishSaved(token, entry.Descriptor)) _companionSaved[script.Id] = entry;
                _companionMutation = Guid.NewGuid();
            }
            catch (Exception) { CompanionUnavailableLocked(); }
        }
    }

    partial void CloseCompanionCatalog()
    {
        lock (_companionCatalogSync)
        {
            _companionClosed = true; _companionEpoch = Guid.NewGuid(); _companionMutation = Guid.NewGuid(); _companionLoad = null;
            _companionSaved.Clear(); _companionDeleted = null; _companionCatalog.Reset();
        }
        CancelCompanionDocumentRead();
    }
#endif
}
