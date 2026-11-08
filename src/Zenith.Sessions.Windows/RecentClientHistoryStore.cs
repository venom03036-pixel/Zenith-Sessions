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
    // Store format 1 stays readable and writable by builds that predate end evidence: they require format 1, exactly these
    // two tables and payloads of exactly the fourteen original fields, and they select and insert only ordinal and payload.
    // So the payload always keeps those fourteen fields, and end evidence goes in the optional per-row evidence column,
    // NULL when there is none. An earlier build therefore reads and clears this history; when it saves, the rows it
    // rewrites lose their evidence (their cause reads Unknown again). Payloads with the evidence fields inline, which the
    // first end-evidence build wrote, are still read and are rewritten in the fourteen-field shape on the next save.
    private sealed record Stored(Guid EntryId, string? HostId, int Pid, long Start, long First, long Last, long Absent,
        long? Close, long? User, long? UserTime, long? Place, Guid? Job, long? Universe, long? DestinationTime,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Hung = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Disconnected = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Reason = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Checked = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Complete = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Fault = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? Code = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Dumps = null)
    {
        public Evidence InlineEvidence() => new(Hung, Disconnected, Reason, Checked, Complete, Fault, Code, Dumps);
    }
    private sealed record Evidence(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Hung = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Disconnected = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Reason = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Checked = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Complete = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Fault = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? Code = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Dumps = null)
    {
        public static Evidence None { get; } = new();
    }
    private const string EvidenceColumnQuery = "SELECT count(*) FROM pragma_table_info('history_entries') WHERE name='evidence'";
    private static readonly string[] RequiredFields = ["EntryId", "HostId", "Pid", "Start", "First", "Last", "Absent", "Close", "User", "UserTime", "Place", "Job", "Universe", "DestinationTime"];
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
        command.CommandText = EvidenceColumnQuery;
        bool evidenceColumn = Convert.ToInt64(command.ExecuteScalar()) == 1;
        command.CommandText = "SELECT ordinal,length(CAST(payload AS BLOB)),payload,"
            + (evidenceColumn ? "length(CAST(evidence AS BLOB)),evidence" : "NULL,NULL") + " FROM history_entries ORDER BY ordinal LIMIT 101";
        var entries = new List<RecentClientHistoryEntry>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= RecentClientHistoryReducer.MaximumEnded || rows.GetInt64(0) != entries.Count
                    || rows.GetInt64(1) is < 1 or > MaximumEntryBytes) throw new InvalidDataException();
                string payload = rows.GetString(2);
                var fields = FieldNames(payload);
                if (!RequiredFields.All(fields.Contains)) throw new InvalidDataException();
                var stored = JsonSerializer.Deserialize<Stored>(payload, Json) ?? throw new InvalidDataException();
                var evidence = stored.InlineEvidence();
                if (!rows.IsDBNull(4))
                {
                    // Evidence is in the payload or in its column, never both, and a stored column value is never empty.
                    if (rows.GetInt64(3) is < 1 or > MaximumEntryBytes || fields.Count != RequiredFields.Length) throw new InvalidDataException();
                    string column = rows.GetString(4);
                    if (FieldNames(column).Count == 0) throw new InvalidDataException();
                    evidence = JsonSerializer.Deserialize<Evidence>(column, Json) ?? throw new InvalidDataException();
                }
                entries.Add(ToEntry(stored, evidence));
            }
        ct.ThrowIfCancellationRequested();
        return new RecentClientHistorySnapshot(1, revision, Array.AsReadOnly(entries.ToArray())).Validated();
    }
    private static HashSet<string> FieldNames(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 3 });
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject()) if (!fields.Add(property.Name)) throw new InvalidDataException();
        return fields;
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
        if (existing)
        {
            previous = Read(db, tx, ct);
            command.CommandText = EvidenceColumnQuery;
            if (Convert.ToInt64(command.ExecuteScalar()) == 0) { command.CommandText = "ALTER TABLE history_entries ADD COLUMN evidence TEXT"; command.ExecuteNonQuery(); }
        }
        else
        {
            command.CommandText = "CREATE TABLE history_meta(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL); CREATE TABLE history_entries(ordinal INTEGER PRIMARY KEY,payload TEXT NOT NULL,evidence TEXT); PRAGMA user_version=1; INSERT INTO history_meta VALUES(1,0);";
            command.ExecuteNonQuery(); previous = RecentClientHistorySnapshot.Empty;
        }
        if (snapshot.Revision < previous.Revision || snapshot.Revision == previous.Revision && !snapshot.Entries.SequenceEqual(previous.Entries))
            throw new RecentClientHistoryStorageException("A newer or different client history revision is already stored. Existing history was preserved; reload before replacing it.");
        command.CommandText = "DELETE FROM history_entries"; command.ExecuteNonQuery();
        for (int index = 0; index < snapshot.Entries.Count; index++)
        {
            ct.ThrowIfCancellationRequested(); var entry = snapshot.Entries[index];
            string payload = JsonSerializer.Serialize(ToStored(entry), Json);
            var end = ToEvidence(entry); string? evidence = end == Evidence.None ? null : JsonSerializer.Serialize(end, Json);
            if (Encoding.UTF8.GetByteCount(payload) > MaximumEntryBytes || evidence is not null && Encoding.UTF8.GetByteCount(evidence) > MaximumEntryBytes) throw new InvalidDataException();
            command.CommandText = "INSERT INTO history_entries(ordinal,payload,evidence) VALUES($ordinal,$payload,$evidence)";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$ordinal", index); command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$evidence", (object?)evidence ?? DBNull.Value); command.ExecuteNonQuery();
        }
        command.CommandText = "UPDATE history_meta SET revision=$revision WHERE id=1"; command.Parameters.Clear(); command.Parameters.AddWithValue("$revision", snapshot.Revision); command.ExecuteNonQuery();
        ct.ThrowIfCancellationRequested(); tx.Commit();
    }
    private static Stored ToStored(RecentClientHistoryEntry entry) => new(entry.EntryId, entry.Process.HostId, entry.Process.ProcessId,
        entry.Process.ProcessStartUtcTicks, entry.FirstObservedUtc.UtcTicks, entry.LastObservedUtc.UtcTicks, entry.NoLongerObservedUtc!.Value.UtcTicks,
        entry.CloseRequestedUtc?.UtcTicks, entry.LastKnownUserId, entry.LastKnownUserIdentityUtc?.UtcTicks,
        entry.LastKnownDestination?.PlaceId, entry.LastKnownDestination?.JobId, entry.LastKnownDestination?.UniverseId, entry.LastKnownDestination?.ObservedUtc.UtcTicks);
    private static Evidence ToEvidence(RecentClientHistoryEntry entry) => new(entry.NotRespondingUtc?.UtcTicks, entry.DisconnectedUtc?.UtcTicks,
        entry.DisconnectReason, entry.FaultCheck?.CheckedUtc.UtcTicks, entry.FaultCheck?.Complete, entry.FaultCheck?.FaultRecordedUtc?.UtcTicks,
        entry.FaultCheck?.ExceptionCode, entry.FaultCheck?.CrashDumps);
    private static RecentClientHistoryEntry ToEntry(Stored item, Evidence end)
    {
        if ((item.Place.HasValue || item.Job.HasValue || item.Universe.HasValue || item.DestinationTime.HasValue) && !(item.Place.HasValue && item.Job.HasValue && item.DestinationTime.HasValue)) throw new InvalidDataException();
        if (end.Checked.HasValue != end.Complete.HasValue || end.Checked.HasValue != end.Dumps.HasValue
            || !end.Checked.HasValue && (end.Fault.HasValue || end.Code.HasValue)) throw new InvalidDataException();
        return new RecentClientHistoryEntry(item.EntryId, new(item.HostId, item.Pid, item.Start), Utc(item.First), Utc(item.Last), Utc(item.Absent),
            item.Close is { } close ? Utc(close) : null, item.User, item.UserTime is { } userTime ? Utc(userTime) : null,
            item.Place is { } place ? new(place, item.Job!.Value, item.Universe, Utc(item.DestinationTime!.Value)) : null,
            end.Hung is { } hung ? Utc(hung) : null, end.Disconnected is { } disconnected ? Utc(disconnected) : null, end.Reason,
            end.Checked is { } checkedAt ? new(Utc(checkedAt), end.Complete!.Value, end.Fault is { } fault ? Utc(fault) : null, end.Code, end.Dumps!.Value) : null)
            .Validated(ended: true);
    }
    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
    public void Dispose()
    {
        if (_disposed) return;
        if (!_serial.Wait(0)) throw new InvalidOperationException("Await the owned history operation before disposing its store.");
        _disposed = true; _serial.Release(); _serial.Dispose();
    }
}
