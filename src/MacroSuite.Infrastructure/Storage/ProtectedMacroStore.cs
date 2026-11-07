using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;

namespace MacroSuite.Infrastructure.Storage;

/// <summary>
/// AES-GCM v2 documents and portable passphrase backups. Decrypted data and image assets
/// remain in memory; only authenticated ciphertext is written, including temporary files.
/// </summary>
public sealed partial class ProtectedMacroStore : ISavedMacroDocumentStore
{
    public const int MaxDecodedBytes = 100 * 1024 * 1024;
    public const int BackupIterations = 600_000;
    private const int HeaderSize = 64;
    private const int AadSize = 48;
    private const int MaxTemplateBytes = 32 * 1024 * 1024;
    private readonly string _libraryDirectory;
    private readonly string _rootDirectory;
    private readonly ILocalMacroKeyStore _keys;
    private readonly MacroJsonSerializer _serializer = new();
    private readonly object _gate = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public ProtectedMacroStore(string libraryDirectory, ILocalMacroKeyStore? keys = null)
    {
        _libraryDirectory = Path.GetFullPath(libraryDirectory);
        // A caller may pass an arbitrary custom library folder. Keep its keys/backups
        // inside that folder rather than writing into an unrelated parent directory.
        _rootDirectory = _libraryDirectory;
        _keys = keys ?? new DpapiMacroKeyStore(Path.Combine(_rootDirectory, "Security", "macro-data-key.bin"));
    }

    public string GetLibraryPath(Guid id) => Path.Combine(_libraryDirectory, id.ToString("N") + ".zmacro");

