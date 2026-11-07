using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Separate bounded metadata database. A successful commit is returned even if cancellation follows it.</summary>
public sealed class MacroAssignmentStore : IMacroAssignmentStore, IAsyncDisposable
{
    public const int MaximumAssignments = MacroAssignment.MaximumCount, MaximumDatabaseBytes = 16 * 1024 * 1024, MaximumPendingOperations = 4;
    public const int ApplicationId = 0x5A4D4131;
    private const string Columns = "kind,target_id,target_name,macro_id,macro_name";
    private readonly string _root;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _workers = [];
    private Task? _dispose;
    private bool _disposed;
    private readonly Action<NewPrimaryCheckpoint>? _newPrimaryCheckpoint;
    public string Path { get; }

    // Owned-fixture seam only: no connection/path/permission override. Production has no callback.
    internal enum NewPrimaryCheckpoint
    { CreatedBeforeOpen, BeforeUncommittedCleanup, MissingPrimaryBeforeUnownedSidecars, UnownedJournalAttributesObserved }
    internal MacroAssignmentStore(string root, Action<NewPrimaryCheckpoint> checkpoint) : this(root)
        => _newPrimaryCheckpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));

    public MacroAssignmentStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = System.IO.Path.GetFullPath(root);
        if (_root.StartsWith("\\\\", StringComparison.Ordinal)) throw Storage(MacroAssignmentStorageProblem.UnsafePath);
        Path = System.IO.Path.Combine(_root, "macro-assignments.db");
    }

    public Task<MacroAssignmentPage> ReadAsync(MacroAssignmentQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query); query.Validated();
        return OwnedAsync(token => Read(query, token), ct);
    }
    public Task<MacroAssignmentWriteResult> SaveAsync(MacroAssignment assignment, MacroAssignmentStamp expectedStamp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assignment); ArgumentNullException.ThrowIfNull(expectedStamp);
        assignment = assignment.Validated(); expectedStamp.Validated();
        return OwnedAsync(token => Write(assignment.Target, assignment, expectedStamp, token), ct);
    }
    public Task<MacroAssignmentWriteResult> RemoveAsync(MacroAssignmentTarget target, MacroAssignmentStamp expectedStamp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(expectedStamp); target.Validated(); expectedStamp.Validated();
        return OwnedAsync(token => Write(target, null, expectedStamp, token), ct);
    }

    private Task<T> OwnedAsync<T>(Func<CancellationToken, T> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _workers.RemoveWhere(static task => task.IsCompleted);
            if (_workers.Count >= MaximumPendingOperations) throw new MacroAssignmentBusyException();
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = RunAsync(published.Task); _workers.Add(task); published.SetResult(); return task;
        }
        async Task<T> RunAsync(Task published)
        {
            await published.ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15)); bool entered = false;
            try
            {
                await _serial.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
                // The returned task remains the actual worker, including synchronous SQLite/native cleanup.
                return await Task.Run(() =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    try { return operation(deadline.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (MacroAssignmentConflictException) { throw; }
                    catch (MacroAssignmentBusyException) { deadline.Token.ThrowIfCancellationRequested(); throw; }
                    catch (MacroAssignmentCapacityException) { throw; }
                    catch (MacroAssignmentStorageException) { throw; }
                    catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
                    { deadline.Token.ThrowIfCancellationRequested(); throw new MacroAssignmentBusyException(); }
                    catch (SqliteException error) when (error.SqliteErrorCode == 13) { throw new MacroAssignmentCapacityException(); }
                    catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException
                        or ArgumentException or InvalidOperationException or OverflowException or FormatException or InvalidCastException or NotSupportedException)
                    { throw Storage(MacroAssignmentStorageProblem.Damaged); }
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { throw new MacroAssignmentBusyException(); }
            finally { if (entered) _serial.Release(); }
        }
    }

    private MacroAssignmentPage Read(MacroAssignmentQuery query, CancellationToken ct)
    {
        CheckFiles(requireConsistent: false);
        // An absent directory cannot hold a primary/journal at this observation point. Never create it on read.
        if (!CheckPath(_root, directory: true)) return Empty(query);
        using var lease = AcquireLease(ct);
        if (!CheckFiles()) return Empty(query);
        using var pin = PinExisting(); ValidateHeader(pin);
        using var db = Open(Exists(Path + "-journal") ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly, ct);
        using var tx = db.BeginTransaction(deferred: true);
        var stamp = ValidateDatabase(db, tx, ct);
        if (query.Expected is { } expected && expected != stamp) throw new MacroAssignmentConflictException();
        int total = Count(db, tx);
        if (query.Offset > total) throw new MacroAssignmentConflictException();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = SelectRows + " ORDER BY kind,target_id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", query.Limit); command.Parameters.AddWithValue("$offset", query.Offset);
        var items = new List<MacroAssignment>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) { ct.ThrowIfCancellationRequested(); items.Add(ReadRow(reader)); }
        ct.ThrowIfCancellationRequested();
        return new MacroAssignmentPage(stamp, items.AsReadOnly(), total, query.Offset + items.Count < total ? query.Offset + items.Count : null).Validated(query);
    }
    private static MacroAssignmentPage Empty(MacroAssignmentQuery query)
    {
        if (query.Expected is { } expected && expected != MacroAssignmentStamp.Empty || query.Offset != 0) throw new MacroAssignmentConflictException();
        return new(MacroAssignmentStamp.Empty, Array.Empty<MacroAssignment>(), 0, null);
    }

    private MacroAssignmentWriteResult Write(MacroAssignmentTarget target, MacroAssignment? assignment, MacroAssignmentStamp expected, CancellationToken ct)
    {
        CheckFiles(requireConsistent: false);
        if (!CheckPath(_root, directory: true) && assignment is null)
        {
            if (expected != MacroAssignmentStamp.Empty) throw new MacroAssignmentConflictException();
            return new(MacroAssignmentWriteState.Unchanged, MacroAssignmentStamp.Empty);
        }
        ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(_root); CheckFiles(requireConsistent: false);
        using var lease = AcquireLease(ct); bool existing = CheckFiles();
        if (!existing && expected != MacroAssignmentStamp.Empty) throw new MacroAssignmentConflictException();
        if (!existing && assignment is null) return new(MacroAssignmentWriteState.Unchanged, MacroAssignmentStamp.Empty);
        ct.ThrowIfCancellationRequested();
        CreatedFileIdentity? created = null; bool preserveCreation = false;
        try
        {
        // CreateNew claims only an absent exact path. An existing blank/schema0 file is never initialized.
        using var pin = existing ? PinExisting() : new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (!existing)
        {
            created = CreatedFileIdentity.From(pin.SafeFileHandle);
            _newPrimaryCheckpoint?.Invoke(NewPrimaryCheckpoint.CreatedBeforeOpen);
        }
        if (existing) ValidateHeader(pin);
        CheckFiles(); using var db = Open(SqliteOpenMode.ReadWrite, ct);
        using (var setup = db.CreateCommand())
        {
            setup.CommandText = "PRAGMA synchronous=FULL; PRAGMA trusted_schema=OFF;"; setup.ExecuteNonQuery();
            setup.CommandText = existing ? "PRAGMA journal_mode" : "PRAGMA journal_mode=DELETE";
            if (!string.Equals(setup.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase)) throw Storage(MacroAssignmentStorageProblem.Damaged);
        }
        using var tx = db.BeginTransaction(deferred: false);
        MacroAssignmentStamp current;
        if (existing) current = ValidateDatabase(db, tx, ct);
        else
        {
            using var fresh = db.CreateCommand(); fresh.Transaction = tx;
            fresh.CommandText = "PRAGMA user_version"; if (Convert.ToInt64(fresh.ExecuteScalar()) != 0) throw Storage(MacroAssignmentStorageProblem.Damaged);
            fresh.CommandText = "SELECT count(*) FROM sqlite_schema"; if (Convert.ToInt64(fresh.ExecuteScalar()) != 0) throw Storage(MacroAssignmentStorageProblem.Damaged);
            current = MacroAssignmentStamp.Empty;
        }
        if (current != expected) throw new MacroAssignmentConflictException();
        int total = existing ? Count(db, tx) : 0;
        MacroAssignment? previous = existing ? Find(db, tx, target) : null;
        if (previous == assignment) return new(MacroAssignmentWriteState.Unchanged, current);
        if (assignment is not null && previous is null && total >= MaximumAssignments) throw new MacroAssignmentCapacityException();
        if (current.Revision == long.MaxValue) throw Storage(MacroAssignmentStorageProblem.Damaged);
        var next = new MacroAssignmentStamp(existing ? current.Generation : Guid.NewGuid(), current.Revision + 1);
        if (!existing) CreateSchema(db, tx);
        using var command = db.CreateCommand(); command.Transaction = tx;
        if (assignment is null) command.CommandText = "DELETE FROM assignments WHERE kind=$kind AND target_id=$target";
        else
        {
            command.CommandText = "INSERT INTO assignments(" + Columns + ") VALUES($kind,$target,$targetName,$macro,$macroName) "
                + "ON CONFLICT(kind,target_id) DO UPDATE SET target_name=excluded.target_name,macro_id=excluded.macro_id,macro_name=excluded.macro_name";
            command.Parameters.AddWithValue("$targetName", assignment.TargetName); command.Parameters.AddWithValue("$macro", assignment.MacroId.ToString("N"));
            command.Parameters.AddWithValue("$macroName", assignment.MacroName);
        }
        command.Parameters.AddWithValue("$kind", (int)target.Kind); command.Parameters.AddWithValue("$target", target.Id.ToString("N"));
        ct.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); command.Parameters.Clear();
        command.CommandText = "INSERT INTO assignment_meta(id,generation,revision) VALUES(1,$generation,$revision) "
            + "ON CONFLICT(id) DO UPDATE SET generation=excluded.generation,revision=excluded.revision";
        command.Parameters.AddWithValue("$generation", next.Generation.ToString("N")); command.Parameters.AddWithValue("$revision", next.Revision);
        command.ExecuteNonQuery(); ct.ThrowIfCancellationRequested(); CheckFiles();
        try { preserveCreation = true; tx.Commit(); }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        { preserveCreation = false; throw new MacroAssignmentBusyException(); }
        catch (Exception error) when (error is SqliteException or IOException or InvalidOperationException)
        { throw Storage(MacroAssignmentStorageProblem.OutcomeUnknown); }
        // Do not throw cancellation after the actual successful commit, or imply its metadata was rolled back.
        return new(MacroAssignmentWriteState.Applied, next);
        }
        finally
        {
            // Nested using scopes have disposed the actual SQLite transaction/connection and file pin.
            // Keep the cooperative lease until this positively owned, definitely uncommitted cleanup ends.
            if (created is { } identity && !preserveCreation)
            {
                _newPrimaryCheckpoint?.Invoke(NewPrimaryCheckpoint.BeforeUncommittedCleanup);
                RemoveOwnedEmptyPrimary(identity);
            }
        }
    }

    private readonly record struct CreatedFileIdentity(uint Volume, uint IndexHigh, uint IndexLow, uint CreatedHigh, uint CreatedLow)
    {
        internal static CreatedFileIdentity From(SafeFileHandle handle)
        {
            if (!Native.GetFileInformationByHandle(handle, out var info)) throw Storage(MacroAssignmentStorageProblem.Damaged);
            return From(info);
        }
        internal static CreatedFileIdentity From(Native.FileInformation info)
            => new(info.Volume, info.IndexHigh, info.IndexLow, info.Created.High, info.Created.Low);
    }
    private void RemoveOwnedEmptyPrimary(CreatedFileIdentity expected)
    {
        CheckFiles();
        // OPEN_REPARSE_POINT prevents following a replacement link; zero sharing pins the selected object
        // against another writer/replacement while identity/length are checked and deletion is marked.
        using var file = Native.CreateFile(Path, 0x10000 | 0x80, 0, 0, 3, 0x00200000, 0); // DELETE | READ_ATTRIBUTES, OPEN_EXISTING.
        if (file.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() is 2 or 3) return;
            throw Storage(MacroAssignmentStorageProblem.Damaged);
        }
        if (!Native.GetFileInformationByHandle(file, out var info)) throw Storage(MacroAssignmentStorageProblem.Damaged);
        if (CreatedFileIdentity.From(info) != expected || info.Links != 1 || info.SizeHigh != 0 || info.SizeLow != 0
            || (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return;
        // A remaining journal can contain recovery data. Never delete it or its primary by inference.
        if (Exists(Path + "-journal") || Exists(Path + "-wal") || Exists(Path + "-shm")) return;
        var disposition = new Native.FileDisposition { Delete = 1 };
        if (!Native.SetFileInformationByHandle(file, 4, ref disposition, 1)) throw Storage(MacroAssignmentStorageProblem.Damaged);
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct FileTime { internal uint Low, High; }
        [StructLayout(LayoutKind.Sequential)] internal struct FileInformation
        {
            internal uint Attributes; internal FileTime Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct FileDisposition { internal byte Delete; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition information, uint size);
    }

    private const string SelectRows = "SELECT typeof(kind),kind,typeof(target_id),length(CAST(target_id AS BLOB)),target_id,"
        + "typeof(target_name),length(CAST(target_name AS BLOB)),target_name,typeof(macro_id),length(CAST(macro_id AS BLOB)),macro_id,"
        + "typeof(macro_name),length(CAST(macro_name AS BLOB)),macro_name FROM assignments";
    private static MacroAssignment? Find(SqliteConnection db, SqliteTransaction tx, MacroAssignmentTarget target)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = SelectRows + " WHERE kind=$kind AND target_id=$target";
        command.Parameters.AddWithValue("$kind", (int)target.Kind); command.Parameters.AddWithValue("$target", target.Id.ToString("N"));
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadRow(reader) : null;
    }
    private static MacroAssignment ReadRow(SqliteDataReader reader)
    {
        if (reader.GetString(0) != "integer") throw Storage(MacroAssignmentStorageProblem.Damaged);
        long kind = reader.GetInt64(1);
        if (kind is < 1 or > 3) throw Storage(MacroAssignmentStorageProblem.Damaged);
        var id = ReadGuid(reader, 2); string target = ReadText(reader, 5); var macro = ReadGuid(reader, 8); string name = ReadText(reader, 11);
        return new MacroAssignment(new((MacroAssignmentTargetKind)kind, id), target, macro, name).ValidatedSaved();
    }
    private static Guid ReadGuid(SqliteDataReader reader, int start)
    {
        if (reader.GetString(start) != "text" || reader.GetInt64(start + 1) != 32) throw Storage(MacroAssignmentStorageProblem.Damaged);
        string text = reader.GetString(start + 2);
        if (!Guid.TryParseExact(text, "N", out var id) || id == Guid.Empty || text != id.ToString("N")) throw Storage(MacroAssignmentStorageProblem.Damaged);
        return id;
    }
    private static string ReadText(SqliteDataReader reader, int start)
    {
        long bytes = reader.GetInt64(start + 1);
        if (reader.GetString(start) != "text" || bytes is < 1 or > MacroAssignment.MaximumNameLength * 4) throw Storage(MacroAssignmentStorageProblem.Damaged);
        string value = reader.GetString(start + 2);
        if (new UTF8Encoding(false, true).GetByteCount(value) != bytes) throw Storage(MacroAssignmentStorageProblem.Damaged);
        return value;
    }
    private static MacroAssignmentStamp ValidateDatabase(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw Storage(MacroAssignmentStorageProblem.UnsupportedVersion);
        command.CommandText = "PRAGMA application_id";
        if (Convert.ToInt64(command.ExecuteScalar()) != ApplicationId) throw Storage(MacroAssignmentStorageProblem.Damaged);
        command.CommandText = "PRAGMA quick_check(1)";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw Storage(MacroAssignmentStorageProblem.Damaged);
        command.CommandText = "SELECT type,name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' LIMIT 4";
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var schema = command.ExecuteReader())
            while (schema.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (schema.GetString(0) != "table" || !names.Add(schema.GetString(1))) throw Storage(MacroAssignmentStorageProblem.Damaged);
                string expected = schema.GetString(1) switch { "assignment_meta" => MetaSchema, "assignments" => AssignmentSchema, _ => "" };
                if (schema.GetString(2) != expected) throw Storage(MacroAssignmentStorageProblem.Damaged);
            }
        if (!names.SetEquals(new[] { "assignment_meta", "assignments" })) throw Storage(MacroAssignmentStorageProblem.Damaged);
        command.CommandText = "SELECT typeof(generation),length(CAST(generation AS BLOB)),generation,typeof(revision),revision FROM assignment_meta WHERE id=1";
        MacroAssignmentStamp stamp;
        using (var meta = command.ExecuteReader())
        {
            if (!meta.Read()) throw Storage(MacroAssignmentStorageProblem.Damaged);
            var generation = ReadGuid(meta, 0);
            if (meta.GetString(3) != "integer") throw Storage(MacroAssignmentStorageProblem.Damaged);
            stamp = new(generation, meta.GetInt64(4)); stamp.Validated();
            if (meta.Read()) throw Storage(MacroAssignmentStorageProblem.Damaged);
        }
        command.CommandText = "SELECT count(*) FROM assignment_meta";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1 || Count(db, tx) > MaximumAssignments) throw Storage(MacroAssignmentStorageProblem.Damaged);
        // Validate all bounded rows so an off-page malformed record cannot be hidden behind a healthy page or write.
        command.CommandText = SelectRows + " ORDER BY kind,target_id LIMIT 10001";
        using var rows = command.ExecuteReader(); int count = 0;
        while (rows.Read()) { ct.ThrowIfCancellationRequested(); if (++count > MaximumAssignments) throw Storage(MacroAssignmentStorageProblem.Damaged); _ = ReadRow(rows); }
        return stamp;
    }
    private const string MetaSchema = "CREATE TABLE assignment_meta(id INTEGER PRIMARY KEY CHECK(id=1),generation TEXT NOT NULL,revision INTEGER NOT NULL CHECK(revision>=1))";
    private const string AssignmentSchema = "CREATE TABLE assignments(kind INTEGER NOT NULL CHECK(kind BETWEEN 1 AND 3),target_id TEXT NOT NULL,target_name TEXT NOT NULL,macro_id TEXT NOT NULL,macro_name TEXT NOT NULL,PRIMARY KEY(kind,target_id)) WITHOUT ROWID";
    private static void CreateSchema(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = MetaSchema + ";" + AssignmentSchema + "; PRAGMA user_version=1; PRAGMA application_id=" + ApplicationId + ";";
        command.ExecuteNonQuery();
    }
    private static int Count(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = "SELECT count(*) FROM assignments";
        long count = Convert.ToInt64(command.ExecuteScalar()); if (count is < 0 or > MaximumAssignments) throw Storage(MacroAssignmentStorageProblem.Damaged);
        return (int)count;
    }

    private SqliteConnection Open(SqliteOpenMode mode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); CheckFiles();
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = mode, Pooling = false, DefaultTimeout = 2 }.ToString());
        try
        {
            db.Open(); using var limits = db.CreateCommand(); limits.CommandText = "PRAGMA trusted_schema=OFF;"; limits.ExecuteNonQuery();
            if (mode != SqliteOpenMode.ReadOnly)
            {
                limits.CommandText = "PRAGMA page_size"; int size = Convert.ToInt32(limits.ExecuteScalar());
                if (size is < 512 or > 65536) throw Storage(MacroAssignmentStorageProblem.Damaged);
                limits.CommandText = "PRAGMA max_page_count=" + (MaximumDatabaseBytes / size); limits.ExecuteScalar();
                limits.CommandText = "PRAGMA journal_size_limit=" + MaximumDatabaseBytes; limits.ExecuteScalar();
            }
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private FileStream PinExisting() => new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    private static void ValidateHeader(FileStream pin)
    {
        if (pin.Length is < 100 or > MaximumDatabaseBytes) throw Storage(MacroAssignmentStorageProblem.Damaged);
        Span<byte> header = stackalloc byte[100]; pin.Position = 0; pin.ReadExactly(header);
        if (!header[..16].SequenceEqual("SQLite format 3\0"u8)) throw Storage(MacroAssignmentStorageProblem.Damaged);
        int version = BinaryPrimitives.ReadInt32BigEndian(header[60..64]);
        if (version != 1) throw Storage(version == 0 ? MacroAssignmentStorageProblem.Damaged : MacroAssignmentStorageProblem.UnsupportedVersion);
        if (BinaryPrimitives.ReadInt32BigEndian(header[68..72]) != ApplicationId) throw Storage(MacroAssignmentStorageProblem.Damaged);
        if (header[18] != 1 || header[19] != 1) throw Storage(MacroAssignmentStorageProblem.Damaged); // This schema uses rollback journals, never WAL.
    }
    private bool CheckFiles(bool requireConsistent = true)
    {
        for (string? current = _root; current is not null; current = System.IO.Path.GetDirectoryName(current)) CheckPath(current, directory: true);
        bool existing = CheckPath(Path, directory: false);
        if (!existing && !requireConsistent) _newPrimaryCheckpoint?.Invoke(NewPrimaryCheckpoint.MissingPrimaryBeforeUnownedSidecars);
        foreach (string suffix in new[] { "-journal", "-wal", "-shm", ".lock" })
        {
            bool present = CheckPath(Path + suffix, directory: false, unowned: !requireConsistent);
            // Before the lease these files can be created/deleted between individual observations.
            // Only an owned coherent scan may classify them as orphaned or incompatible storage.
            if (requireConsistent && present && (suffix is "-wal" or "-shm")) throw Storage(MacroAssignmentStorageProblem.Damaged);
            if (requireConsistent && !existing && present && suffix != ".lock") throw Storage(MacroAssignmentStorageProblem.Damaged);
            if (present && suffix == ".lock" && LengthIfPresent(Path + suffix) is > 0) throw Storage(MacroAssignmentStorageProblem.Damaged);
        }
        return existing;
    }
    private bool CheckPath(string path, bool directory, bool unowned = false)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
                throw Storage(MacroAssignmentStorageProblem.UnsafePath);
            if (!directory)
            {
                if (unowned && path == Path + "-journal") _newPrimaryCheckpoint?.Invoke(NewPrimaryCheckpoint.UnownedJournalAttributesObserved);
                if (new FileInfo(path).Length > MaximumDatabaseBytes) throw new MacroAssignmentCapacityException();
            }
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static long? LengthIfPresent(string path)
    { try { return new FileInfo(path).Length; } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; } }
    private FileStream AcquireLease(CancellationToken ct)
    {
        CheckFiles(requireConsistent: false);
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested(); CheckPath(Path + ".lock", directory: false);
            try
            {
                var file = new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
                if (file.Length != 0) { file.Dispose(); throw Storage(MacroAssignmentStorageProblem.Damaged); }
                return file;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            { if (attempt >= 99) throw new MacroAssignmentBusyException(); if (ct.WaitHandle.WaitOne(20)) ct.ThrowIfCancellationRequested(); }
        }
    }
    private static bool Exists(string path)
    { try { File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    private static MacroAssignmentStorageException Storage(MacroAssignmentStorageProblem problem) => new(problem);

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true; return new(_dispose = DisposeOwnedAsync(_workers.ToArray()));
        }
    }
    private async Task DisposeOwnedAsync(Task[] workers)
    {
        await Task.Yield(); await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (Exception) { /* Request tasks retain their own failures. All actual workers and their native cleanup have now joined. */ }
        _serial.Dispose(); _lifetime.Dispose();
    }
}
