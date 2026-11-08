using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Saved private servers in one DPAPI-protected file (current user) under the writable data folder. The clear document
/// exists only in memory and its buffers are zeroed after use; a damaged file is reported and never overwritten. One
/// writer per process plus a cross-process lock file, like the favorites store.
/// </summary>
public sealed class PrivateServerStore : IPrivateServerStore, IDisposable
{
    public const int MaximumFileBytes = 256 * 1024;
    /// <summary>Version 2 adds each server's GameName (string or null). Version 1 files are read as unnamed and rewritten as version 2 on the next save or removal.</summary>
    private const int CurrentVersion = 2;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Zenith.Sessions.PrivateServers.v1");
    private static readonly string[] Version1Properties = ["Id", "Name", "PlaceId", "UniverseId", "OwnerUserId", "Code", "Revision"];
    private static readonly string[] Version2Properties = [.. Version1Properties, "GameName"];
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _root;
    private int _disposed;
    public string Path { get; }
    private sealed record Document(int Version, long Revision, StoredServer[] Items);
    /// <summary>Holds the clear link code in memory between the protected holder and the protected file. Its text form is
    /// redacted like <see cref="SavedPrivateServer"/>'s so no diagnostic or message can print the code or the user's name.</summary>
    private sealed record StoredServer(Guid Id, string Name, long PlaceId, long UniverseId, long? OwnerUserId, string Code, long Revision, string? GameName)
    {
        public override string ToString() => "Stored private server { Place = " + PlaceId.ToString(CultureInfo.InvariantCulture)
            + ", Revision = " + Revision.ToString(CultureInfo.InvariantCulture) + ", Name = [omitted], Code = [protected] }";
    }

