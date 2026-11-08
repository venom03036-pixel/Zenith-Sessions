using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class RecentClientHistoryStoreTests : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZenithHistoryTests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
    public RecentClientHistoryStoreTests() => Directory.CreateDirectory(_root);
    private static RecentClientHistoryEntry Entry(int pid = 123) => new(Guid.NewGuid(), new(null, pid, Now.AddSeconds(-10).UtcTicks),
        Now, Now.AddSeconds(1), Now.AddSeconds(2), Now.AddMilliseconds(500), 900_000_000_000_000_002, Now,
        new(900_000_000_000_000_001, Guid.Parse("11111111-2222-4333-8444-000000000001"), 456, Now));
    private static RecentClientHistorySnapshot Snapshot(long revision, params RecentClientHistoryEntry[] entries)
        => new(1, revision, Array.AsReadOnly(entries));

    [Fact]
    public async Task MissingHistoryIsEmptyWithoutCreatingOtherDataAndEndedMetadataRoundTrips()
    {
        using var store = new RecentClientHistoryStore(_root);
        Assert.Equal(0, (await store.LoadAsync(default)).Revision); Assert.False(File.Exists(store.Path));
        var entry = Entry(); await store.SaveAsync(Snapshot(1, entry), default);
        var read = await store.LoadAsync(default); Assert.Equal(entry, Assert.Single(read.Entries));
        Assert.Equal(1, read.Version); Assert.Equal(1, read.Revision); Assert.Equal("client-history.db", System.IO.Path.GetFileName(store.Path));
        Assert.False(File.Exists(System.IO.Path.Combine(_root, "accounts.db")));
        Assert.False(File.Exists(store.Path + "-wal"));
        Assert.InRange(new FileInfo(store.Path).Length, 1, RecentClientHistoryStore.MaximumDatabaseBytes);
        var reducer = new RecentClientHistoryReducer(); reducer.Restore(read);
        Assert.Empty(reducer.Snapshot().Active); Assert.False(Assert.Single(reducer.Snapshot().Ended).CanAuthorizeAccountAction);
    }

    [Fact]
    public async Task ClearRevisionRejectsOldOrDifferentWritesAndAllowsAnExactRetry()
    {
        using var first = new RecentClientHistoryStore(_root); using var second = new RecentClientHistoryStore(_root);
        var old = Snapshot(1, Entry()); await first.SaveAsync(old, default);
        var cleared = Snapshot(2); await second.SaveAsync(cleared, default); await second.SaveAsync(cleared, default);
        await Assert.ThrowsAsync<RecentClientHistoryStorageException>(() => first.SaveAsync(old, default));
        await Assert.ThrowsAsync<RecentClientHistoryStorageException>(() => first.SaveAsync(Snapshot(2, Entry(456)), default));
        var latest = await first.LoadAsync(default); Assert.Equal(2, latest.Revision); Assert.Empty(latest.Entries);
    }

    [Fact]
    public async Task CorruptFileIsPreservedAndNeitherLoadNorSaveClaimsEmptySuccess()
    {
        using var store = new RecentClientHistoryStore(_root); byte[] corrupt = [0x52, 0x41, 0x57, 0, 0xff];
        await File.WriteAllBytesAsync(store.Path, corrupt);
        await Assert.ThrowsAsync<RecentClientHistoryStorageException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<RecentClientHistoryStorageException>(() => store.SaveAsync(Snapshot(1), default));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(store.Path));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("json")]
    [InlineData("oversized")]
    [InlineData("extra-table")]
    [InlineData("evidence-json")]
    [InlineData("evidence-twice")]
    public async Task UnsupportedOrMalformedExistingDatabaseCannotBeOverwritten(string kind)
    {
        using var store = new RecentClientHistoryStore(_root); await store.SaveAsync(Snapshot(1, Entry()), default);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString()))
        {
            await db.OpenAsync(); using var command = db.CreateCommand();
            command.CommandText = kind switch
            {
                "version" => "PRAGMA user_version=99", "json" => "UPDATE history_entries SET payload='{}'",
                "oversized" => "UPDATE history_entries SET payload=$payload", "evidence-json" => "UPDATE history_entries SET evidence='{\"Unexpected\":1}'",
                "evidence-twice" => "UPDATE history_entries SET payload=json_set(payload,'$.Hung',$ticks),evidence=json_object('Hung',$ticks)",
                _ => "CREATE TABLE unexpected(value TEXT)"
            };
            if (kind == "oversized") command.Parameters.AddWithValue("$payload", new string('x', RecentClientHistoryStore.MaximumEntryBytes + 1));
            if (kind == "evidence-twice") command.Parameters.AddWithValue("$ticks", Now.UtcTicks);
            await command.ExecuteNonQueryAsync();
        }
        byte[] before = await File.ReadAllBytesAsync(store.Path);
        await Assert.ThrowsAsync<RecentClientHistoryStorageException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<RecentClientHistoryStorageException>(() => store.SaveAsync(Snapshot(2), default));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.Path));
    }

    [Fact]
    public async Task CancellationAndInvalidSnapshotsLeaveTheLastCommittedCopyIntact()
    {
        using var store = new RecentClientHistoryStore(_root); var original = Snapshot(1, Entry()); await store.SaveAsync(original, default);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Snapshot(2), stop.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Snapshot(2, Entry() with { NoLongerObservedUtc = null }), default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Snapshot(2, Entry() with { LastKnownUserIdentityUtc = Now.AddDays(1) }), default));
        var read = await store.LoadAsync(default); Assert.Equal(original.Entries, read.Entries); Assert.Equal(original.Revision, read.Revision);
    }

    [Fact]
    public async Task HundredEntryBoundRejectsAdditionalRowsAndSerialWritesKeepTheLatestRevision()
    {
        using var store = new RecentClientHistoryStore(_root);
        var entries = Enumerable.Range(1, 100).Select(Entry).ToArray(); await store.SaveAsync(Snapshot(1, entries), default);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Snapshot(2, entries.Append(Entry(101)).ToArray()), default));
        var write = store.SaveAsync(Snapshot(2), default); var next = store.SaveAsync(Snapshot(3, Entry(500)), default);
        await Task.WhenAll(write, next); var read = await store.LoadAsync(default);
        Assert.Equal(3, read.Revision); Assert.Equal(500, Assert.Single(read.Entries).Process.ProcessId);
    }

    [Fact]
    public async Task EndEvidenceRoundTripsInItsOwnColumnAndEveryPayloadKeepsTheOriginalRowShape()
    {
        using var store = new RecentClientHistoryStore(_root);
        var evidence = Entry() with { NotRespondingUtc = Now.AddSeconds(1), DisconnectedUtc = Now, DisconnectReason = 277,
            FaultCheck = new(Now.AddSeconds(8), true, Now.AddSeconds(1), 0xC0000005, 2) };
        var plain = Entry(456);
        await store.SaveAsync(Snapshot(1, evidence, plain), default);
        var read = await store.LoadAsync(default);
        Assert.Equal(evidence, read.Entries[0]); Assert.Equal(plain, read.Entries[1]);
        Assert.Equal(ClientEndCause.Crashed, read.Entries[0].EndCause);
        // Every payload keeps the earlier fourteen-field shape; end evidence is in its own column, NULL when there is none.
        var rows = await RowsAsync(store.Path);
        Assert.All(rows, row => Assert.Equal(14, FieldCount(row.Payload)));
        Assert.Equal(8, FieldCount(rows[0].Evidence!)); Assert.Null(rows[1].Evidence);
    }

    [Fact]
    public async Task HistoryWithEndEvidenceStaysReadableAndWritableByBuildsThatPredateEndEvidence()
    {
        using var store = new RecentClientHistoryStore(_root);
        var evidence = Entry() with { NotRespondingUtc = Now.AddSeconds(1), DisconnectedUtc = Now, DisconnectReason = 277,
            FaultCheck = new(Now.AddSeconds(8), true, Now.AddSeconds(1), 0xC0000005, 2) };
        var plain = Entry(456);
        await store.SaveAsync(Snapshot(1, evidence, plain), default);
        var payloads = await EarlierBuildReadAsync(store.Path);
        Assert.Equal(2, payloads.Count);
        // An earlier build saves with exactly these statements. It keeps both rows, but it does not know the evidence, so
        // the rows it rewrites lose it and this build then reads their cause as Unknown.
        await ExecuteAsync(store.Path, "DELETE FROM history_entries; INSERT INTO history_entries(ordinal,payload) VALUES(0,$p0); INSERT INTO history_entries(ordinal,payload) VALUES(1,$p1); UPDATE history_meta SET revision=2 WHERE id=1", payloads[0], payloads[1]);
        var rewritten = await store.LoadAsync(default);
        Assert.Equal(2, rewritten.Revision);
        Assert.Equal(new[] { evidence with { NotRespondingUtc = null, DisconnectedUtc = null, DisconnectReason = null, FaultCheck = null }, plain }, rewritten.Entries);
        Assert.Equal(ClientEndCause.Unknown, rewritten.Entries[0].EndCause);
        // An earlier build's Clear.
        await ExecuteAsync(store.Path, "DELETE FROM history_entries; UPDATE history_meta SET revision=3 WHERE id=1");
        var cleared = await store.LoadAsync(default); Assert.Equal(3, cleared.Revision); Assert.Empty(cleared.Entries);
    }

    [Fact]
    public async Task InlineEndEvidenceFromTheFirstEvidenceBuildStillLoadsAndIsRewrittenInTheOriginalRowShape()
    {
        using var store = new RecentClientHistoryStore(_root);
        var evidence = Entry() with { NotRespondingUtc = Now.AddSeconds(1), DisconnectedUtc = Now, DisconnectReason = 277,
            FaultCheck = new(Now.AddSeconds(8), true, Now.AddSeconds(1), 0xC0000005, 2) };
        var plain = Entry(456);
        await store.SaveAsync(Snapshot(1, evidence, plain), default);
        // Rebuild the shape the first end-evidence build wrote: no evidence column and the evidence fields inside the payload.
        var rows = await RowsAsync(store.Path);
        var inline = System.Text.Json.Nodes.JsonNode.Parse(rows[0].Payload)!.AsObject();
        foreach (var field in System.Text.Json.Nodes.JsonNode.Parse(rows[0].Evidence!)!.AsObject()) inline[field.Key] = field.Value?.DeepClone();
        await ExecuteAsync(store.Path, "ALTER TABLE history_entries DROP COLUMN evidence; UPDATE history_entries SET payload=$p0 WHERE ordinal=0", inline.ToJsonString());
        Assert.Equal(22, FieldCount((await RowsAsync(store.Path, evidenceColumn: false))[0].Payload));

        var read = await store.LoadAsync(default);
        Assert.Equal(new[] { evidence, plain }, read.Entries); Assert.Equal(ClientEndCause.Crashed, read.Entries[0].EndCause);
        await store.SaveAsync(Snapshot(2, evidence, plain), default);
        Assert.Equal(2, (await EarlierBuildReadAsync(store.Path)).Count);
        Assert.Equal(new[] { evidence, plain }, (await store.LoadAsync(default)).Entries);
    }

    [Fact]
    public async Task AForcedEndRoundTripsInTheEvidenceColumnAndKeepsTheOriginalRowShapeForEarlierBuilds()
    {
        // R-instances-12: the forced end is one more piece of end evidence. It rides in the optional evidence column, so the
        // payload keeps its fourteen original fields and builds that predate end evidence still read and rewrite the history.
        using var store = new RecentClientHistoryStore(_root);
        var forced = Entry() with { ForcedEndUtc = Now.AddSeconds(1), NotRespondingUtc = Now.AddSeconds(1) };
        var plain = Entry(456);
        await store.SaveAsync(Snapshot(1, forced, plain), default);
        var rows = await RowsAsync(store.Path);
        Assert.Equal(14, FieldCount(rows[0].Payload));
        Assert.Contains("\"Forced\"", rows[0].Evidence);
        Assert.Null(rows[1].Evidence);
        var read = await store.LoadAsync(default);
        Assert.Equal(new[] { forced, plain }, read.Entries);
        Assert.Equal(ClientEndCause.ForcedEnd, read.Entries[0].EndCause); Assert.Null(read.Entries[1].ForcedEndUtc);
        var payloads = await EarlierBuildReadAsync(store.Path);
        await ExecuteAsync(store.Path, "DELETE FROM history_entries; INSERT INTO history_entries(ordinal,payload) VALUES(0,$p0); INSERT INTO history_entries(ordinal,payload) VALUES(1,$p1); UPDATE history_meta SET revision=2 WHERE id=1", payloads[0], payloads[1]);
        var rewritten = await store.LoadAsync(default);
        Assert.Equal(new[] { forced with { ForcedEndUtc = null, NotRespondingUtc = null }, plain }, rewritten.Entries);
    }

    private static int FieldCount(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Count();
    }
    private static async Task<List<(string Payload, string? Evidence)>> RowsAsync(string path, bool evidenceColumn = true)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await db.OpenAsync(); using var command = db.CreateCommand();
        command.CommandText = "SELECT payload," + (evidenceColumn ? "evidence" : "NULL") + " FROM history_entries ORDER BY ordinal";
        var rows = new List<(string, string?)>();
        using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    // The row checks of builds before end evidence: format version 1, exactly the two tables, and payloads with exactly the
    // fourteen original fields and nothing else. Any other shape made them show the whole history as unavailable.
    private sealed record EarlierBuildRow(Guid EntryId, string? HostId, int Pid, long Start, long First, long Last, long Absent,
        long? Close, long? User, long? UserTime, long? Place, Guid? Job, long? Universe, long? DestinationTime);
    private static async Task<List<string>> EarlierBuildReadAsync(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await db.OpenAsync(); using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version"; Assert.Equal(1, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' AND type='table'";
        var tables = new List<string>();
        using (var rows = await command.ExecuteReaderAsync()) while (await rows.ReadAsync()) tables.Add(rows.GetString(0));
        Assert.Equal(new[] { "history_entries", "history_meta" }, tables.Order(StringComparer.Ordinal).ToArray());
        command.CommandText = "SELECT payload FROM history_entries ORDER BY ordinal";
        var payloads = new List<string>();
        using (var rows = await command.ExecuteReaderAsync()) while (await rows.ReadAsync()) payloads.Add(rows.GetString(0));
        var strict = new System.Text.Json.JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        foreach (string payload in payloads)
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            Assert.Equal(14, document.RootElement.EnumerateObject().Count());
            Assert.NotNull(System.Text.Json.JsonSerializer.Deserialize<EarlierBuildRow>(payload, strict));
        }
        return payloads;
    }
    private static async Task ExecuteAsync(string path, string sql, params string[] payloads)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql;
        for (int index = 0; index < payloads.Length; index++) command.Parameters.AddWithValue("$p" + index, payloads[index]);
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        string full = System.IO.Path.GetFullPath(_root), temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if (System.IO.Path.GetDirectoryName(full) != temporary.TrimEnd(System.IO.Path.DirectorySeparatorChar)
            || !System.IO.Path.GetFileName(full).StartsWith("ZenithHistoryTests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected history fixture root.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
