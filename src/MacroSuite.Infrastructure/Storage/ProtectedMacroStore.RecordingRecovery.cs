using System.Security.Cryptography;
using MacroSuite.Core.Storage;
namespace MacroSuite.Infrastructure.Storage;
public sealed partial class ProtectedMacroStore : IRecordingRecoveryProvider
{
    private IRecordingRecoveryStore? _recordingRecovery;
    public IRecordingRecoveryStore RecordingRecovery { get { lock (_gate) return _recordingRecovery ??= new EncryptedRecordingRecoveryStore(
        Path.Combine(_rootDirectory, "RecordingRecovery"), EncryptRecording, DecryptRecording,
        LoadRecordingDocument,
        SaveRecordingDocument,
        model => PrepareModel(_serializer.Serialize(model), _rootDirectory)); } }

    // Older managed libraries retain descriptive filenames after encrypted migration.
    // Recording identity comes from authenticated contents, never from a filename.
    private (string Path, MacroSuite.Core.Models.MacroModel Model)? FindRecordingDocument(Guid id)
    {
        if (!Directory.Exists(_libraryDirectory)) return null;
        (string Path, MacroSuite.Core.Models.MacroModel Model)? found = null;
        string canonical = GetLibraryPath(id);
        foreach (string file in Directory.EnumerateFiles(_libraryDirectory))
        {
            if (!Path.GetExtension(file).Equals(".zmacro", StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recording recovery cannot verify a linked library document. Existing files and recovery data were preserved.");
            MacroSuite.Core.Models.MacroModel model;
            try { model = Load(file).Model; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            {
                // An unreadable managed document might be this identity or a duplicate.
                // Never treat it as an absent macro and replace it during recovery.
                throw new IOException($"Recording recovery could not verify library document '{Path.GetFileName(file)}'. Existing files and recovery data were preserved.", ex);
            }
            if (model.Id != id)
            {
                if (file.Equals(canonical, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The recording destination filename belongs to a different macro. Existing files and recovery data were preserved.");
                continue;
            }
            if (found.HasValue)
                throw new IOException("Multiple library documents have the recording destination's identity. Existing files and recovery data were preserved; resolve the duplicate before retrying recovery.");
            found = (file, model);
        }
        return found;
    }

    private MacroSuite.Core.Models.MacroModel? LoadRecordingDocument(Guid id)
    {
        lock (_gate) return FindRecordingDocument(id)?.Model;
    }

    private void SaveRecordingDocument(MacroSuite.Core.Models.MacroModel model)
    {
        lock (_gate)
        {
            var existing = FindRecordingDocument(model.Id);
            Save(model, existing?.Path ?? GetLibraryPath(model.Id));
        }
    }

    private byte[] EncryptRecording(byte[] plain)
    {
        lock (_gate)
        {
        string recovery = Path.Combine(_rootDirectory, "RecordingRecovery");
        bool hasRecovery = Directory.Exists(recovery) && Directory.EnumerateFiles(recovery, "*", SearchOption.AllDirectories).Any();
        byte[] key = _keys.GetKey(createIfMissing: !HasProtectedLibrary() && !hasRecovery);
        try { return Encrypt(plain, key, null, 4); }
        finally { CryptographicOperations.ZeroMemory(key); }
        }
    }
    private byte[] DecryptRecording(byte[] encrypted)
    {
        lock (_gate)
        {
        byte[] key = _keys.GetKey(createIfMissing: false);
        try { return Decrypt(encrypted, key, null, 4); }
        finally { CryptographicOperations.ZeroMemory(key); }
        }
    }
}

