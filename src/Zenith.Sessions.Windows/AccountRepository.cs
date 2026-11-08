using Microsoft.Data.Sqlite;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class AccountRepository : IAccountRepository,IAccountGroupEditor,IAccountGroupLifecycle,IAccountGroupCatalog,IAccountMetadataImport,IDisposable
{
    private const int MaximumGroupsJsonLength = 16384;
    private const string ProfileColumns = "id,label,username,group_name,priority,place_id,user_id,updated_utc,revision,notes,is_favorite,additional_groups";
    private const string GroupsColumn = "additional_groups TEXT NOT NULL DEFAULT '[]' CHECK(length(additional_groups)<=16384 AND json_valid(additional_groups) AND json_type(additional_groups)='array' AND json_array_length(additional_groups)<=31)";
    private readonly string _connection;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _writer = new(1);
    public AccountRepository(string root)
    {
        Directory.CreateDirectory(root);
        _databasePath=Path.Combine(root,"accounts.db");
        _connection = new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 5 }.ToString();
    }
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct,bool allowMigration=false)
    {
        var db = new SqliteConnection(_connection);
        try
        {
            await db.OpenAsync(ct);
            // SQLite NOCASE covers ASCII only; the domain uses ordinal Unicode case comparison.
            db.CreateCollation("ZENITH_GROUP", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            db.CreateFunction<string, string, string>("zenith_groups_json", (primary, json) =>
            { _ = ReadAdditionalGroups(primary, json); return json; }, isDeterministic: true);
            db.CreateFunction<string,string,long>("zenith_group_matches",(name,search)=>
                AccountGroups.ValidateName(name).Contains(search,StringComparison.OrdinalIgnoreCase)?1L:0L,isDeterministic:true);
            if(!allowMigration)
            {
                using var version=db.CreateCommand();version.CommandText="PRAGMA user_version";
                if(Convert.ToInt64(await version.ExecuteScalarAsync(ct))!=5)throw UnsupportedAccountSchema();
            }
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var db = await OpenAsync(ct,allowMigration:true);
            using var query = db.CreateCommand(); query.CommandText = "SELECT sqlite_version()";
            string? nativeVersion=(string?)await query.ExecuteScalarAsync(ct);
            if(!Version.TryParse(nativeVersion,out var sqliteVersion) || sqliteVersion<new Version(3,50,2))
                throw new InvalidDataException("The bundled database runtime is too old. Preserve your data and reinstall a complete current Zenith Sessions package.");
            while(true)
            {
            using var tx = db.BeginTransaction();
            query.Transaction=tx;query.CommandText="PRAGMA user_version";
            int version=Convert.ToInt32(await query.ExecuteScalarAsync(ct));
            if(version is <0 or >5)throw UnsupportedAccountSchema();
            if(version==5){_ = await ReadCatalogRevisionAsync(db,tx,ct);tx.Commit();return;}
            if(version is 1 or 2 or 3 or 4)
            {
                // The writer connection has not modified anything. A separate read-only source
                // can copy this exact committed version while this lease blocks foreign writes.
                await using var source=new SqliteConnection(new SqliteConnectionStringBuilder(_connection){Mode=SqliteOpenMode.ReadOnly}.ToString());
                await source.OpenAsync(ct);await BackupSchemaAsync(source,version,ct);
            }
            using var command = db.CreateCommand(); command.Transaction = tx;
            if(version==4)
            {
                await CreateGroupCatalogAsync(db,tx,ct);ct.ThrowIfCancellationRequested();tx.Commit();return;
            }
            command.CommandText = version==0?$"""
                CREATE TABLE profiles(id TEXT PRIMARY KEY, label TEXT NOT NULL, username TEXT NOT NULL, group_name TEXT NOT NULL,
                priority INTEGER NOT NULL, place_id INTEGER NULL, user_id INTEGER NULL, updated_utc TEXT NOT NULL, revision INTEGER NOT NULL,
                notes TEXT NOT NULL DEFAULT '',is_favorite INTEGER NOT NULL DEFAULT 0 CHECK(is_favorite IN (0,1)),{GroupsColumn});
                CREATE INDEX profiles_group_label ON profiles(group_name, label COLLATE NOCASE);
                CREATE TABLE credential_cleanup(profile_id TEXT PRIMARY KEY);
                PRAGMA user_version=4;
                """:(version==1?"CREATE TABLE credential_cleanup(profile_id TEXT PRIMARY KEY);":"")+(version is 1 or 2?"""
                ALTER TABLE profiles ADD COLUMN notes TEXT NOT NULL DEFAULT '';
                ALTER TABLE profiles ADD COLUMN is_favorite INTEGER NOT NULL DEFAULT 0 CHECK(is_favorite IN (0,1));
                """:"")+"ALTER TABLE profiles ADD COLUMN "+GroupsColumn+"; PRAGMA user_version=4;";
            await command.ExecuteNonQueryAsync(ct); ct.ThrowIfCancellationRequested();
            if(version==0)
            {
                await CreateGroupCatalogAsync(db,tx,ct);ct.ThrowIfCancellationRequested();tx.Commit();return;
            }
            tx.Commit();
            }
        }
        finally { _writer.Release(); }
    }
    private async Task BackupSchemaAsync(SqliteConnection source,int version,CancellationToken ct)
    {
        string temporary=_databasePath+$".schema{version}."+Guid.NewGuid().ToString("N")+".tmp",backup=_databasePath+$".schema{version}.bak";bool ownsTemporary=false;
        try
        {
            // Never truncate an old interrupted .schemaX.tmp, and never replace an unknown
            // or corrupt rollback generation merely because a new migration was requested.
            if(File.Exists(backup))
            {
                await using var previous=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
                await previous.OpenAsync(ct);using var check=previous.CreateCommand();check.CommandText="PRAGMA integrity_check";
                if((string?)await check.ExecuteScalarAsync(ct)!="ok")throw new InvalidDataException("The existing schema backup could not be verified. Preserve it before migrating the library.");
                check.CommandText="PRAGMA user_version";
                if(Convert.ToInt32(await check.ExecuteScalarAsync(ct))!=version)throw new InvalidDataException("The existing schema backup has an unsupported version. It was preserved; review it before migrating the library.");
            }
            using(var owned=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))ownsTemporary=true;
            await using(var destination=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=temporary,Pooling=false }.ToString()))
            {
                await destination.OpenAsync(ct); source.BackupDatabase(destination);
                using var verify=destination.CreateCommand(); verify.CommandText="PRAGMA integrity_check";
                if((string?)await verify.ExecuteScalarAsync(ct)!="ok") throw new InvalidDataException("The pre-update library backup could not be verified. Migration was cancelled.");
                verify.CommandText="PRAGMA user_version";
                if(Convert.ToInt32(await verify.ExecuteScalarAsync(ct))!=version) throw new InvalidDataException("The backup schema could not be verified. Migration was cancelled.");
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary,backup,true);
        }
        finally { if(ownsTemporary&&File.Exists(temporary)) File.Delete(temporary); }
    }
    private static InvalidDataException UnsupportedAccountSchema()=>new("This account library has an unsupported schema version. Preserve it and initialize a compatible Zenith Sessions version before using it.");
    public async Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct)
    {
        if (query.Page < 0 || query.PageSize is < 1 or > 100 || query.Page > 10000000 || query.Search is null || query.Search.Length > 200)
            throw new ArgumentException("Invalid account page or search.");
        string? group = query.Group is null ? null : AccountGroups.ValidateName(query.Group);
        try
        {
        await using var db = await OpenAsync(ct);
        // Read-only: deferred keeps one SHARED snapshot for count and page without taking the writers' RESERVED lock.
        using var tx = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand(); command.Transaction = tx;
        const string where = " WHERE ($group IS NULL OR group_name=$group COLLATE ZENITH_GROUP OR EXISTS(SELECT 1 FROM json_each(zenith_groups_json(group_name,additional_groups)) AS membership WHERE membership.value=$group COLLATE ZENITH_GROUP)) AND (instr(lower(label),lower($search))>0 OR instr(lower(username),lower($search))>0 OR instr(lower(notes),lower($search))>0) ";
        command.Parameters.AddWithValue("$group", (object?)group ?? DBNull.Value);
        command.Parameters.AddWithValue("$search", query.Search.Trim());
        command.CommandText = "SELECT count(*) FROM profiles" + where;
        int total = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        string order = query.Order switch { AccountOrder.Priority => "priority DESC,label COLLATE NOCASE,id", AccountOrder.RecentlyUpdated => "updated_utc DESC,id", AccountOrder.FavoritesFirst => "is_favorite DESC,label COLLATE NOCASE,id", _ => "label COLLATE NOCASE,id" };
        command.CommandText = "SELECT "+ProfileColumns+" FROM profiles" + where + "ORDER BY " + order + " LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", query.PageSize); command.Parameters.AddWithValue("$offset", (long)query.Page * query.PageSize);
        var rows = new List<AccountProfile>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) rows.Add(ReadProfile(reader));
        }
        tx.Commit(); return new(rows, total, query.Page, query.PageSize);
        }
        catch(SqliteException error) when(error.SqliteErrorCode==1) { throw InvalidGroups(); }
    }
    public async Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty) throw new ArgumentException("Profile identity is required.");
        await using var db = await OpenAsync(ct); using var command=db.CreateCommand();
        command.CommandText="SELECT "+ProfileColumns+" FROM profiles WHERE id=$id";
        command.Parameters.AddWithValue("$id",id.ToString("D"));
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct)) return null;
        return ReadProfile(reader);
    }
    private static bool ReadFavorite(SqliteDataReader reader)=>reader.GetInt64(10) switch
    { 0=>false,1=>true,_=>throw new InvalidDataException("A profile contains an invalid favorite state. Preserve the library and restore a verified backup.") };
    private static AccountProfile ReadProfile(SqliteDataReader reader)
    {
        try
        {
            string primary = reader.GetString(3), additional = ReadAdditionalGroups(primary, reader.GetString(11));
            return new AccountProfile(Guid.Parse(reader.GetString(0)),reader.GetString(1),reader.GetString(2),primary,reader.GetInt32(4),
                reader.IsDBNull(5)?null:reader.GetInt64(5),reader.IsDBNull(6)?null:reader.GetInt64(6),DateTimeOffset.Parse(reader.GetString(7),System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(8),reader.GetString(9),ReadFavorite(reader),additional).Validated();
        }
        catch(Exception error) when(error is ArgumentException or FormatException or InvalidCastException or OverflowException) { throw InvalidGroups(); }
    }
    private static string ReadAdditionalGroups(string primary, string json)
    {
        try
        {
            primary = AccountGroups.ValidateName(primary);
            if (json is null || json.Length > MaximumGroupsJsonLength) throw InvalidGroups();
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
            if (parsed.RootElement.ValueKind != JsonValueKind.Array || parsed.RootElement.GetArrayLength() > AccountGroups.MaximumAdditionalGroups) throw InvalidGroups();
            var names = new List<string>(parsed.RootElement.GetArrayLength());
            foreach (var item in parsed.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name || AccountGroups.ValidateName(name) != name) throw InvalidGroups();
                names.Add(name);
            }
            string input = string.Join('\n', names), canonical = AccountGroups.Canonicalize(primary, input);
            if (canonical != input || (canonical.Length == 0 ? 0 : canonical.Split('\n').Length) != names.Count) throw InvalidGroups();
            return canonical;
        }
        catch(Exception error) when(error is JsonException or ArgumentException or InvalidOperationException) { throw InvalidGroups(); }
    }
    private static string GroupsJson(AccountProfile profile) => JsonSerializer.Serialize(profile.AdditionalGroups.Length == 0 ? Array.Empty<string>() : profile.AdditionalGroups.Split('\n'));
    private static InvalidDataException InvalidGroups() => new("A profile contains invalid group metadata. The library was preserved; review it or restore a verified backup.");
    public async Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct)
    {
        try
        {
        await using var db = await OpenAsync(ct); using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM (SELECT name COLLATE ZENITH_GROUP AS name FROM account_groups UNION SELECT group_name COLLATE ZENITH_GROUP AS name FROM profiles UNION SELECT membership.value COLLATE ZENITH_GROUP AS name FROM profiles,json_each(zenith_groups_json(group_name,additional_groups)) AS membership) ORDER BY name COLLATE ZENITH_GROUP LIMIT 1000";
        var groups = new List<string>(); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) groups.Add(AccountGroups.ValidateName(reader.GetString(0)));
        return groups.AsReadOnly();
        }
        catch(SqliteException error) when(error.SqliteErrorCode==1) { throw InvalidGroups(); }
    }
    public async Task<AccountProfile> SaveAsync(AccountProfile account, CancellationToken ct)
    {
        account = account.Validated();
        await _writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);using var tx=db.BeginTransaction();using var command = db.CreateCommand();command.Transaction=tx;
            await RequireCurrentAccountSchemaAsync(db,tx,ct);
            var saved = account with { UpdatedUtc = DateTimeOffset.UtcNow, Revision = account.Revision + 1 };
            command.CommandText = account.Revision == 0
                ? "INSERT INTO profiles("+ProfileColumns+") SELECT $id,$label,$username,$group,$priority,$place,$user,$updated,$next,$notes,$favorite,$additional WHERE NOT EXISTS(SELECT 1 FROM credential_cleanup WHERE profile_id=$id) ON CONFLICT(id) DO NOTHING"
                : "UPDATE profiles SET label=$label,username=$username,group_name=$group,priority=$priority,place_id=$place,user_id=$user,updated_utc=$updated,revision=$next,notes=$notes,is_favorite=$favorite,additional_groups=$additional WHERE id=$id AND revision=$previous";
            command.Parameters.AddWithValue("$id", saved.Id.ToString("D")); command.Parameters.AddWithValue("$label", saved.Label);
            command.Parameters.AddWithValue("$username", saved.Username); command.Parameters.AddWithValue("$group", saved.Group);
            command.Parameters.AddWithValue("$priority", saved.Priority); command.Parameters.AddWithValue("$place", (object?)saved.PreferredPlaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$user", (object?)saved.RobloxUserId ?? DBNull.Value); command.Parameters.AddWithValue("$updated", saved.UpdatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$next", saved.Revision); command.Parameters.AddWithValue("$previous", account.Revision);
            command.Parameters.AddWithValue("$notes",saved.Notes); command.Parameters.AddWithValue("$favorite",saved.IsFavorite?1:0);
            command.Parameters.AddWithValue("$additional",GroupsJson(saved));
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new RevisionConflictException();
            if(await EnsureProfileGroupDefinitionsAsync(db,tx,saved,ct))await AdvanceCatalogAsync(db,tx,ct);
            ct.ThrowIfCancellationRequested();tx.Commit();
            return saved;
        }
        finally { _writer.Release(); }
    }
    public async Task DeleteAsync(Guid id, long revision, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); using var tx=db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction=tx;
            await RequireCurrentAccountSchemaAsync(db,tx,ct);
            command.CommandText = "DELETE FROM profiles WHERE id=$id AND revision=$revision";
            command.Parameters.AddWithValue("$id", id.ToString("D")); command.Parameters.AddWithValue("$revision", revision);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new RevisionConflictException();
            command.CommandText="INSERT INTO credential_cleanup(profile_id) VALUES($id) ON CONFLICT DO NOTHING";
            await command.ExecuteNonQueryAsync(ct); ct.ThrowIfCancellationRequested(); tx.Commit();
        }
        finally { _writer.Release(); }
    }
    public async Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct)
    {
        var validated = ValidateNewProfiles(profiles);
        await _writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); using var tx = db.BeginTransaction();
            await RequireCurrentAccountSchemaAsync(db,tx,ct);
            if(await InsertNewProfilesAsync(db,tx,validated,ct))await AdvanceCatalogAsync(db,tx,ct);
            ct.ThrowIfCancellationRequested(); tx.Commit();
        }
        finally { _writer.Release(); }
    }
    public async Task<int> ImportMetadataAsync(IReadOnlyList<ProfileMetadataGroup> groups, IReadOnlyList<AccountProfile> profiles, CancellationToken ct)
    {
        var definitions = ValidateImportedGroups(groups); var validated = ValidateNewProfiles(profiles);
        await _writer.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            // One connection, one transaction and one commit for every group and profile, so a failure or
            // cancellation part way leaves no saved group or profile behind.
            await using var db = await OpenAsync(ct); using var tx = db.BeginTransaction();
            await RequireCurrentAccountSchemaAsync(db,tx,ct);
            // Saved groups first: creating a profile adds a missing membership group without a color at the end of the list.
            int added = 0;
            foreach (var group in definitions)
            {
                ct.ThrowIfCancellationRequested();
                if (await FindGroupByNameAsync(db,tx,group.Name,ct) is not null) continue;
                await InsertGroupAsync(db,tx,group.Name,group.ColorHex,ct); added++;
            }
            bool membershipGroupsAdded = await InsertNewProfilesAsync(db,tx,validated,ct);
            if(added > 0 || membershipGroupsAdded)await AdvanceCatalogAsync(db,tx,ct);
            ct.ThrowIfCancellationRequested(); tx.Commit(); return added;
        }
        finally { _writer.Release(); }
    }
    private static AccountProfile[] ValidateNewProfiles(IReadOnlyList<AccountProfile> profiles)
    {
        if (profiles.Count > 100000 || profiles.Select(p => p.Id).Distinct().Count() != profiles.Count)
            throw new ArgumentException("Import requires distinct profiles and at most 100,000 rows.");
        var validated = profiles.Select(p => p.Validated()).ToArray();
        if (validated.Any(p => p.Revision != 0)) throw new ArgumentException("Imported profiles must be new records.");
        return validated;
    }
    private static ProfileMetadataGroup[] ValidateImportedGroups(IReadOnlyList<ProfileMetadataGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count > ProfileMetadataImport.MaximumGroups) throw new ArgumentException("Import at most 100,000 saved groups per operation.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new ProfileMetadataGroup[groups.Count];
        for (int index = 0; index < result.Length; index++)
        {
            var group = groups[index] ?? throw new ArgumentException("Every imported saved group must be present.");
            result[index] = group with { Name = AccountGroups.ValidateName(group.Name), ColorHex = AccountGroupDefinition.ValidateColor(group.ColorHex) };
            if (!names.Add(result[index].Name)) throw new ArgumentException("Each imported saved group needs its own name.");
        }
        return result;
    }
    /// <summary>Inserts validated new profiles inside the caller's transaction. Returns whether a missing membership group was added.</summary>
    private static async Task<bool> InsertNewProfilesAsync(SqliteConnection db, SqliteTransaction tx, AccountProfile[] validated, CancellationToken ct)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO profiles("+ProfileColumns+") SELECT $id,$label,$username,$group,$priority,$place,$user,$updated,1,$notes,$favorite,$additional WHERE NOT EXISTS(SELECT 1 FROM credential_cleanup WHERE profile_id=$id)";
        foreach (var p in validated)
        {
            ct.ThrowIfCancellationRequested(); command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", p.Id.ToString("D")); command.Parameters.AddWithValue("$label", p.Label);
            command.Parameters.AddWithValue("$username", p.Username); command.Parameters.AddWithValue("$group", p.Group);
            command.Parameters.AddWithValue("$priority", p.Priority); command.Parameters.AddWithValue("$place", (object?)p.PreferredPlaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$user", (object?)p.RobloxUserId ?? DBNull.Value); command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$notes",p.Notes); command.Parameters.AddWithValue("$favorite",p.IsFavorite?1:0);
            command.Parameters.AddWithValue("$additional",GroupsJson(p));
            if(await command.ExecuteNonQueryAsync(ct)!=1) throw new RevisionConflictException();
        }
        bool catalogChanged=false;
        foreach(var profile in validated)catalogChanged|=await EnsureProfileGroupDefinitionsAsync(db,tx,profile,ct);
        return catalogChanged;
    }
    public async Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); using var tx = db.BeginTransaction();
            using var command = db.CreateCommand(); command.Transaction = tx;
            await RequireCurrentAccountSchemaAsync(db,tx,ct);
            foreach (var p in profiles)
            {
                ct.ThrowIfCancellationRequested(); command.Parameters.Clear();
                command.CommandText = "DELETE FROM profiles WHERE id=$id AND revision=$revision";
                command.Parameters.AddWithValue("$id", p.Id.ToString("D")); command.Parameters.AddWithValue("$revision", p.Revision);
                if (await command.ExecuteNonQueryAsync(ct) != 1) throw new RevisionConflictException();
                command.CommandText="INSERT INTO credential_cleanup(profile_id) VALUES($id) ON CONFLICT DO NOTHING";
                await command.ExecuteNonQueryAsync(ct);
            }
            ct.ThrowIfCancellationRequested(); tx.Commit();
        }
        finally { _writer.Release(); }
    }
    public async Task<int> ApplyGroupAsync(IReadOnlyList<AccountProfile> expected, string group, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(expected); group = AccountGroups.ValidateName(group);
        int count = expected.Count;
        if (count is < 0 or > IAccountGroupEditor.MaximumBatchSize) throw new ArgumentException("Choose at most 100 saved profiles for one group edit.");
        var pinned = new AccountProfile[count]; var ids = new HashSet<Guid>();
        for (int index = 0; index < count; index++)
        {
            pinned[index] = (expected[index] ?? throw new ArgumentException("Every selected profile must be present.")).Validated();
            if (pinned[index].Revision <= 0 || !ids.Add(pinned[index].Id)) throw new ArgumentException("Choose distinct, already saved profiles with their current revisions.");
        }
        if (count == 0) return 0;
        // Microsoft.Data.Sqlite's async calls may complete synchronously. Keep bounded native
        // transaction/lock waits off the calling UI context, while retaining the actual owner.
        await _writer.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var db = await OpenAsync(ct); using var tx = db.BeginTransaction();
            using var command = db.CreateCommand(); command.Transaction = tx;
            await RequireCurrentAccountSchemaAsync(db,tx,ct);
            var changes = new List<AccountProfile>(count); DateTimeOffset updated = DateTimeOffset.UtcNow;
            // Validate EVERY revision, including no-ops, and every resulting membership limit
            // before writing. A conflict/limit/cancellation rolls back the entire transaction.
            foreach (var profile in pinned)
            {
                ct.ThrowIfCancellationRequested(); command.Parameters.Clear();
                command.CommandText = "SELECT "+ProfileColumns+" FROM profiles WHERE id=$id";
                command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
                AccountProfile current;
                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct)) throw new RevisionConflictException();
                    current = ReadProfile(reader);
                }
                if (current.Revision != profile.Revision) throw new RevisionConflictException();
                var next = AccountGroups.Add(current, group);
                if (next.AdditionalGroups != current.AdditionalGroups)
                    changes.Add((next with { Revision = checked(current.Revision + 1), UpdatedUtc = updated }).Validated());
            }
            foreach (var next in changes)
            {
                ct.ThrowIfCancellationRequested(); command.Parameters.Clear();
                command.CommandText = "UPDATE profiles SET additional_groups=$additional,revision=$next,updated_utc=$updated WHERE id=$id AND revision=$previous";
                command.Parameters.AddWithValue("$id", next.Id.ToString("D")); command.Parameters.AddWithValue("$additional", GroupsJson(next));
                command.Parameters.AddWithValue("$next", next.Revision); command.Parameters.AddWithValue("$previous", next.Revision - 1);
                command.Parameters.AddWithValue("$updated", next.UpdatedUtc.ToString("O"));
                if (await command.ExecuteNonQueryAsync(ct) != 1) throw new RevisionConflictException();
            }
            if(changes.Count>0 && await EnsureGroupDefinitionAsync(db,tx,group,ct))await AdvanceCatalogAsync(db,tx,ct);
            ct.ThrowIfCancellationRequested(); tx.Commit(); return changes.Count;
        }
        finally { _writer.Release(); }
    }
    public async Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct)
    {
        // Read-only: deferred (SHARED) like the page read, so it never waits for another connection's write reservation.
        await using var db=await OpenAsync(ct); using var tx=db.BeginTransaction(deferred:true); using var command=db.CreateCommand(); command.Transaction=tx;
        command.CommandText="SELECT count(*) FROM credential_cleanup"; int count=Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        command.CommandText="SELECT profile_id FROM credential_cleanup ORDER BY profile_id LIMIT 100";
        var ids=new List<Guid>();
        await using(var reader=await command.ExecuteReaderAsync(ct)) while(await reader.ReadAsync(ct)) ids.Add(Guid.Parse(reader.GetString(0)));
        tx.Commit(); return new(ids,count);
    }
    public async Task CompleteCredentialCleanupAsync(Guid id,CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            await using var db=await OpenAsync(ct);using var tx=db.BeginTransaction(deferred:false);using var command=db.CreateCommand();command.Transaction=tx;
            await RequireCurrentAccountSchemaAsync(db,tx,ct);
            command.CommandText="DELETE FROM credential_cleanup WHERE profile_id=$id"; command.Parameters.AddWithValue("$id",id.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);ct.ThrowIfCancellationRequested();tx.Commit();
        }
        finally { _writer.Release(); }
    }
    public void Dispose()=>_writer.Dispose();
}