    public bool CanWriteLibrary
    {
        get
        {
            try
            {
                EnsureNoPendingRecovery();
                if (!HasProtectedLibrary() && !File.Exists(ContentKeyPath)) return true;
                byte[] key = _keys.GetKey(createIfMissing: false);
                try { return key.Length == 32; }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            { return false; }
        }
    }

    public IReadOnlyList<string> EnumerateLibraryFiles()
    {
        if (!Directory.Exists(_libraryDirectory)) return Array.Empty<string>();
        string[] protectedFiles = Directory.GetFiles(_libraryDirectory, "*.zmacro");
        var stems = new HashSet<string>(protectedFiles.Select(path => Path.GetFileNameWithoutExtension(path)), StringComparer.OrdinalIgnoreCase);
        return protectedFiles.Concat(Directory.GetFiles(_libraryDirectory, "*.json")
            .Where(path => !stems.Contains(Path.GetFileNameWithoutExtension(path)))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public MacroDocumentLoadResult Load(string path, string? passphrase = null)
    {
        path = Path.GetFullPath(path);
        byte[] bytes = ReadBounded(path, MaxDecodedBytes + HeaderSize);
        byte[]? plain = null;
        try
        {
            bool encrypted = IsV2(bytes);
            if (encrypted)
            {
                if (bytes.Length < HeaderSize) throw new MacroProtectionException("The encrypted macro header is incomplete.");
                byte purpose = bytes[5];
                if (purpose == 2)
                {
                    if (string.IsNullOrEmpty(passphrase)) throw new MacroProtectionException("Enter the backup passphrase to import this file.");
                    plain = Decrypt(bytes, null, passphrase, 2);
                    return new MacroDocumentLoadResult(ReadBackup(plain, path), path, false);
                }
                if (purpose != 1) throw new MacroProtectionException("This protected file is not an importable macro document.");
                EnsureNoPendingRecovery();
                byte[] key = _keys.GetKey(createIfMissing: false);
                try { plain = Decrypt(bytes, key, null, 1); }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            else plain = ReadLegacy(bytes);
            string json = Utf8.GetString(plain);
            MacroModel prepared = PrepareModel(json, Path.GetDirectoryName(path)!, out bool readExternalAsset);
            return new MacroDocumentLoadResult(new[] { prepared }, path, !encrypted)
            {
                Receipt = encrypted && !readExternalAsset && IsReceiptEligible(path, prepared.Id, allowMissing: false)
                    ? CreateReceipt(prepared.Id, bytes) : null
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Save(MacroModel model, string path) => SaveProtected(model, path, verifiedInPlaceMigration: false);

    public SavedMacroDocument SaveWithReceipt(MacroModel model, string path)
        => SaveProtected(model, path, verifiedInPlaceMigration: false, captureReceipt: true)!;

    private SavedMacroDocument? SaveProtected(MacroModel model, string path, bool verifiedInPlaceMigration, bool captureReceipt = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        path = Path.GetFullPath(path);
        if (!Path.GetExtension(path).Equals(".zmacro", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Protected macros must use the .zmacro extension.", nameof(path));
        lock (_gate)
        {
            EnsureNoPendingRecovery();
            byte[] plain = PrepareBytes(model, Path.GetDirectoryName(path)!, out MacroModel prepared);
            // Missing keys are never silently regenerated over an existing encrypted library.
            byte[]? key = null;
            try
            {
                key = _keys.GetKey(createIfMissing: !HasProtectedLibrary());
                if (!verifiedInPlaceMigration) PreservePreviousSchema(path, key);
                byte[] ciphertext = Encrypt(plain, key, null, 1);
                try
                {
                    // Allocate/validate all returned evidence before the commit. WriteAtomic clears its
                    // ciphertext buffer; no receipt may hash a later re-read or the cleared buffer.
                    SavedMacroDocument? withoutReceipt = captureReceipt ? new(prepared, null) : null;
                    SavedMacroDocumentReceipt? receipt = captureReceipt && IsReceiptEligible(path, prepared.Id, allowMissing: true)
                        ? CreateReceipt(prepared.Id, ciphertext) : null;
                    SavedMacroDocument? withReceipt = receipt is not null ? new(prepared, receipt) : null;
                    WriteAtomic(path, ciphertext);
                    return withReceipt is not null && IsReceiptEligible(path, prepared.Id, allowMissing: false)
                        ? withReceipt : withoutReceipt;
                }
                catch
                {
                    // Also clear if evidence allocation or directory creation failed before the writer took ownership.
                    CryptographicOperations.ZeroMemory(ciphertext);
                    throw;
                }
            }
            finally { CryptographicOperations.ZeroMemory(plain); if (key is not null) CryptographicOperations.ZeroMemory(key); }
        }
    }

    public MacroDocumentLoadResult MigrateLibraryDocument(string path)
    {
        path = Path.GetFullPath(path);
        if (!IsImmediateLibraryFile(path)) throw new ArgumentException("Only owned library documents can be migrated.", nameof(path));
        lock (_gate)
        {
            MacroDocumentLoadResult loaded = Load(path);
            if (!loaded.IsLegacy) return loaded;
            string target = Path.ChangeExtension(path, ".zmacro");
            bool inPlace = path.Equals(target, StringComparison.OrdinalIgnoreCase);
            if (!inPlace && File.Exists(target))
                throw new MacroProtectionException("A protected document already uses this filename. Both originals were preserved; import the older file as a separate macro.");
            byte[] original = ReadBounded(path, MaxDecodedBytes + HeaderSize);
            byte[]? key = null;
            string rollback = Path.Combine(_rootDirectory, "Backups", "LegacyEncrypted", Guid.NewGuid().ToString("N") + ".zrollback");
            try
            {
                key = _keys.GetKey(createIfMissing: !HasProtectedLibrary());
                byte[] encryptedRollback = Encrypt(original, key, null, 3);
                WriteAtomic(rollback, encryptedRollback);
                byte[] verified = Decrypt(ReadBounded(rollback, MaxDecodedBytes + HeaderSize), key, null, 3);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), SHA256.HashData(verified)))
                        throw new MacroProtectionException("The migration rollback copy did not verify. The original was kept.");
                }
                finally { CryptographicOperations.ZeroMemory(verified); }
                SaveProtected(loaded.Model, target, verifiedInPlaceMigration: inPlace);
                MacroDocumentLoadResult protectedCopy = Load(target);
                if (_serializer.Serialize(loaded.Model) != _serializer.Serialize(protectedCopy.Model))
                    throw new MacroProtectionException("The encrypted macro did not verify. The original was kept.");
                if (!path.Equals(target, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
                return protectedCopy;
            }
            finally { CryptographicOperations.ZeroMemory(original); if (key is not null) CryptographicOperations.ZeroMemory(key); }
        }
    }

    public void ExportBackup(IReadOnlyList<MacroModel> models, string path, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(models), "A backup must contain 1–1000 macros.");
        ValidatePassphrase(passphrase);
        var array = new JsonArray();
        long totalPreparedBytes = 128; // Envelope names, array punctuation and version metadata.
        foreach (MacroModel model in models)
        {
            byte[] prepared = PrepareBytes(model, _libraryDirectory);
            try
            {
                totalPreparedBytes += prepared.Length + 1L;
                if (totalPreparedBytes > MaxDecodedBytes)
                    throw new InvalidDataException("The combined backup exceeds the 100 MB decoded archive limit. Export fewer macros together.");
                array.Add(JsonNode.Parse(prepared));
            }
            finally { CryptographicOperations.ZeroMemory(prepared); }
        }
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["backupVersion"] = 1, ["macros"] = array });
        try
        {
            CheckDecodedLength(plain.Length);
            WriteAtomic(Path.GetFullPath(path), Encrypt(plain, null, passphrase, 2));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length is < 12 or > 1024)
            throw new ArgumentException("Use a backup passphrase between 12 and 1024 characters. Keep it somewhere safe; it cannot be reset.", nameof(passphrase));
    }

    private IReadOnlyList<MacroModel> ReadBackup(byte[] plain, string path)
    {
        JsonNode node = JsonNode.Parse(plain, documentOptions: new JsonDocumentOptions { MaxDepth = 64 })
            ?? throw new InvalidDataException("The backup is empty.");
        if (node["backupVersion"]?.GetValue<int>() != 1 || node["macros"] is not JsonArray macros || macros.Count is < 1 or > 1000)
            throw new InvalidDataException("This backup version or macro count is unsupported.");
        return macros.Select(macro => PrepareModel(macro?.ToJsonString() ?? "", Path.GetDirectoryName(path)!)).ToArray();
    }

    private byte[] PrepareBytes(MacroModel model, string baseDirectory)
        => PrepareBytes(model, baseDirectory, out _);

    private byte[] PrepareBytes(MacroModel model, string baseDirectory, out MacroModel prepared)
    {
        string json = _serializer.Serialize(model);
        prepared = PrepareModel(json, baseDirectory);
        byte[] bytes = Utf8.GetBytes(_serializer.Serialize(prepared));
        CheckDecodedLength(bytes.Length);
        return bytes;
    }

    private MacroModel PrepareModel(string json, string baseDirectory)
        => PrepareModel(json, baseDirectory, out _);

    private MacroModel PrepareModel(string json, string baseDirectory, out bool readExternalAsset)
    {
        if (Utf8.GetByteCount(json) > MaxDecodedBytes) throw new InvalidDataException("The macro exceeds the 100 MB decoded document limit.");
        JsonNode node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 })
            ?? throw new InvalidDataException("The macro document is empty.");
        long embeddedBytes = 0;
        long documentBudget = Utf8.GetByteCount(json);
        readExternalAsset = false;
        EmbedImages(node, baseDirectory, ref embeddedBytes, ref documentBudget, ref readExternalAsset);
        return _serializer.Deserialize(node.ToJsonString());
    }

    private void EmbedImages(JsonNode node, string baseDirectory, ref long embeddedBytes, ref long documentBudget, ref bool readExternalAsset)
    {
        if (node is JsonObject obj)
        {
            string? pathKey = obj.Select(pair => pair.Key).FirstOrDefault(key => key.Equals("templatePath", StringComparison.OrdinalIgnoreCase));
            string? bytesKey = obj.Select(pair => pair.Key).FirstOrDefault(key => key.Equals("templateBytes", StringComparison.OrdinalIgnoreCase));
            if (pathKey is not null || bytesKey is not null)
            {
                string? encoded = bytesKey is not null && obj[bytesKey] is JsonValue encodedValue && encodedValue.TryGetValue<string>(out var value) ? value : null;
                string? imagePath = pathKey is not null && obj[pathKey] is JsonValue pathValue && pathValue.TryGetValue<string>(out var pathText) ? pathText : null;
                if (!string.IsNullOrEmpty(encoded))
                {
                    if (encoded.Length > ((long)MaxTemplateBytes + 2) / 3 * 4) throw new InvalidDataException("A target image exceeds the 32 MB image limit.");
                    byte[] decoded = Convert.FromBase64String(encoded);
                    try
                    {
                        ValidateTemplate(decoded);
                        embeddedBytes += decoded.Length;
                    }
                    finally { CryptographicOperations.ZeroMemory(decoded); }
                }
                else if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    string resolved = ResolveTemplate(imagePath, baseDirectory);
                    byte[] image = ReadBounded(resolved, MaxTemplateBytes);
                    readExternalAsset = true;
                    try
                    {
                        ValidateTemplate(image);
                        embeddedBytes += image.Length;
                        documentBudget += ((long)image.Length + 2) / 3 * 4;
                        if (documentBudget > MaxDecodedBytes)
                            throw new InvalidDataException("Embedded target images exceed the 100 MB decoded document limit. Use smaller images or fewer repeated templates.");
                        obj[bytesKey ?? "templateBytes"] = Convert.ToBase64String(image);
                    }
                    finally { CryptographicOperations.ZeroMemory(image); }
                }
                if (embeddedBytes > MaxDecodedBytes) throw new InvalidDataException("Embedded target images exceed the 100 MB document limit.");
                if (pathKey is not null && (!string.IsNullOrEmpty(encoded) || !string.IsNullOrWhiteSpace(imagePath))) obj[pathKey] = null;
            }
            foreach (JsonNode? child in obj.Select(pair => pair.Value).ToArray())
                if (child is JsonObject or JsonArray) EmbedImages(child, baseDirectory, ref embeddedBytes, ref documentBudget, ref readExternalAsset);
        }
        else if (node is JsonArray array)
            foreach (JsonNode? child in array) if (child is not null) EmbedImages(child, baseDirectory, ref embeddedBytes, ref documentBudget, ref readExternalAsset);
    }

    private string ResolveTemplate(string path, string baseDirectory)
    {
        if (Path.IsPathRooted(path)) return path;
        string[] candidates = { Path.Combine(baseDirectory, path), Path.Combine(_rootDirectory, "Templates", path),
            Path.Combine(_rootDirectory, path), Path.Combine(Path.GetDirectoryName(_rootDirectory)!, "Templates", path),
            Path.Combine(Path.GetDirectoryName(_rootDirectory)!, path) };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("A target image is missing. Restore it or replace the image before saving an encrypted copy.", path);
    }

    private static void ValidateTemplate(byte[] image)
    {
        // Do not turn an arbitrary referenced local file into an exported attachment.
        // The existing decoder bounds encoded length, dimensions and decoded pixels.
        using var decoded = OpenCV.BoundedTemplateDecoder.Decode(null, image);
    }

    private bool HasProtectedLibrary()
    {
        if (!Directory.Exists(_libraryDirectory)) return false;
        Span<byte> header = stackalloc byte[5];
        foreach (string path in Directory.EnumerateFiles(_libraryDirectory, "*.zmacro"))
        {
            using var input = File.OpenRead(path);
            if (input.ReadAtLeast(header, 5, false) == 5 && header[..4].SequenceEqual("ZMCR"u8) && header[4] == 2) return true;
        }
        return false;
    }

    private bool IsImmediateLibraryFile(string path) => Path.GetDirectoryName(path)!.Equals(_libraryDirectory, StringComparison.OrdinalIgnoreCase);

    private static SavedMacroDocumentReceipt? CreateReceipt(Guid id, byte[] ciphertext)
        => id != Guid.Empty && ciphertext.LongLength is >= SavedMacroDocumentReceipt.MinimumCiphertextLength and <= SavedMacroDocumentReceipt.MaximumCiphertextLength
            ? new SavedMacroDocumentReceipt(id, Convert.ToHexString(SHA256.HashData(ciphertext)), ciphertext.LongLength).Validated() : null;

    private bool IsReceiptEligible(string path, Guid id, bool allowMissing)
    {
        // Eligibility is evidence for this call's byte snapshot, not a held filesystem authority. Metadata
        // uncertainty only removes the receipt; it must not turn a previously successful save/load into failure.
        try
        {
            if (id == Guid.Empty || !path.Equals(Path.GetFullPath(GetLibraryPath(id)), StringComparison.OrdinalIgnoreCase)
                || !Path.GetExtension(path).Equals(".zmacro", StringComparison.OrdinalIgnoreCase)) return false;
            string? current = path; bool leaf = true;
            while (current is not null)
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0
                        || leaf == ((attributes & FileAttributes.Directory) != 0)) return false;
                }
                catch (Exception exception) when (allowMissing && (exception is FileNotFoundException or DirectoryNotFoundException))
                {
                    // A new canonical file/directory can be created by this save under existing checked ancestors.
                }
                leaf = false; current = Path.GetDirectoryName(current);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static bool IsV2(ReadOnlySpan<byte> bytes) => bytes.Length >= 5 && bytes[..4].SequenceEqual("ZMCR"u8) && bytes[4] == 2;

    private static byte[] Encrypt(byte[] plain, byte[]? localKey, string? passphrase, byte purpose)
    {
        CheckDecodedLength(plain.Length);
        byte[] compressed = Compress(plain);
        byte[] envelope = new byte[checked(HeaderSize + compressed.Length)];
        "ZMCR"u8.CopyTo(envelope); envelope[4] = 2; envelope[5] = purpose; envelope[6] = 1;
        BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan(8), purpose == 2 ? BackupIterations : 0);
        BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan(12), plain.Length);
        BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan(16), compressed.Length);
        if (purpose == 2) RandomNumberGenerator.Fill(envelope.AsSpan(20, 16));
        RandomNumberGenerator.Fill(envelope.AsSpan(36, 12));
        byte[] key = purpose == 2 ? Rfc2898DeriveBytes.Pbkdf2(passphrase!, envelope.AsSpan(20, 16), BackupIterations, HashAlgorithmName.SHA256, 32)
            : localKey?.ToArray() ?? throw new ArgumentNullException(nameof(localKey));
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Encrypt(envelope.AsSpan(36, 12), compressed, envelope.AsSpan(HeaderSize), envelope.AsSpan(AadSize, 16), envelope.AsSpan(0, AadSize));
            return envelope;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(compressed); }
    }

    private static byte[] Decrypt(byte[] envelope, byte[]? localKey, string? passphrase, byte purpose)
    {
        if (envelope.Length < HeaderSize || !IsV2(envelope) || envelope[5] != purpose || envelope[6] != 1 || envelope[7] != 0)
            throw new MacroProtectionException("The encrypted document header is invalid or unsupported.");
        int iterations = BinaryPrimitives.ReadInt32BigEndian(envelope.AsSpan(8));
        int decodedLength = BinaryPrimitives.ReadInt32BigEndian(envelope.AsSpan(12));
        int cipherLength = BinaryPrimitives.ReadInt32BigEndian(envelope.AsSpan(16));
        CheckDecodedLength(decodedLength);
        if (cipherLength < 1 || cipherLength > MaxDecodedBytes || envelope.Length != HeaderSize + cipherLength
            || iterations != (purpose == 2 ? BackupIterations : 0)) throw new MacroProtectionException("The encrypted document size or key settings are invalid.");
        if (purpose == 2 && (string.IsNullOrEmpty(passphrase) || passphrase.Length > 1024))
            throw new MacroProtectionException("Enter a backup passphrase between 1 and 1024 characters.");
        byte[] key = purpose == 2 ? Rfc2898DeriveBytes.Pbkdf2(passphrase!, envelope.AsSpan(20, 16), BackupIterations, HashAlgorithmName.SHA256, 32)
            : localKey?.ToArray() ?? throw new ArgumentNullException(nameof(localKey));
        byte[] compressed = new byte[cipherLength];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(envelope.AsSpan(36, 12), envelope.AsSpan(HeaderSize), envelope.AsSpan(AadSize, 16), compressed, envelope.AsSpan(0, AadSize));
            byte[] plain = DecompressBounded(compressed);
            if (plain.Length == decodedLength) return plain;
            CryptographicOperations.ZeroMemory(plain);
            throw new MacroProtectionException("The encrypted document length does not match its authenticated header.");
        }
        catch (CryptographicException ex)
        {
            throw new MacroProtectionException(purpose == 2
                ? "The backup passphrase is incorrect or the file was changed. The original file has been kept."
                : "This macro could not be unlocked on this Windows account, or its file was changed. Restore an encrypted backup. The original file has been kept.", ex);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(compressed); }
    }

    private static byte[] ReadLegacy(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("ZMCR"u8))
        {
            if (bytes.Length < 13 || bytes[4] != 1) throw new InvalidDataException("Unsupported macro container version.");
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(9));
            if (length < 1 || length > MaxDecodedBytes || bytes.Length != 13 + length) throw new InvalidDataException("Invalid legacy macro container length.");
            ReadOnlySpan<byte> compressed = bytes.AsSpan(13);
            if (Crc32Helper.Compute(compressed) != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)))
                throw new InvalidDataException("The legacy macro checksum failed.");
            return DecompressBounded(compressed.ToArray());
        }
        CheckDecodedLength(bytes.Length);
        // Legacy File.ReadAllText supported BOM-marked UTF-8/UTF-16/UTF-32 files.
        // Preserve that compatibility while enforcing the decoded UTF-8 budget.
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(input, Utf8, detectEncodingFromByteOrderMarks: true);
        string json = reader.ReadToEnd();
        CheckDecodedLength(Utf8.GetByteCount(json));
        return Utf8.GetBytes(json);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        try
        {
            using (var zip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(bytes);
            if (output.Length > MaxDecodedBytes) throw new InvalidDataException("The compressed document exceeds the 100 MB limit.");
            return output.ToArray();
        }
        finally { if (output.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan()); }
    }

    private static byte[] DecompressBounded(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var zip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        try
        {
            int count;
            while ((count = zip.Read(buffer)) != 0)
            {
                if (output.Length + count > MaxDecodedBytes) throw new InvalidDataException("The decoded macro exceeds the 100 MB limit.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var plaintextBuffer)) CryptographicOperations.ZeroMemory(plaintextBuffer.AsSpan());
        }
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 || input.Length > maximum) throw new InvalidDataException("The macro file is empty or exceeds the supported file-size limit.");
        byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes;
    }

    private static void CheckDecodedLength(int size)
    {
        if (size is < 1 or > MaxDecodedBytes) throw new InvalidDataException("The macro exceeds the 100 MB decoded document limit.");
    }

    private static void WriteAtomic(string path, byte[] ciphertext)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(ciphertext); output.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("An encrypted temporary macro file could not be removed ({0}).", ex.GetType().Name); }
        }
    }
}