    public PrivateServerStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The private servers data directory is required.", nameof(root));
        _root = System.IO.Path.GetFullPath(root); Path = System.IO.Path.Combine(_root, "private-servers.dat");
    }

    public Task<IReadOnlyList<SavedPrivateServer>> ReadAsync(CancellationToken ct) => SerializedAsync(async token =>
    {
        var document = await LoadAsync(token).ConfigureAwait(false);
        return (IReadOnlyList<SavedPrivateServer>)Array.AsReadOnly(document.Items.Select(ToSaved).ToArray());
    }, ct);

    public Task<SavedPrivateServer> AddAsync(string name, long placeId, long universeId, long? ownerUserId, string? gameName, ProtectedPrivateServerCode code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        var draft = new SavedPrivateServer(Guid.NewGuid(), SavedPrivateServer.NormalizeName(name), placeId, universeId, ownerUserId, code.Last4, 0, gameName).Validated();
        return SerializedAsync(async token =>
        {
            var document = await LoadAsync(token).ConfigureAwait(false);
            // The clear code is a transient string between the protected holder and the protected file, like a cookie header.
            string value = ""; code.Reveal(span => value = new string(span));
            // A holder cleared while it was being copied would leave a torn code; persisting it would make the whole
            // file unreadable (it is never overwritten once damaged). Refuse it before anything is written.
            if (!IsStorableCode(value, draft.Last4))
                throw new InvalidOperationException("The private server code was cleared while it was being saved. Nothing was written.");
            // The link code is the private server's identity: one saved row per code, checked under the writer and file lock
            // so two windows cannot both add it. Files written before this rule may hold copies; they still read, and the first is named.
            if (Array.Find(document.Items, item => string.Equals(item.Code, value, StringComparison.Ordinal)) is { } existing)
                throw new PrivateServerDuplicateException(ToSaved(existing));
            if (document.Items.Length >= SavedPrivateServer.MaximumCount) throw new PrivateServerCapacityException();
            if (document.Revision == long.MaxValue) throw Damaged("Private server revisions cannot advance. Existing files were preserved.");
            var saved = draft with { Revision = 1 };
            var items = new StoredServer[document.Items.Length + 1]; document.Items.CopyTo(items, 0);
            items[^1] = new(saved.Id, saved.Name, saved.PlaceId, saved.UniverseId, saved.OwnerUserId, value, saved.Revision, saved.GameName);
            await WriteAsync(new(CurrentVersion, document.Revision + 1, items), token).ConfigureAwait(false);
            return saved;
        }, ct);
    }

    public async Task DeleteAsync(Guid id, long revision, CancellationToken ct)
    {
        if (id == Guid.Empty || revision < 1) throw new ArgumentException("A saved private server identity and revision are required.");
        await SerializedAsync(async token =>
        {
            var document = await LoadAsync(token).ConfigureAwait(false);
            int index = Array.FindIndex(document.Items, item => item.Id == id);
            if (index < 0 || document.Items[index].Revision != revision) throw new PrivateServerConflictException();
            if (document.Revision == long.MaxValue) throw Damaged("Private server revisions cannot advance. Existing files were preserved.");
            var items = new StoredServer[document.Items.Length - 1];
            Array.Copy(document.Items, 0, items, 0, index); Array.Copy(document.Items, index + 1, items, index, items.Length - index);
            await WriteAsync(new(CurrentVersion, document.Revision + 1, items), token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public Task<ProtectedPrivateServerCode> ReadCodeAsync(Guid id, CancellationToken ct) => SerializedAsync(async token =>
    {
        var document = await LoadAsync(token).ConfigureAwait(false);
        var item = Array.Find(document.Items, item => item.Id == id) ?? throw new PrivateServerConflictException();
        return new ProtectedPrivateServerCode(item.Code);
    }, ct);

    public async Task<PrivateServerLaunchLease> AcquireForLaunchAsync(PrivateServerReference reference, long placeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.IsValid || placeId <= 0) throw new PrivateServerConflictException();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        FileStream? ownership = null;
        try
        {
            ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            Directory.CreateDirectory(_root);
            ownership = await AcquireFileLockAsync(ct).ConfigureAwait(false);
            var document = await LoadAsync(ct).ConfigureAwait(false);
            var item = Array.Find(document.Items, item => item.Id == reference.Id && item.Revision == reference.Revision && item.PlaceId == placeId)
                ?? throw new PrivateServerConflictException();
            ct.ThrowIfCancellationRequested();
            var heldFile = ownership;
            var lease = new PrivateServerLaunchLease(reference, placeId, new ProtectedPrivateServerCode(item.Code),
                () => { try { heldFile.Dispose(); } finally { _writer.Release(); } });
            ownership = null; return lease;
        }
        catch { ownership?.Dispose(); _writer.Release(); throw; }
    }

    private async Task<T> SerializedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Directory/replace/durable flush calls are blocking platform operations, bounded to this one owned request.
            return await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                Directory.CreateDirectory(_root);
                await using var ownership = await AcquireFileLockAsync(ct).ConfigureAwait(false);
                return await operation(ct).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }
    private async Task<FileStream> AcquireFileLockAsync(CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException ex) when (((ex.HResult & 0xffff) is 32 or 33) && attempt < 99)
            { await Task.Delay(TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false); }
        }
    }

    private async Task<Document> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(Path)) return new(1, 1, []);
        byte[] protectedBytes;
        await using (var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (file.Length > MaximumFileBytes) throw Damaged("Saved private servers exceed the bounded storage size. Existing files were preserved.");
            protectedBytes = new byte[(int)file.Length];
            try { await file.ReadExactlyAsync(protectedBytes, ct).ConfigureAwait(false); }
            catch (EndOfStreamException) { throw Damaged("Saved private servers were truncated while reading. Existing files were preserved."); }
        }
        byte[] clear;
        try { clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw Damaged("Saved private servers could not be unprotected for this Windows user. Existing files were preserved."); }
        try { return Parse(clear, ct); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static Document Parse(byte[] clear, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(clear, new JsonDocumentOptions { MaxDepth = 8 }); var root = json.RootElement;
            RequireProperties(root, ["Version", "Revision", "Items"]);
            int version = root.GetProperty("Version").GetInt32();
            if (version is not (1 or CurrentVersion))
                throw new PrivateServerStorageException(PrivateServerStorageProblem.UnsupportedVersion, "The saved private servers file has an unsupported version. Existing files were preserved.");
            long revision = root.GetProperty("Revision").GetInt64(); var data = root.GetProperty("Items");
            if (revision < 1 || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > SavedPrivateServer.MaximumCount) throw new InvalidDataException();
            var items = new StoredServer[data.GetArrayLength()]; var ids = new HashSet<Guid>(); int index = 0;
            foreach (var entry in data.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested(); RequireProperties(entry, version == 1 ? Version1Properties : Version2Properties);
                long? owner = entry.GetProperty("OwnerUserId").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("OwnerUserId").GetInt64();
                // Version 1 servers were saved before game names were kept; the list shows their place instead.
                string? gameName = version == 1 || entry.GetProperty("GameName").ValueKind == JsonValueKind.Null ? null
                    : entry.GetProperty("GameName").GetString() ?? throw new InvalidDataException();
                string code = entry.GetProperty("Code").GetString() ?? throw new InvalidDataException();
                using var holder = new ProtectedPrivateServerCode(code); // Validates the code's shape without keeping it (the same rule AddAsync writes with).
                var stored = new StoredServer(entry.GetProperty("Id").GetGuid(), entry.GetProperty("Name").GetString() ?? "", entry.GetProperty("PlaceId").GetInt64(),
                    entry.GetProperty("UniverseId").GetInt64(), owner, code, entry.GetProperty("Revision").GetInt64(), gameName);
                ToSaved(stored).Validated();
                if (stored.Revision < 1 || stored.Revision > revision || !ids.Add(stored.Id)) throw new InvalidDataException();
                items[index++] = stored;
            }
            return new(version, revision, items);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException)
        { throw Damaged("Saved private servers contain an invalid or unreadable record. Existing files were preserved."); }
    }
    private static void RequireProperties(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidDataException();
        if (seen.Count != expected.Length) throw new InvalidDataException();
    }

    private async Task WriteAsync(Document document, CancellationToken ct)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(document); byte[] protectedBytes;
        try
        {
            if (clear.Length > MaximumFileBytes) throw Damaged("Saved private servers exceed the bounded storage size. Existing files were preserved.");
            protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        ct.ThrowIfCancellationRequested(); string temporary = Path + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await file.WriteAsync(protectedBytes, ct).ConfigureAwait(false); await file.FlushAsync(ct).ConfigureAwait(false); file.Flush(true);
        }
        ct.ThrowIfCancellationRequested();
        if (File.Exists(Path))
        {
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { File.Replace(temporary, Path, null); break; }
                catch (IOException ex) when (((ex.HResult & 0xffff) is 32 or 33 or 1175) && attempt < 9 && File.Exists(temporary) && File.Exists(Path))
                { await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false); }
            }
        }
        else File.Move(temporary, Path);
    }

    /// <summary>A code is storable when the holder's own shape rule accepts it (the rule <see cref="Parse"/> reads it back
    /// with, so the store can never write a code its next read rejects) and its last four are the ones the row shows. The
    /// shape refusal is caught here so it reads as a torn code, not as a bad server name.</summary>
    private static bool IsStorableCode(string value, string last4)
    {
        try
        {
            using var check = new ProtectedPrivateServerCode(value);
            return string.Equals(check.Last4, last4, StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }
    private static SavedPrivateServer ToSaved(StoredServer item) => new(item.Id, item.Name, item.PlaceId, item.UniverseId, item.OwnerUserId, item.Code[^4..], item.Revision, item.GameName);
    private static PrivateServerStorageException Damaged(string message) => new(PrivateServerStorageProblem.Damaged, message);
    /// <summary>Dispose after the caller has awaited all operations; this store owns no persistent worker.</summary>
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _writer.Dispose(); }
}
