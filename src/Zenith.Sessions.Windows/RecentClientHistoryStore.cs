using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed class RecentClientHistoryStorageException(string message) : Exception(message);

/// <summary>One caller-owned serialized request at a time; no worker, timer, running-state restore or profile/vault IO.</summary>
public sealed class RecentClientHistoryStore : ISessionHistoryStore, IDisposable
{
    public const int MaximumDatabaseBytes = 2 * 1024 * 1024, MaximumEntryBytes = 2048;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private bool _disposed;
    public string Path { get; }
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Stored(Guid EntryId, string? HostId, int Pid, long Start, long First, long Last, long Absent,
        long? Close, long? User, long? UserTime, long? Place, Guid? Job, long? Universe, long? DestinationTime);
    public RecentClientHistoryStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(root), "client-history.db");
    }
    public Task<RecentClientHistorySnapshot> LoadAsync(CancellationToken ct) => SerializedAsync(() => Load(ct), ct);
    public async Task SaveAsync(RecentClientHistorySnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot); snapshot.Validated();
        // Freeze caller collections before yielding. Only ended observations cross the storage boundary.
        var frozen = new RecentClientHistorySnapshot(snapshot.Version, snapshot.Revision, Array.AsReadOnly(snapshot.Entries.ToArray()));
        await SerializedAsync(() => { Save(frozen, ct); return true; }, ct).ConfigureAwait(false);
    }
    private async Task<T> SerializedAsync<T>(Func<T> operation, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this); ct.ThrowIfCancellationRequested();
                try { return operation(); }
                catch (OperationCanceledException) { throw; }
                catch (RecentClientHistoryStorageException) { throw; }
                catch (Exception error) when (error is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OverflowException or FormatException or InvalidCastException or NotSupportedException)
                { throw new RecentClientHistoryStorageException("Client history could not be read or saved. Existing files were preserved; retry explicitly or review this separate history database."); }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    private bool Exists()
    {
        try
        {
            if ((File.GetAttributes(Path) & FileAttributes.Directory) != 0) throw new InvalidDataException();
            foreach (string file in new[] { Path, Path + "-wal", Path + "-journal", Path + "-shm" })
                try { if (new FileInfo(file).Length > MaximumDatabaseBytes) throw new InvalidDataException(); }
                catch (FileNotFoundException) { }
            return true;
        }
        catch (FileNotFoundException)
        {
            if (File.Exists(Path + "-wal") || File.Exists(Path + "-journal") || File.Exists(Path + "-shm")) throw new InvalidDataException();
            return false;
        }
        catch (DirectoryNotFoundException) { return false; }
    }
    private SqliteConnection Open(SqliteOpenMode mode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = mode, Pooling = false, DefaultTimeout = 2 }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }
    private RecentClientHistorySnapshot Load(CancellationToken ct)
    {
        if (!Exists()) return RecentClientHistorySnapshot.Empty;
        using var db = Open(SqliteOpenMode.ReadOnly, ct);
        using var tx = db.BeginTransaction(deferred: true);
        return Read(db, tx, ct);
    }
    private static RecentClientHistorySnapshot Read(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidDataException();
        command.CommandText = "PRAGMA quick_check(1)";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException();
        command.CommandText = "SELECT type,name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' LIMIT 4";
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var schema = command.ExecuteReader())
            while (schema.Read())
                if (schema.GetString(0) != "table" || !names.Add(schema.GetString(1))) throw new InvalidDataException();
        if (!names.SetEquals(new[] { "history_meta", "history_entries" })) throw new InvalidDataException();
        command.CommandText = "SELECT revision FROM history_meta WHERE id=1 LIMIT 2";
        long revision;
        using (var meta = command.ExecuteReader())
        { if (!meta.Read()) throw new InvalidDataException(); revision = meta.GetInt64(0); if (meta.Read()) throw new InvalidDataException(); }
        command.CommandText = "SELECT count(*) FROM history_meta";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidDataException();
        command.CommandText = "SELECT ordinal,length(CAST(payload AS BLOB)),payload FROM history_entries ORDER BY ordinal LIMIT 101";
        var entries = new List<RecentClientHistoryEntry>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= RecentClientHistoryReducer.MaximumEnded || rows.GetInt64(0) != entries.Count
                    || rows.GetInt64(1) is < 1 or > MaximumEntryBytes) throw new InvalidDataException();
                string payload = rows.GetString(2);
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 3 });
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.RootElement.EnumerateObject()) if (!fields.Add(property.Name)) throw new InvalidDataException();
                if (fields.Count != 14) throw new InvalidDataException();
                var stored = JsonSerializer.Deserialize<Stored>(payload, Json) ?? throw new InvalidDataException();
                entries.Add(ToEntry(stored));
            }
        ct.ThrowIfCancellationRequested();
        return new RecentClientHistorySnapshot(1, revision, Array.AsReadOnly(entries.ToArray())).Validated();
    }
    private void Save(RecentClientHistorySnapshot snapshot, CancellationToken ct)
    {
        bool existing = Exists();
        // Validate any existing database read-only before opening a writing connection. Never repair an unknown/corrupt file.
        if (existing) _ = Load(ct);
        else Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var db = Open(existing ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate, ct);
        using (var bounds = db.CreateCommand())
        {
            // This store uses bounded rollback journals, not a WAL that can grow behind a retained reader.
            bounds.CommandText = "PRAGMA journal_mode=DELETE";
            if (!string.Equals(bounds.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            bounds.CommandText = "PRAGMA page_size"; int pageSize = Convert.ToInt32(bounds.ExecuteScalar());
            if (pageSize is < 512 or > 65536) throw new InvalidDataException();
            bounds.CommandText = "PRAGMA max_page_count=" + (MaximumDatabaseBytes / pageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
            bounds.ExecuteScalar();
            bounds.CommandText = "PRAGMA journal_size_limit=" + MaximumDatabaseBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            bounds.ExecuteScalar();
        }
        using var tx = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = tx;
        RecentClientHistorySnapshot previous;
        if (existing) previous = Read(db, tx, ct);
        else
        {
            command.CommandText = "CREATE TABLE history_meta(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL); CREATE TABLE history_entries(ordinal INTEGER PRIMARY KEY,payload TEXT NOT NULL); PRAGMA user_version=1; INSERT INTO history_meta VALUES(1,0);";
            command.ExecuteNonQuery(); previous = RecentClientHistorySnapshot.Empty;
        }
        if (snapshot.Revision < previous.Revision || snapshot.Revision == previous.Revision && !snapshot.Entries.SequenceEqual(previous.Entries))
            throw new RecentClientHistoryStorageException("A newer or different client history revision is already stored. Existing history was preserved; reload before replacing it.");
        command.CommandText = "DELETE FROM history_entries"; command.ExecuteNonQuery();
        for (int index = 0; index < snapshot.Entries.Count; index++)
        {
            ct.ThrowIfCancellationRequested(); string payload = JsonSerializer.Serialize(ToStored(snapshot.Entries[index]), Json);
            if (Encoding.UTF8.GetByteCount(payload) > MaximumEntryBytes) throw new InvalidDataException();
            command.CommandText = "INSERT INTO history_entries(ordinal,payload) VALUES($ordinal,$payload)";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$ordinal", index); command.Parameters.AddWithValue("$payload", payload); command.ExecuteNonQuery();
        }
        command.CommandText = "UPDATE history_meta SET revision=$revision WHERE id=1"; command.Parameters.Clear(); command.Parameters.AddWithValue("$revision", snapshot.Revision); command.ExecuteNonQuery();
        ct.ThrowIfCancellationRequested(); tx.Commit();
    }
    private static Stored ToStored(RecentClientHistoryEntry entry) => new(entry.EntryId, entry.Process.HostId, entry.Process.ProcessId,
        entry.Process.ProcessStartUtcTicks, entry.FirstObservedUtc.UtcTicks, entry.LastObservedUtc.UtcTicks, entry.NoLongerObservedUtc!.Value.UtcTicks,
        entry.CloseRequestedUtc?.UtcTicks, entry.LastKnownUserId, entry.LastKnownUserIdentityUtc?.UtcTicks,
        entry.LastKnownDestination?.PlaceId, entry.LastKnownDestination?.JobId, entry.LastKnownDestination?.UniverseId, entry.LastKnownDestination?.ObservedUtc.UtcTicks);
    private static RecentClientHistoryEntry ToEntry(Stored item)
    {
        if ((item.Place.HasValue || item.Job.HasValue || item.Universe.HasValue || item.DestinationTime.HasValue) && !(item.Place.HasValue && item.Job.HasValue && item.DestinationTime.HasValue)) throw new InvalidDataException();
        return new RecentClientHistoryEntry(item.EntryId, new(item.HostId, item.Pid, item.Start), Utc(item.First), Utc(item.Last), Utc(item.Absent),
            item.Close is { } close ? Utc(close) : null, item.User, item.UserTime is { } userTime ? Utc(userTime) : null,
            item.Place is { } place ? new(place, item.Job!.Value, item.Universe, Utc(item.DestinationTime!.Value)) : null).Validated(ended: true);
    }
    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
    public void Dispose()
    {
        if (_disposed) return;
        if (!_serial.Wait(0)) throw new InvalidOperationException("Await the owned history operation before disposing its store.");
        _disposed = true; _serial.Release(); _serial.Dispose();
    }
}
