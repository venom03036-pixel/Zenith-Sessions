using System.IO;
using System.Windows;
using System.Text.Json.Nodes;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Security;
using MacroSuite.Infrastructure.Storage;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    private IMacroDocumentStore _documentStore = null!;
    private bool _libraryLoadDeferred;
    private bool _libraryLoadHadErrors;
    private bool _libraryInitialized;
    private string _libraryAccessError = string.Empty;
    private string _runningMacroHotkey = string.Empty;
    private bool _libraryKeyUnavailable;

    public string LibraryAccessError => _libraryAccessError;
    public string RunningMacroHotkey => _runningMacroHotkey;
    public bool LibraryKeyUnavailable => _libraryKeyUnavailable;
    public bool CanImportMacro => HasActivation && !_libraryLoadDeferred && !IsRunning && !IsRecordingBusy
        && !ActionEditor.IsTestingDetection && ExternalInputIsActive?.Invoke() != true && !_shutdownStarted;

    public void RefreshImportAvailability()
    {
        OnPropertyChanged(nameof(CanImportMacro));
        LoadMacroCommand?.RaiseCanExecuteChanged();
    }
    public Func<bool, string?> BackupPassphrasePrompter { get; set; } = confirm =>
        BackupPassphraseDialog.Prompt(Application.Current?.MainWindow, confirm);
    public Func<bool> ConfirmBackupRecoveryPrompter { get; set; } = () => MessageBox.Show(Application.Current?.MainWindow,
        "This Windows account cannot unlock the current macro library. Restore the verified backup using a new local encryption key? "
        + "The current files and old key record will be preserved in an encrypted Recovery archive before replacement. "
        + "Only macros in this backup will appear in the restored library.", "Restore encrypted macro library",
        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void InitializeProtectedStorage(IMacroDocumentStore? documentStore, bool deferLibraryLoad)
    {
        _documentStore = documentStore ?? new ProtectedMacroStore(_macrosDirectory);
        _libraryLoadDeferred = deferLibraryLoad;
    }

    /// <summary>Called only after the public license has been validated. No encrypted library is read beforehand.</summary>
    public bool LoadLibraryAfterActivation()
    {
        if (!HasActivation) { NotifyStatus("Activate this installation before opening the macro library."); return false; }
        if (_libraryInitialized && !_libraryLoadHadErrors) return true;
        _libraryLoadDeferred = false;
        InitializeScriptsLibrary(seedDemoData: false);
        RefreshKeybindRows();
        return !_libraryLoadHadErrors;
    }

    private void SetLibraryAccessError(Exception error)
    {
        _libraryLoadHadErrors = true;
        _libraryKeyUnavailable = _isPersistenceEnabled && !_documentStore.CanWriteLibrary;
        _libraryAccessError = error is MacroProtectionException ? error.Message
            : "Some macro files could not be loaded. They have been kept. Check the Log tab before restoring a backup or replacing missing target images.";
        OnPropertyChanged(nameof(LibraryAccessError));
        OnPropertyChanged(nameof(LibraryKeyUnavailable));
        RefreshImportAvailability();
        NotifyStatus(_libraryAccessError);
    }

    private void BeginProtectedLibraryLoad()
    {
        _libraryInitialized = true;
        _libraryLoadHadErrors = false;
        _libraryAccessError = string.Empty;
        _libraryKeyUnavailable = _isPersistenceEnabled && !_documentStore.CanWriteLibrary;
        if (_libraryKeyUnavailable)
        {
            _libraryLoadHadErrors = true;
            _libraryAccessError = "This Windows account cannot unlock the macro library key. Existing files have been kept. Import a passphrase-protected backup to recover the library.";
        }
        OnPropertyChanged(nameof(LibraryAccessError)); OnPropertyChanged(nameof(LibraryKeyUnavailable));
        RefreshImportAvailability();
    }

    private string ProtectedSavePath(MacroScriptItemViewModel script)
        => !string.IsNullOrEmpty(script.FilePath) && Path.GetExtension(script.FilePath).Equals(".zmacro", StringComparison.OrdinalIgnoreCase)
            ? script.FilePath : _documentStore.GetLibraryPath(script.Id);

    private MacroModel ReadProtectedLibraryDocument(string path, out string resolvedPath, out MacroDocumentLoadResult loaded)
    {
        ValidateProtectedDocument(_documentStore.Load(path).Model);
        loaded = _documentStore.MigrateLibraryDocument(path);
        resolvedPath = loaded.SourcePath;
        return loaded.Model;
    }

    partial void ValidateProtectedDocument(MacroModel model);

    private bool ImportProtectedMacroFile(string sourcePath, string? passphrase, Func<Exception, string> describeError)
    {
        if (!CanImportMacro) { NotifyStatus(HasActivation ? "Stop recording, playback, detection testing and the Auto Clicker before importing a macro." : "Activate this installation before opening the macro library."); return false; }
        if (!TryCommitPendingActionEdits()) return false;
        var persisted = new List<string>();
        object? companionImport = null;
        bool verifiedBackup = false;
        try
        {
            passphrase = GetBackupPassphrase(sourcePath, passphrase);
            if (Path.GetExtension(sourcePath).Equals(".zbackup", StringComparison.OrdinalIgnoreCase) && passphrase is null) return false;
            MacroDocumentLoadResult document = _documentStore.Load(sourcePath, passphrase);
            var importedItems = new List<MacroScriptItemViewModel>();
            foreach (MacroModel model in document.Models)
            {
                ValidateProtectedDocument(model);
                var validation = new MacroFileValidator().Validate(ValidationJson(model), Path.GetFileName(sourcePath));
                if (!validation.IsValid) throw new InvalidDataException(string.Join(" ", validation.Violations));
            }
            verifiedBackup = !document.IsLegacy && Path.GetExtension(sourcePath).Equals(".zbackup", StringComparison.OrdinalIgnoreCase);
            foreach (MacroModel model in document.Models)
            {
                MacroSettings settings = model.Settings ?? new MacroSettings();
                string name = string.IsNullOrWhiteSpace(model.Name) ? Path.GetFileNameWithoutExtension(sourcePath) : model.Name;
                Guid id = Guid.NewGuid();
                string destination = _documentStore.GetLibraryPath(id);
                var owned = new MacroModel
                {
                    RecordingCommit = model.RecordingCommit, Id = id, Name = name, Description = model.Description,
                    CreatedAt = model.CreatedAt == default ? DateTimeOffset.UtcNow : model.CreatedAt,
                    IsFavorite = model.IsFavorite, Settings = settings, Steps = model.Steps ?? Array.Empty<MacroStep>()
                };
                object? companionAttempt = null;
                CaptureCompanionImport(owned, ref companionImport, ref companionAttempt);
                SaveProtectedDocument(owned, destination, companionAttempt); persisted.Add(destination);
                var script = new MacroScriptItemViewModel(name, owned.Steps.Count, settings.Hotkey, settings.IsActive, "Never", id, destination, owned.CreatedAt, owned.IsFavorite)
                {
                    RecordingCommit = owned.RecordingCommit, Target = settings.Target, Description = owned.Description, MaxRuntime = settings.MaxRuntime, MaxIterations = settings.MaxIterations,
                    MaxLoopDepth = settings.MaxLoopDepth, RepeatCount = Math.Max(1, settings.RepeatCount), RepeatUntilStopped = settings.RepeatUntilStopped,
                    PlaybackSpeed = settings.PlaybackSpeed > 0.05 ? settings.PlaybackSpeed : 1.0, MouseSamplingMode = settings.MouseSamplingMode
                };
                script.Actions.AddRange(owned.Steps); importedItems.Add(script);
            }
            foreach (MacroScriptItemViewModel script in importedItems) MacroScripts.Add(script);
            // All owned import writes succeeded. Publish before selection can initiate another save
            // for an imported ID and supersede this successful write's metadata token.
            CompleteCompanionImport(companionImport, success: true);
            ApplyScriptSorting(); SelectedScript = importedItems.Last(); HotkeysUpdated?.Invoke();
            NotifyStatus($"Imported {importedItems.Count} encrypted macro(s). The original file was kept.");
            LogActivity($"Imported {importedItems.Count} macro(s) from {Path.GetFileName(sourcePath)}.");
            return true;
        }
        catch (Exception ex)
        {
            CompleteCompanionImport(companionImport, success: false);
            object? rollback = null;
            BeginCompanionMutation(null, ref rollback);
            try
            {
                foreach (string path in persisted)
                try { File.Delete(path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { LogActivity("An encrypted incomplete import could not be removed; the original source is unchanged."); }
            }
            finally { CompleteCompanionMutation(rollback); }
            if (verifiedBackup && passphrase is not null && ex is MacroProtectionException && ConfirmBackupRecoveryPrompter())
            {
                try
                {
                    object? recovery = null;
                    BeginCompanionLibraryLoad();
                    BeginCompanionMutation(null, ref recovery);
                    try { _documentStore.RecoverLibraryFromBackup(sourcePath, passphrase); }
                    finally { CompleteCompanionMutation(recovery); }
                    _deletedMacro = null; _deletedMacroPath = null;
                    OnPropertyChanged(nameof(HasDeletedMacro)); OnPropertyChanged(nameof(DeletedMacroNotice));
                    UndoDeleteMacroCommand.RaiseCanExecuteChanged();
                    InitializeScriptsLibrary(seedDemoData: false); RefreshKeybindRows();
                    if (_libraryLoadHadErrors) return false;
                    NotifyStatus("Encrypted backup restored. Previous files and the old key record are preserved in the Recovery folder.");
                    LogActivity("Encrypted backup recovery completed and verified."); return true;
                }
                catch (Exception recoveryError)
                {
                    CompleteCompanionLibraryLoad(success: false);
                    SetLibraryAccessError(recoveryError);
                    LogActivity("Encrypted backup recovery failed. Existing files or the verified Recovery archive have been kept."); return false;
                }
            }
            string message = describeError(ex);
            NotifyStatus($"Could not import macro: {message}"); LogActivity($"Warning: Macro import failed: {message}"); return false;
        }
    }

    private string ValidationJson(MacroModel model)
    {
        JsonNode node = JsonNode.Parse(_serializer.Serialize(model))!;
        RedactImageBytes(node);
        return node.ToJsonString();
    }

    private static void RedactImageBytes(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(pair => pair.Key).ToArray())
            {
                if ((key.Equals("templateBytes", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("templateBgra", StringComparison.OrdinalIgnoreCase)) && obj[key] is JsonValue)
                    obj[key] = null; // The document store already validates actual binary sizes and images.
                else if (obj[key] is JsonObject or JsonArray) RedactImageBytes(obj[key]!);
            }
        }
        else if (node is JsonArray array)
            foreach (JsonNode? child in array) if (child is not null) RedactImageBytes(child);
    }

    private string? GetBackupPassphrase(string path, string? passphrase)
    {
        if (!Path.GetExtension(path).Equals(".zbackup", StringComparison.OrdinalIgnoreCase)) return passphrase;
        return passphrase ?? BackupPassphrasePrompter(false);
    }
}
