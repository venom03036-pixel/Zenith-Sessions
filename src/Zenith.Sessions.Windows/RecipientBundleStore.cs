using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Bounded metadata-only storage outside the application package. Atomic committed writes retain
/// one last-valid backup and one preserved damaged-original slot. No credential or account reads.
/// Await all operations before disposal; blocking platform work always retains its actual owner.
/// </summary>
public sealed class RecipientBundleStore : IRecipientBundleStore, IDisposable
{
    public const int MaximumFileBytes = 512 * 1024, MaximumPendingOperations = 4;
    private readonly object _lifetime = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _root;
    private bool _disposed;
    private int _calls;
    public string Path { get; }
    private sealed record Document(int Version, long Revision, StoredBundle[] Items);
    private sealed record StoredBundle(Guid Id, string Name, Guid[] ProfileIds, long Revision, Guid ChangeId);
    private sealed record Loaded(Document Document, bool MainValid, bool MainPresent, string? Notice);

    public RecipientBundleStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = System.IO.Path.GetFullPath(root); Path = System.IO.Path.Combine(_root, "recipient-bundles.json");
    }

    public Task<RecipientBundleSnapshot> LoadAsync(CancellationToken ct) => SerializedAsync(async token =>
    {
        var loaded = await LoadDocumentAsync(token).ConfigureAwait(false);
        return new RecipientBundleSnapshot(loaded.Document.Revision,
            Array.AsReadOnly(loaded.Document.Items.Select(ToBundle).ToArray()), loaded.Notice);
    }, ct);

    public Task<RecipientBundle> SaveAsync(RecipientBundle bundle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bundle); bundle = bundle.Validated();
        return SerializedAsync(async token =>
        {
            var loaded = await LoadDocumentAsync(token).ConfigureAwait(false); var document = loaded.Document;
            int index = Array.FindIndex(document.Items, item => item.Id == bundle.Id);
            if (index < 0 ? bundle.Revision != 0 : !Matches(document.Items[index], bundle.Revision, bundle.ChangeId))
                throw new RecipientBundleConflictException();
            if (index < 0 && document.Items.Length >= RecipientBundle.MaximumCount) throw new RecipientBundleCapacityException();
            return await SaveItemAsync(bundle, index, loaded, token).ConfigureAwait(false);
        }, ct);
    }

    public Task<RecipientBundle> RenameAsync(Guid id, long expectedRevision, Guid expectedChangeId, string name, CancellationToken ct)
    {
        RequireSavedIdentity(id, expectedRevision, expectedChangeId); name = RecipientBundle.ValidateName(name);
        return SerializedAsync(async token =>
        {
            var loaded = await LoadDocumentAsync(token).ConfigureAwait(false);
            int index = RequireCurrent(loaded.Document, id, expectedRevision, expectedChangeId);
            return await SaveItemAsync(ToBundle(loaded.Document.Items[index]) with { Name = name }, index, loaded, token).ConfigureAwait(false);
        }, ct);
    }

    public async Task DeleteAsync(Guid id, long expectedRevision, Guid expectedChangeId, CancellationToken ct)
    {
        RequireSavedIdentity(id, expectedRevision, expectedChangeId);
        await SerializedAsync(async token =>
        {
            var loaded = await LoadDocumentAsync(token).ConfigureAwait(false); var document = loaded.Document;
            int index = RequireCurrent(document, id, expectedRevision, expectedChangeId);
            RequireAdvance(document.Revision);
            var items = new StoredBundle[document.Items.Length - 1];
            Array.Copy(document.Items, 0, items, 0, index); Array.Copy(document.Items, index + 1, items, index, items.Length - index);
            await WriteDocumentAsync(new(1, document.Revision + 1, items), loaded, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    private async Task<RecipientBundle> SaveItemAsync(RecipientBundle bundle, int index, Loaded loaded, CancellationToken ct)
    {
        var document = loaded.Document; RequireAdvance(document.Revision); RequireAdvance(bundle.Revision);
        int references = bundle.ProfileIds.Count;
        for (int current = 0; current < document.Items.Length; current++)
            if (current != index) references += document.Items[current].ProfileIds.Length;
        if (references > RecipientBundle.MaximumTotalRecipientReferences) throw new RecipientBundleCapacityException();
        var saved = (bundle with { Revision = bundle.Revision + 1, ChangeId = Guid.NewGuid() }).Validated();
        var items = new StoredBundle[document.Items.Length + (index < 0 ? 1 : 0)]; document.Items.CopyTo(items, 0);
        items[index < 0 ? items.Length - 1 : index] = ToStored(saved);
        await WriteDocumentAsync(new(1, document.Revision + 1, items), loaded, ct).ConfigureAwait(false);
        return saved;
    }

    private Task<T> SerializedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_calls >= MaximumPendingOperations) throw new RecipientBundleBusyException();
            _calls++;
        }
        return OwnAsync();
        async Task<T> OwnAsync()
        {
            bool entered = false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await _writer.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
                return await Task.Run(async () =>
                {
                    deadline.Token.ThrowIfCancellationRequested(); CheckPaths(); Directory.CreateDirectory(_root);
                    await using var ownership = await AcquireFileLockAsync(deadline.Token).ConfigureAwait(false);
                    return await operation(deadline.Token).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (entered) _writer.Release();
                lock (_lifetime) _calls--;
            }
        }
    }
    private async Task<FileStream> AcquireFileLockAsync(CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested(); RejectLink(Path + ".lock");
            try { return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                if (attempt >= 99) throw new RecipientBundleBusyException();
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<Loaded> LoadDocumentAsync(CancellationToken ct)
    {
        CheckPaths(); bool mainPresent = Exists(Path);
        if (mainPresent)
        {
            try
            {
                var document = await ReadDocumentAsync(Path, ct).ConfigureAwait(false);
                return new(document, true, true, Exists(Path + ".tmp")
                    ? "An unfinished recipient bundle write was ignored. Only the committed library was loaded." : null);
            }
            catch (RecipientBundleStorageException error) when (error.Problem == RecipientBundleStorageProblem.Damaged)
            {
                if (!Exists(Path + ".bak")) throw Damaged("Recipient bundles are damaged and no valid backup is available. Existing files were preserved; review storage recovery.");
            }
        }
        if (Exists(Path + ".bak"))
        {
            var backup = await ReadDocumentAsync(Path + ".bak", ct).ConfigureAwait(false);
            return new(backup, false, mainPresent, "Loaded the last valid recipient bundle copy. Original files were preserved; save a deliberate change to repair the main file.");
        }
        if (Exists(Path + ".tmp") || Exists(Path + ".damaged"))
            throw Damaged("Recipient bundles have interrupted or damaged storage. Existing files were preserved; review recovery before creating a new library.");
        var initial = new Document(1, 1, []);
        await WriteDocumentAsync(initial, new(initial, false, false, null), ct).ConfigureAwait(false);
        return new(initial, true, true, null);
    }

    private static async Task<Document> ReadDocumentAsync(string path, CancellationToken ct)
    {
        byte[] bytes = await ReadBytesAsync(path, ct).ConfigureAwait(false);
        PreflightVersion(bytes, ct);
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 }); var root = json.RootElement;
            int version = root.GetProperty("Version").GetInt32();
            if (version != 1) throw new RecipientBundleStorageException(RecipientBundleStorageProblem.UnsupportedVersion,
                "The recipient bundle file has an unsupported version. Existing files were preserved; use a compatible Zenith Sessions version.");
            RequireProperties(root, ["Version", "Revision", "Items"]);
            long revision = root.GetProperty("Revision").GetInt64(); var data = root.GetProperty("Items");
            if (revision < 1 || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > RecipientBundle.MaximumCount) throw new InvalidDataException();
            var items = new StoredBundle[data.GetArrayLength()]; var identities = new HashSet<Guid>(); int index = 0, total = 0;
            foreach (var entry in data.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested(); RequireProperties(entry, ["Id", "Name", "ProfileIds", "Revision", "ChangeId"]);
                var members = entry.GetProperty("ProfileIds");
                if (members.ValueKind != JsonValueKind.Array || members.GetArrayLength() is < 1 or > RecipientBundle.MaximumRecipients) throw new InvalidDataException();
                total += members.GetArrayLength();
                if (total > RecipientBundle.MaximumTotalRecipientReferences) throw new InvalidDataException();
                var ids = new Guid[members.GetArrayLength()]; int member = 0;
                foreach (var id in members.EnumerateArray()) ids[member++] = id.GetGuid();
                var bundle = new RecipientBundle(entry.GetProperty("Id").GetGuid(), entry.GetProperty("Name").GetString()!, ids,
                    entry.GetProperty("Revision").GetInt64(), entry.GetProperty("ChangeId").GetGuid()).Validated();
                if (bundle.Revision < 1 || bundle.Revision > revision || !identities.Add(bundle.Id)) throw new InvalidDataException();
                items[index++] = ToStored(bundle);
            }
            return new(1, revision, items);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException)
        { throw Damaged("Recipient bundles contain an invalid or unreadable record. Existing files were preserved; review storage recovery."); }
    }
    private static bool PreflightVersion(ReadOnlySpan<byte> bytes, CancellationToken ct, bool allowCurrentCorruption = false)
    {
        // Root version authority precedes record corruption and backup fallback. Nested Version
        // fields are ordinary data, and never declare this document's storage format.
        // Inspect unfamiliar deeper data within the existing byte bound without loosening the
        // actual current-format decoder's depth limit of eight.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaximumFileBytes }); int versions = 0; bool current = false;
        try
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("Version"u8)) continue;
                if (++versions > 1) throw Unsupported();
                if (!reader.Read()) break;
                if (reader.TokenType == JsonTokenType.Number)
                {
                    if (!reader.TryGetInt32(out int version) || version != 1) throw Unsupported();
                    current = true;
                }
            }
        }
        catch (JsonException)
        {
            if (allowCurrentCorruption && current) return true;
            throw Damaged("Recipient bundles contain an invalid or unreadable record. Existing files were preserved; review storage recovery.");
        }
        return current;
        static RecipientBundleStorageException Unsupported() => new(RecipientBundleStorageProblem.UnsupportedVersion,
            "The recipient bundle file has an unsupported or ambiguous version. Existing files were preserved; use a compatible Zenith Sessions version.");
    }
    private static void RequireProperties(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidDataException();
        if (seen.Count != expected.Length) throw new InvalidDataException();
    }
    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken ct, bool allowEmpty = false)
    {
        RejectLink(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaximumFileBytes || !allowEmpty && file.Length == 0) throw Damaged("Recipient bundle storage is empty or exceeds its bounded file size. Existing files were preserved.");
        var bytes = new byte[(int)file.Length];
        try { await file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw Damaged("Recipient bundle storage was truncated during reading. Existing files were preserved; retry recovery."); }
        return bytes;
    }
    private async Task WriteDocumentAsync(Document document, Loaded previous, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        if (bytes.Length > MaximumFileBytes) throw new RecipientBundleCapacityException();
        CheckPaths();
        // Loading a valid primary need not consult its backup. A mutation does: only a
        // positively declared current-format backup may be replaced by the accepted main.
        if (Exists(Path + ".bak"))
        {
            byte[] backup = await ReadBytesAsync(Path + ".bak", ct).ConfigureAwait(false);
            if (!PreflightVersion(backup, ct, allowCurrentCorruption: previous.MainValid))
                throw Damaged("The recipient bundle backup's storage version could not be confirmed. Existing files were preserved; review the backup before saving.");
        }
        if (previous.MainPresent && !previous.MainValid) await PreserveDamagedAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); string temporary = Path + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
        { await file.WriteAsync(bytes, ct).ConfigureAwait(false); await file.FlushAsync(ct).ConfigureAwait(false); file.Flush(true); }
        ct.ThrowIfCancellationRequested(); CheckPaths();
        if (previous.MainPresent)
        {
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { File.Replace(temporary, Path, previous.MainValid ? Path + ".bak" : null); break; }
                catch (IOException error) when (((error.HResult & 0xffff) is 32 or 33 or 1175) && attempt < 9 && Exists(temporary) && Exists(Path))
                { await Task.Delay(25, ct).ConfigureAwait(false); }
            }
        }
        else File.Move(temporary, Path);
    }
    private async Task PreserveDamagedAsync(CancellationToken ct)
    {
        byte[] original;
        try { original = await ReadBytesAsync(Path, ct, allowEmpty: true).ConfigureAwait(false); }
        catch (RecipientBundleStorageException)
        { throw new RecipientBundleStorageException(RecipientBundleStorageProblem.RecoveryBlocked, "The damaged recipient bundle file exceeds the recovery-copy bound. Preserve it outside the data folder before repairing storage."); }
        string damaged = Path + ".damaged";
        if (Exists(damaged))
        {
            byte[] previous;
            try { previous = await ReadBytesAsync(damaged, ct, allowEmpty: true).ConfigureAwait(false); }
            catch (RecipientBundleStorageException)
            { throw new RecipientBundleStorageException(RecipientBundleStorageProblem.RecoveryBlocked, "The damaged-original recovery slot is occupied. Preserve it outside the data folder before another repair."); }
            if (!original.AsSpan().SequenceEqual(previous))
                throw new RecipientBundleStorageException(RecipientBundleStorageProblem.RecoveryBlocked, "The damaged-original recovery slot is occupied. Preserve it outside the data folder before another repair.");
            return;
        }
        await using var copy = new FileStream(damaged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await copy.WriteAsync(original, ct).ConfigureAwait(false); await copy.FlushAsync(ct).ConfigureAwait(false); copy.Flush(true);
    }

    private void CheckPaths()
    {
        for (string? current = _root; current is not null; current = System.IO.Path.GetDirectoryName(current)) RejectLink(current);
        foreach (string suffix in new[] { "", ".bak", ".tmp", ".damaged", ".lock" }) RejectLink(Path + suffix);
    }
    private static void RejectLink(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        // This pre-lease check may overlap another instance's atomic replacement.
        // Inspect one observation; do not require an earlier existence probe to stay current.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recipient bundle storage cannot use redirected files or directories.");
    }
    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static int RequireCurrent(Document document, Guid id, long revision, Guid change)
    {
        int index = Array.FindIndex(document.Items, item => item.Id == id);
        if (index < 0 || !Matches(document.Items[index], revision, change)) throw new RecipientBundleConflictException();
        return index;
    }
    private static bool Matches(StoredBundle item, long revision, Guid change) => item.Revision == revision && item.ChangeId == change;
    private static void RequireSavedIdentity(Guid id, long revision, Guid change)
    { if (id == Guid.Empty || revision < 1 || change == Guid.Empty) throw new ArgumentException("A saved recipient bundle identity, revision and change identity are required."); }
    private static void RequireAdvance(long revision)
    { if (revision == long.MaxValue) throw Damaged("Recipient bundle revisions cannot advance. Existing files were preserved; review storage recovery."); }
    private static RecipientBundle ToBundle(StoredBundle item) => new RecipientBundle(item.Id, item.Name, item.ProfileIds, item.Revision, item.ChangeId).Validated();
    private static StoredBundle ToStored(RecipientBundle item) => new(item.Id, item.Name, item.ProfileIds.ToArray(), item.Revision, item.ChangeId);
    private static RecipientBundleStorageException Damaged(string message) => new(RecipientBundleStorageProblem.Damaged, message);
    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            if (_calls != 0) throw new InvalidOperationException("Cancel and await recipient bundle operations before disposing their store.");
            _disposed = true; _writer.Dispose();
        }
    }
}
