using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class AccountRepository
{
    private const int GroupChangePageSize = 128;
    // Bound managed decoding before GetString: malformed/oversized stored values become NULL
    // and fail closed rather than allocating an unbounded primary name or JSON string.
    private const string GroupChangeColumns = "CASE WHEN typeof(id)='text' AND length(id) BETWEEN 1 AND 64 AND length(CAST(id AS BLOB))<=256 THEN id END,"
        + "CASE WHEN typeof(revision)='integer' THEN revision END,"
        + "CASE WHEN typeof(group_name)='text' AND length(group_name) BETWEEN 1 AND 60 AND length(CAST(group_name AS BLOB))<=240 THEN group_name END,"
        + "CASE WHEN typeof(additional_groups)='text' AND length(additional_groups)<=16384 AND length(CAST(additional_groups AS BLOB))<=65536 THEN additional_groups END";

    public async Task<AccountGroupChangePreview> PreviewGroupChangeAsync(AccountGroupChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change); change = change.Validated(); ct.ThrowIfCancellationRequested();
        // Microsoft.Data.Sqlite operations may finish synchronously. The user-owned scan is
        // awaited off the calling UI context, without creating a loop, timer or inventory cache.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        ct.ThrowIfCancellationRequested();
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = db.BeginTransaction(deferred: true);
        var preview = await ReadGroupChangePreviewAsync(db, tx, change, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); tx.Commit(); return preview;
    }

    public async Task<int> ApplyGroupChangeAsync(AccountGroupChangePreview expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(expected.Change);
        var change = expected.Change.Validated();
        if (change != expected.Change || expected.AffectedCount < 0 || expected.PrimaryCount < 0 || expected.PrimaryCount > expected.AffectedCount
            || expected.SnapshotFingerprint is not { Length: 64 } || expected.SnapshotFingerprint.Any(value => !char.IsAsciiHexDigit(value)))
            throw new ArgumentException("A valid reviewed group-change preview is required.");
        ct.ThrowIfCancellationRequested();
        await _writer.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var db = await OpenAsync(ct).ConfigureAwait(false);
            // Reserve the SQLite writer before comparing, including against other repository
            // instances/processes. No read-to-write transaction upgrade can slip in another edit.
            using var tx = db.BeginTransaction(deferred: false);
            await RequireCurrentAccountSchemaAsync(db,tx,ct);
            var current = await ReadGroupChangePreviewAsync(db, tx, change, ct).ConfigureAwait(false);
            if (current != expected) throw new RevisionConflictException();
            if (!current.SourceExists) { ct.ThrowIfCancellationRequested(); tx.Commit(); return 0; }
            var source=(await FindGroupByNameAsync(db,tx,change.SourceName,ct))!;
            var destination=await FindGroupByNameAsync(db,tx,change.DestinationName,ct);
            if (change.Kind == AccountGroupChangeKind.Rename && current.DestinationExists && !change.AllowMerge
                && !StringComparer.OrdinalIgnoreCase.Equals(change.SourceName, change.DestinationName))
                throw new ArgumentException("The destination group already exists. Confirm merging these groups, then review the change again.");

            bool catalogChanged=false;
            bool sameDefinition=destination?.Id==source.Id;
            // Existing merge/fallback definitions retain their exact name and appearance. A
            // case-only rename deliberately chooses the user's new spelling on the same ID.
            var effective=change with{DestinationName=destination is not null&&!sameDefinition?destination.Name:change.DestinationName};
            if(change.Kind==AccountGroupChangeKind.Rename&&(destination is null||sameDefinition))
            {
                if(source.Name!=change.DestinationName)
                {
                    var renamed=source with{Name=change.DestinationName,Revision=NextCatalogRevision(source.Revision),UpdatedUtc=DateTimeOffset.UtcNow};
                    await UpdateGroupDefinitionAsync(db,tx,source,renamed,ct);catalogChanged=true;
                }
            }
            else if(change.Kind==AccountGroupChangeKind.Remove&&current.PrimaryCount>0&&destination is null)
                catalogChanged=await EnsureGroupDefinitionAsync(db,tx,change.DestinationName,ct);

            int changed = 0; string? after = null; DateTimeOffset updated = DateTimeOffset.UtcNow;
            using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = "UPDATE profiles SET group_name=$primary,additional_groups=$additional,revision=$next,updated_utc=$updated WHERE id=$id AND revision=$previous";
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // Keyset pages keep memory bounded while readers are closed before updates.
                // Membership updates cannot change this stable primary-key ordering.
                var page = await ReadGroupChangePageAsync(db, tx, after, ct).ConfigureAwait(false);
                if (page.Count == 0) break;
                foreach (var row in page)
                {
                    ct.ThrowIfCancellationRequested(); after = row.Id;
                    var next = RewriteGroupMembership(row, effective);
                    if (next.Primary == row.Primary && next.Additional == row.Additional) continue;
                    update.Parameters.Clear();
                    update.Parameters.AddWithValue("$id", row.Id); update.Parameters.AddWithValue("$previous", row.Revision);
                    update.Parameters.AddWithValue("$next", checked(row.Revision + 1));
                    update.Parameters.AddWithValue("$primary", next.Primary);
                    update.Parameters.AddWithValue("$additional", JsonSerializer.Serialize(next.Additional.Length == 0 ? Array.Empty<string>() : next.Additional.Split('\n')));
                    update.Parameters.AddWithValue("$updated", updated.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new RevisionConflictException();
                    changed = checked(changed + 1);
                }
            }
            if(change.Kind==AccountGroupChangeKind.Remove||destination is not null&&!sameDefinition)
            {
                using var remove=db.CreateCommand();remove.Transaction=tx;remove.CommandText="DELETE FROM account_groups WHERE id=$id AND revision=$revision";
                remove.Parameters.AddWithValue("$id",source.Id.ToString("D"));remove.Parameters.AddWithValue("$revision",source.Revision);
                if(await remove.ExecuteNonQueryAsync(ct)!=1)throw new AccountGroupCatalogConflictException();catalogChanged=true;
            }
            if(catalogChanged)await AdvanceCatalogAsync(db,tx,ct);
            ct.ThrowIfCancellationRequested(); tx.Commit(); return changed;
        }
        finally { _writer.Release(); }
    }

    private static async Task<AccountGroupChangePreview> ReadGroupChangePreviewAsync(SqliteConnection db, SqliteTransaction tx,
        AccountGroupChange change, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendGroupHash(hash, "Zenith account group preview v2"); AppendGroupHash(hash, (long)change.Kind);
        AppendGroupHash(hash, change.SourceName); AppendGroupHash(hash, change.DestinationName); AppendGroupHash(hash, change.AllowMerge ? 1L : 0L);
        long catalogRevision=await ReadCatalogRevisionAsync(db,tx,ct);
        var source=await FindGroupByNameAsync(db,tx,change.SourceName,ct);var destination=await FindGroupByNameAsync(db,tx,change.DestinationName,ct);
        AppendGroupHash(hash,catalogRevision);AppendGroupDefinitionHash(hash,source);AppendGroupDefinitionHash(hash,destination);
        int affected = 0, primary = 0; bool destinationExists = destination is not null;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT " + GroupChangeColumns + " FROM profiles ORDER BY id COLLATE BINARY";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested(); var row = ReadGroupChangeRow(reader);
            if(HasGroupMembership(row,change.DestinationName)&&destination is null)throw InvalidCatalog();
            if (!HasGroupMembership(row, change.SourceName)) continue;
            if(source is null)throw InvalidCatalog();
            affected = checked(affected + 1);
            if (StringComparer.OrdinalIgnoreCase.Equals(row.Primary, change.SourceName)) primary = checked(primary + 1);
            AppendGroupHash(hash, row.Id); AppendGroupHash(hash, row.Revision); AppendGroupHash(hash, row.Primary); AppendGroupHash(hash, row.StoredJson);
            var next = RewriteGroupMembership(row, change);
            if ((next.Primary != row.Primary || next.Additional != row.Additional) && row.Revision >= long.MaxValue - 1)
                throw new InvalidDataException("A profile revision cannot advance safely. The group change was cancelled; preserve the library for recovery.");
        }
        AppendGroupHash(hash, affected); AppendGroupHash(hash, primary); AppendGroupHash(hash, destinationExists ? 1L : 0L);
        return new(change, affected, primary, destinationExists, Convert.ToHexString(hash.GetHashAndReset()),source is not null,catalogRevision);
    }
    private static void AppendGroupDefinitionHash(IncrementalHash hash,AccountGroupDefinition? group)
    {
        AppendGroupHash(hash,group is null?0L:1L);if(group is null)return;
        AppendGroupHash(hash,group.Id.ToString("D"));AppendGroupHash(hash,group.Name);AppendGroupHash(hash,group.ColorHex??"");
        AppendGroupHash(hash,group.SortOrder);AppendGroupHash(hash,group.Revision);AppendGroupHash(hash,group.UpdatedUtc.UtcTicks);AppendGroupHash(hash,group.UpdatedUtc.Offset.Ticks);
    }

    private static async Task<List<GroupChangeRow>> ReadGroupChangePageAsync(SqliteConnection db, SqliteTransaction tx, string? after, CancellationToken ct)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT " + GroupChangeColumns + " FROM profiles WHERE ($after IS NULL OR id>$after COLLATE BINARY) ORDER BY id COLLATE BINARY LIMIT $limit";
        command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value); command.Parameters.AddWithValue("$limit", GroupChangePageSize);
        var rows = new List<GroupChangeRow>(GroupChangePageSize);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) { ct.ThrowIfCancellationRequested(); rows.Add(ReadGroupChangeRow(reader)); }
        return rows;
    }

    private static GroupChangeRow ReadGroupChangeRow(SqliteDataReader reader)
    {
        try
        {
            for (int index = 0; index < 4; index++) if (reader.IsDBNull(index)) throw InvalidGroups();
            string id = reader.GetString(0), primary = reader.GetString(2), json = reader.GetString(3); long revision = reader.GetInt64(1);
            if (!Guid.TryParse(id, out var identity) || identity == Guid.Empty || id.Length > 64 || revision <= 0 || revision == long.MaxValue
                || AccountGroups.ValidateName(primary) != primary) throw InvalidGroups();
            return new(id, revision, primary, ReadAdditionalGroups(primary, json), json);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or OverflowException)
        { throw InvalidGroups(); }
    }
    private static bool HasGroupMembership(GroupChangeRow row, string group) => StringComparer.OrdinalIgnoreCase.Equals(row.Primary, group)
        || (row.Additional.Length != 0 && row.Additional.Split('\n').Contains(group, StringComparer.OrdinalIgnoreCase));
    private static (string Primary, string Additional) RewriteGroupMembership(GroupChangeRow row, AccountGroupChange change)
    {
        if (!HasGroupMembership(row, change.SourceName)) return (row.Primary, row.Additional);
        bool primaryMatches = StringComparer.OrdinalIgnoreCase.Equals(row.Primary, change.SourceName);
        string primary = primaryMatches ? change.DestinationName : row.Primary;
        var names = new List<string>(AccountGroups.MaximumAdditionalGroups);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary };
        if (row.Additional.Length != 0)
            foreach (string name in row.Additional.Split('\n'))
            {
                bool source = StringComparer.OrdinalIgnoreCase.Equals(name, change.SourceName);
                if (source && change.Kind == AccountGroupChangeKind.Remove) continue;
                string next = source ? change.DestinationName : name;
                if (seen.Add(next)) names.Add(next);
            }
        return (primary, AccountGroups.Canonicalize(primary, string.Join('\n', names)));
    }
    private static void AppendGroupHash(IncrementalHash hash, long value)
    { Span<byte> bytes = stackalloc byte[sizeof(long)]; BinaryPrimitives.WriteInt64LittleEndian(bytes, value); hash.AppendData(bytes); }
    private static void AppendGroupHash(IncrementalHash hash, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); AppendGroupHash(hash, bytes.Length); hash.AppendData(bytes); }
    private sealed record GroupChangeRow(string Id, long Revision, string Primary, string Additional, string StoredJson);
}
