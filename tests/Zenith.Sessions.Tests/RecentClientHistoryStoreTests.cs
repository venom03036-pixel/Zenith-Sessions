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
    public async Task UnsupportedOrMalformedExistingDatabaseCannotBeOverwritten(string kind)
    {
        using var store = new RecentClientHistoryStore(_root); await store.SaveAsync(Snapshot(1, Entry()), default);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString()))
        {
            await db.OpenAsync(); using var command = db.CreateCommand();
            command.CommandText = kind switch
            {
                "version" => "PRAGMA user_version=99", "json" => "UPDATE history_entries SET payload='{}'",
                "oversized" => "UPDATE history_entries SET payload=$payload", _ => "CREATE TABLE unexpected(value TEXT)"
            };
            if (kind == "oversized") command.Parameters.AddWithValue("$payload", new string('x', RecentClientHistoryStore.MaximumEntryBytes + 1));
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
    public async Task EndEvidenceRoundTripsAndEntriesWithoutItKeepTheOriginalRowShape()
    {
        using var store = new RecentClientHistoryStore(_root);
        var evidence = Entry() with { NotRespondingUtc = Now.AddSeconds(1), DisconnectedUtc = Now, DisconnectReason = 277,
            FaultCheck = new(Now.AddSeconds(8), true, Now.AddSeconds(1), 0xC0000005, 2) };
        var plain = Entry(456);
        await store.SaveAsync(Snapshot(1, evidence, plain), default);
        var read = await store.LoadAsync(default);
        Assert.Equal(evidence, read.Entries[0]); Assert.Equal(plain, read.Entries[1]);
        Assert.Equal(ClientEndCause.Crashed, read.Entries[0].EndCause);
        // Rows without end evidence keep the earlier fourteen-field shape, so they stay readable by earlier versions.
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await db.OpenAsync(); using var command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM history_entries ORDER BY ordinal";
        var payloads = new List<string>();
        using (var rows = await command.ExecuteReaderAsync()) while (await rows.ReadAsync()) payloads.Add(rows.GetString(0));
        using var withEvidence = System.Text.Json.JsonDocument.Parse(payloads[0]); using var without = System.Text.Json.JsonDocument.Parse(payloads[1]);
        Assert.Equal(22, withEvidence.RootElement.EnumerateObject().Count()); Assert.Equal(14, without.RootElement.EnumerateObject().Count());
    }

    public void Dispose()
    {
        string full = System.IO.Path.GetFullPath(_root), temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if (System.IO.Path.GetDirectoryName(full) != temporary.TrimEnd(System.IO.Path.DirectorySeparatorChar)
            || !System.IO.Path.GetFileName(full).StartsWith("ZenithHistoryTests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected history fixture root.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
