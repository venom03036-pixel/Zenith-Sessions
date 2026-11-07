using System.IO;
using System.Security.Cryptography;
using MacroSuite.Core.Models;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    private sealed record RecordingSaveRequest(MacroScriptItemViewModel Script, MacroModel Model, string Path, object? CompanionAttempt = null);

    private static string? RecordingFileRevision(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private async Task CaptureRecordingFileRevisionAsync(OwnedRecording session, CancellationToken ct)
    {
        var path = ProtectedSavePath(session.Destination);
        session.DestinationFileHash = await Task.Run(() => RecordingFileRevision(path), ct);
        session.DestinationRevisionCaptured = true;
    }

    // Capture UI-owned values once. Editing stays locked until the owned stop task finishes.
    private RecordingSaveRequest? PrepareRecordingSave()
    {
        if (!_isPersistenceEnabled || _libraryLoadDeferred || (_ownedRecording?.Destination ?? SelectedScript) is not { } script) return null;
        var captured = _ownedRecording?.Baseline;
        var model = new MacroModel
        {
            Id = script.Id, Name = captured?.Name ?? script.Name, Description = captured?.Description ?? script.Description, CreatedAt = script.CreatedAt,
            IsFavorite = script.IsFavorite, RecordingCommit = script.RecordingCommit,
            Settings = captured?.Settings ?? new MacroSettings
            {
                Target = script.Target, Hotkey = script.Hotkey, IsActive = script.IsActive, RepeatCount = script.RepeatCount,
                RepeatUntilStopped = script.RepeatUntilStopped, PlaybackSpeed = script.PlaybackSpeed,
                MaxRuntime = script.MaxRuntime, MaxIterations = script.MaxIterations,
                MaxLoopDepth = script.MaxLoopDepth, MouseSamplingMode = script.MouseSamplingMode
            },
            Steps = script.GetActions().ToList()
        };
        BeginScriptSave(script);
        string path = ProtectedSavePath(script);
        object? companionAttempt = null;
        CaptureCompanionSave(model, ref companionAttempt);
        return new(script, model, path, companionAttempt);
    }

    private async Task SaveRecordingSnapshotAsync(RecordingSaveRequest save)
    {
        Exception? failure = null;
        bool journalPrepared = false;
        var session = _ownedRecording;
        try
        {
            if (session?.Journal != null)
            {
                var original = save.Model;
                var commit = session.Journal.CreateCapturedCommitMetadata(_recordingInputBuffer?.LastAcceptedSequence ?? 0);
                save = save with { Model = new MacroModel {
                    Id = original.Id, Name = original.Name, Description = original.Description, CreatedAt = original.CreatedAt,
                    IsFavorite = original.IsFavorite, Settings = original.Settings, Steps = original.Steps, RecordingCommit = commit } };
            }
            if (session?.Journal != null && !session.RecoveryFailed)
            {
                try { await session.Journal.PrepareCommitAsync(save.Model).ConfigureAwait(false); journalPrepared = true; }
                catch (Exception ex) { _recordingFailure = "Recovery checkpoint failed; recording data is still being saved: " + ex.Message; }
            }
            // Encryption, image packing and atomic disk writes run once, away from WPF's dispatcher.
            // This task is awaited by _recordingStopTask and by application shutdown.
            await Task.Run(() =>
            {
                Directory.CreateDirectory(_macrosDirectory);
                if (session?.DestinationRevisionCaptured == true)
                {
                    try
                    {
                        if (RecordingFileRevision(save.Path) != session.DestinationFileHash)
                            throw new IOException("The destination file changed during recording. It was not overwritten. Export the retained recording as a backup, or restart and use recording recovery to create a separate copy.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { session.DestinationRevisionConflict = true; throw; }
                }
                SaveProtectedDocument(save.Model, save.Path, save.CompanionAttempt);
            }, CancellationToken.None).ConfigureAwait(false);
            if (journalPrepared)
            {
                try { await session!.Journal!.CompleteCommitAsync().ConfigureAwait(false); }
                catch (Exception ex) { _recordingFailure = "Recording saved; recovery cleanup needs attention: " + ex.Message; }
            }
        }
        catch (Exception ex) { failure = ex; }

        void Finish()
        {
            if (failure is null)
            {
                save.Script.FilePath = save.Path;
                save.Script.RecordingCommit = save.Model.RecordingCommit;
                CompleteScriptSave(save.Script, save.Model, save.CompanionAttempt);
            }
            else
            {
                _recordingFailure = $"Save failed: {failure.Message}. Recorded actions remain in the editor.";
                CompleteScriptSave(save.Script, null, save.CompanionAttempt);
                NotifyStatus($"Could not save '{save.Script.Name}': {failure.Message}. Recorded actions remain in the editor.");
                LogActivity($"Recording save failed for '{save.Script.Name}': {failure.Message}");
            }
        }
        if (_uiDispatcher != null && !_uiDispatcher.CheckAccess()) await _uiDispatcher.InvokeAsync(Finish);
        else Finish();
    }
}
