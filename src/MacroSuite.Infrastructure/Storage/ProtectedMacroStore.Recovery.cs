using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Models;

namespace MacroSuite.Infrastructure.Storage;

public sealed partial class ProtectedMacroStore
{
    private string RecoveryMarker => Path.Combine(_rootDirectory, "Recovery.pending");
    private string ContentKeyPath => Path.Combine(_rootDirectory, "Security", "macro-data-key.bin");

    private void EnsureNoPendingRecovery()
    {
        if (File.Exists(RecoveryMarker))
            throw new MacroProtectionException("A previous backup restore was interrupted. Your encrypted recovery archive was kept. Import your verified backup again to finish recovery before using this library.");
    }

    public void RecoverLibraryFromBackup(string path, string passphrase)
    {
        // Fully authenticate/decode before creating a key, directory, or recovery journal.
        byte[] file = ReadBounded(path, MaxDecodedBytes + HeaderSize);
        IReadOnlyList<MacroModel> models;
        try
        {
            if (!IsV2(file) || file.Length < HeaderSize || file[5] != 2)
                throw new MacroProtectionException("Recovery requires a passphrase-protected .zbackup file.");
            byte[] plain = Decrypt(file, null, passphrase, 2);
            try { models = ReadBackup(plain, path); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { CryptographicOperations.ZeroMemory(file); }

        lock (_gate)
        {
            string recoveryId = Guid.NewGuid().ToString("N");
            string recoveryDirectory = Path.Combine(_rootDirectory, "Recovery", recoveryId);
            string freshLibrary = Path.Combine(recoveryDirectory, "FreshLibrary");
            var fresh = new ProtectedMacroStore(freshLibrary);
            foreach (var model in models)
            {
                fresh.Save(model, fresh.GetLibraryPath(model.Id));
                if (_serializer.Serialize(fresh.Load(fresh.GetLibraryPath(model.Id)).Model) != _serializer.Serialize(model))
                    throw new MacroProtectionException("The restored macro did not verify. The existing library was kept.");
            }
            byte[] replacementKey = fresh._keys.GetKey(createIfMissing: false);
            var oldFiles = RecoverySources();
            var records = new List<RecoveryRecord>(oldFiles.Count);
            long totalBytes = 0;
            try
            {
                // Every previous file, including legacy JSON and the old DPAPI key record,
                // is archived under the replacement key before the live library changes.
                for (int index = 0; index < oldFiles.Count; index++)
                {
                    string source = oldFiles[index];
                    byte[] original = ReadBounded(source, MaxDecodedBytes + HeaderSize);
                    totalBytes += original.Length;
                    if (totalBytes > 1024L * 1024 * 1024)
                        throw new MacroProtectionException("This recovery exceeds the 1 GB safety budget. Preserve the current library and restore the backup into a fresh library folder.");
                    string archive = Path.Combine(recoveryDirectory, "Previous", index.ToString("D4") + ".zrollback");
                    try
                    {
                        byte[] hash = SHA256.HashData(original);
                        WriteAtomic(archive, Encrypt(original, replacementKey, null, 3));
                        byte[] verified = Decrypt(ReadBounded(archive, MaxDecodedBytes + HeaderSize), replacementKey, null, 3);
                        try
                        {
                            if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(verified)))
                                throw new MacroProtectionException("A recovery archive did not verify. The existing library was kept.");
                        }
                        finally { CryptographicOperations.ZeroMemory(verified); }
                        records.Add(new RecoveryRecord(source, archive, Convert.ToHexString(hash)));
                    }
                    finally { CryptographicOperations.ZeroMemory(original); }
                }
                byte[] inventory = JsonSerializer.SerializeToUtf8Bytes(records);
                try { WriteAtomic(Path.Combine(recoveryDirectory, "inventory.zrollback"), Encrypt(inventory, replacementKey, null, 3)); }
                finally { CryptographicOperations.ZeroMemory(inventory); }

                string[] restoredPaths = models.Select(model => GetLibraryPath(model.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (restoredPaths.Length != models.Count) throw new InvalidDataException("The backup contains duplicate macro identities.");
                bool changesStarted = false;
                try
                {
                    Directory.CreateDirectory(_rootDirectory);
                    foreach (RecoveryRecord record in records)
                    {
                        byte[] current = ReadBounded(record.Source, MaxDecodedBytes + HeaderSize);
                        try
                        {
                            if (!SHA256.HashData(current).AsSpan().SequenceEqual(Convert.FromHexString(record.Sha256)))
                                throw new IOException("The library changed during recovery. Close other Zenith instances and try again.");
                        }
                        finally { CryptographicOperations.ZeroMemory(current); }
                    }
                    File.WriteAllText(RecoveryMarker, recoveryId, Encoding.ASCII);
                    changesStarted = true;
                    foreach (RecoveryRecord record in records) File.Delete(record.Source);
                    WriteAtomic(ContentKeyPath, File.ReadAllBytes(fresh.ContentKeyPath));
                    foreach (var model in models)
                        WriteAtomic(GetLibraryPath(model.Id), ReadBounded(fresh.GetLibraryPath(model.Id), MaxDecodedBytes + HeaderSize));
                    // Verify through the production key store, not only the staged key.
                    foreach (var model in models)
                    {
                        byte[] envelope = ReadBounded(GetLibraryPath(model.Id), MaxDecodedBytes + HeaderSize);
                        byte[] liveKey = _keys.GetKey(createIfMissing: false);
                        try
                        {
                            byte[] plain = Decrypt(envelope, liveKey, null, 1);
                            try
                            {
                                var actual = PrepareModel(Utf8.GetString(plain), _libraryDirectory);
                                if (_serializer.Serialize(actual) != _serializer.Serialize(model)) throw new MacroProtectionException("Live recovery verification failed.");
                            }
                            finally { CryptographicOperations.ZeroMemory(plain); }
                        }
                        finally { CryptographicOperations.ZeroMemory(liveKey); CryptographicOperations.ZeroMemory(envelope); }
                    }
                    File.Delete(RecoveryMarker);
                }
                catch (Exception restoreFailure)
                {
                    if (!changesStarted) throw;
                    try
                    {
                        foreach (string restored in restoredPaths) File.Delete(restored);
                        foreach (RecoveryRecord record in records)
                        {
                            byte[] original = Decrypt(ReadBounded(record.Archive, MaxDecodedBytes + HeaderSize), replacementKey, null, 3);
                            try
                            {
                                // Restoring an original legacy file is rollback, never a newly created plaintext backup.
                                Directory.CreateDirectory(Path.GetDirectoryName(record.Source)!);
                                File.WriteAllBytes(record.Source, original);
                                if (!SHA256.HashData(File.ReadAllBytes(record.Source)).AsSpan().SequenceEqual(Convert.FromHexString(record.Sha256)))
                                    throw new IOException("Recovery rollback verification failed.");
                            }
                            finally { CryptographicOperations.ZeroMemory(original); }
                        }
                        // A fresh installation had no previous key to restore.
                        if (!records.Any(record => record.Source.Equals(ContentKeyPath, StringComparison.OrdinalIgnoreCase))) File.Delete(ContentKeyPath);
                        File.Delete(RecoveryMarker);
                    }
                    catch (Exception rollbackFailure)
                    {
                        throw new MacroProtectionException("Backup restore and rollback were interrupted. Keep the Recovery folder and import the verified backup again before opening the library.", new AggregateException(restoreFailure, rollbackFailure));
                    }
                    throw new MacroProtectionException("The backup could not be installed. The previous files were restored and the encrypted recovery archive was kept.", restoreFailure);
                }
            }
            finally { CryptographicOperations.ZeroMemory(replacementKey); }
        }
    }

    private List<string> RecoverySources()
    {
        var files = Directory.Exists(_libraryDirectory)
            ? Directory.EnumerateFiles(_libraryDirectory).Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".zmacro", StringComparison.OrdinalIgnoreCase)).Take(1001).ToList()
            : new List<string>();
        foreach (string folder in new[] { Path.Combine(_libraryDirectory, ".deleted"), Path.Combine(_rootDirectory, "Backups", "LegacyEncrypted") })
            if (Directory.Exists(folder)) files.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(Math.Max(0, 1001 - files.Count)));
        if (File.Exists(ContentKeyPath)) files.Add(ContentKeyPath);
        if (files.Count > 1000) throw new MacroProtectionException("This library exceeds the 1,000-file recovery safety limit. Restore into a fresh library folder.");
        return files;
    }

    private sealed record RecoveryRecord(string Source, string Archive, string Sha256);
}
