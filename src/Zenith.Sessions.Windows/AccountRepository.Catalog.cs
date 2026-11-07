using System.Globalization;
using Microsoft.Data.Sqlite;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class AccountRepository
{
    private const string GroupCatalogColumns = "CASE WHEN typeof(id)='text' AND length(CAST(id AS BLOB)) BETWEEN 1 AND 64 THEN id END,"
        + "CASE WHEN typeof(name)='text' AND length(name) BETWEEN 1 AND 60 AND length(CAST(name AS BLOB))<=240 THEN name END,"
        + "CASE WHEN color_hex IS NULL OR (typeof(color_hex)='text' AND length(CAST(color_hex AS BLOB))=7) THEN color_hex END,"
        + "CASE WHEN typeof(sort_order)='integer' THEN sort_order END,CASE WHEN typeof(revision)='integer' THEN revision END,"
        + "CASE WHEN typeof(updated_utc)='text' AND length(CAST(updated_utc AS BLOB)) BETWEEN 1 AND 128 THEN updated_utc END,"
        + "CASE WHEN color_hex IS NULL OR (typeof(color_hex)='text' AND length(CAST(color_hex AS BLOB))=7) THEN 1 ELSE 0 END";

    public async Task<AccountGroupCatalogPage> ReadGroupCatalogAsync(AccountGroupCatalogQuery query,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);query=query.Validated();ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var db=await OpenAsync(ct);using var tx=db.BeginTransaction(deferred:true);
            long revision=await ReadCatalogRevisionAsync(db,tx,ct);
            using var command=db.CreateCommand();command.Transaction=tx;
            const string where=" WHERE zenith_group_matches(CASE WHEN typeof(name)='text' AND length(name) BETWEEN 1 AND 60 AND length(CAST(name AS BLOB))<=240 THEN name ELSE '' END,$search)=1 ";
            command.Parameters.AddWithValue("$search",query.Search);command.CommandText="SELECT count(*) FROM account_groups"+where;
            int total=Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            command.CommandText="SELECT "+GroupCatalogColumns+" FROM account_groups"+where+"ORDER BY sort_order,id LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$limit",query.PageSize);command.Parameters.AddWithValue("$offset",(long)query.Page*query.PageSize);
            var items=new List<AccountGroupDefinition>(query.PageSize);
            await using(var reader=await command.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct)){ct.ThrowIfCancellationRequested();items.Add(ReadGroupDefinition(reader));}
            ct.ThrowIfCancellationRequested();tx.Commit();
            return new(Array.AsReadOnly(items.ToArray()),total,query.Page,query.PageSize,revision);
        }
        catch(SqliteException error)when(error.SqliteErrorCode==1){throw InvalidCatalog();}
    }
    public async Task<AccountGroupDefinition?> FindGroupAsync(string name,CancellationToken ct)
    {
        name=AccountGroups.ValidateName(name);await using var db=await OpenAsync(ct);using var tx=db.BeginTransaction(deferred:true);
        _=await ReadCatalogRevisionAsync(db,tx,ct);var result=await FindGroupByNameAsync(db,tx,name,ct);ct.ThrowIfCancellationRequested();tx.Commit();return result;
    }
    public async Task<AccountGroupDefinition?> FindGroupAsync(Guid id,CancellationToken ct)
    {
        if(id==Guid.Empty)throw new ArgumentException("Choose a saved group.");
        await using var db=await OpenAsync(ct);using var tx=db.BeginTransaction(deferred:true);
        _=await ReadCatalogRevisionAsync(db,tx,ct);var result=await FindGroupByIdAsync(db,tx,id,ct);ct.ThrowIfCancellationRequested();tx.Commit();return result;
    }
    public Task<AccountGroupDefinition> CreateGroupAsync(string name,string? colorHex,long expectedCatalogRevision,CancellationToken ct)
    {
        name=AccountGroups.ValidateName(name);colorHex=AccountGroupDefinition.ValidateColor(colorHex);RequireExpectedCatalog(expectedCatalogRevision);
        return CatalogWriteAsync(async(db,tx)=>
        {
            await RequireCatalogAsync(db,tx,expectedCatalogRevision,ct);
            if(await FindGroupByNameAsync(db,tx,name,ct)is not null)throw new ArgumentException("This group already exists. Select it instead of creating another definition.");
            var created=await InsertGroupAsync(db,tx,name,colorHex,ct);await AdvanceCatalogAsync(db,tx,ct);return created;
        },ct);
    }
    public Task<AccountGroupDefinition> SetGroupColorAsync(AccountGroupDefinition expected,string? colorHex,long expectedCatalogRevision,CancellationToken ct)
    {
        RequireExpectedGroup(expected);colorHex=AccountGroupDefinition.ValidateColor(colorHex);RequireExpectedCatalog(expectedCatalogRevision);
        return CatalogWriteAsync(async(db,tx)=>
        {
            await RequireCatalogAsync(db,tx,expectedCatalogRevision,ct);var current=await RequireGroupAsync(db,tx,expected,ct);
            if(current.ColorHex==colorHex)return current;
            var next=current with{ColorHex=colorHex,Revision=NextCatalogRevision(current.Revision),UpdatedUtc=DateTimeOffset.UtcNow};
            await UpdateGroupDefinitionAsync(db,tx,current,next,ct);await AdvanceCatalogAsync(db,tx,ct);return next;
        },ct);
    }
    public Task<AccountGroupDefinition> MoveGroupAsync(AccountGroupDefinition expected,AccountGroupMoveDirection direction,long expectedCatalogRevision,CancellationToken ct)
    {
        RequireExpectedGroup(expected);RequireExpectedCatalog(expectedCatalogRevision);if(!Enum.IsDefined(direction))throw new ArgumentException("Choose move up or move down.");
        return CatalogWriteAsync(async(db,tx)=>
        {
            await RequireCatalogAsync(db,tx,expectedCatalogRevision,ct);var current=await RequireGroupAsync(db,tx,expected,ct);
            using var adjacent=db.CreateCommand();adjacent.Transaction=tx;
            adjacent.CommandText="SELECT "+GroupCatalogColumns+" FROM account_groups WHERE sort_order"+(direction==AccountGroupMoveDirection.Up?"<$order ORDER BY sort_order DESC":" >$order ORDER BY sort_order")+" LIMIT 1";
            adjacent.Parameters.AddWithValue("$order",current.SortOrder);AccountGroupDefinition? neighbor=null;
            await using(var reader=await adjacent.ExecuteReaderAsync(ct))if(await reader.ReadAsync(ct))neighbor=ReadGroupDefinition(reader);
            if(neighbor is null)return current;
            long temporary=await NextGroupOrderAsync(db,tx,ct);var now=DateTimeOffset.UtcNow;
            var moved=current with{SortOrder=neighbor.SortOrder,Revision=NextCatalogRevision(current.Revision),UpdatedUtc=now};
            var swapped=neighbor with{SortOrder=current.SortOrder,Revision=NextCatalogRevision(neighbor.Revision),UpdatedUtc=now};
            using(var reserve=db.CreateCommand())
            {
                reserve.Transaction=tx;reserve.CommandText="UPDATE account_groups SET sort_order=$temporary WHERE id=$id AND revision=$revision";
                reserve.Parameters.AddWithValue("$temporary",temporary);reserve.Parameters.AddWithValue("$id",current.Id.ToString("D"));reserve.Parameters.AddWithValue("$revision",current.Revision);
                if(await reserve.ExecuteNonQueryAsync(ct)!=1)throw new AccountGroupCatalogConflictException();
            }
            await UpdateGroupDefinitionAsync(db,tx,neighbor,swapped,ct);await UpdateGroupDefinitionAsync(db,tx,current,moved,ct);
            await AdvanceCatalogAsync(db,tx,ct);return moved;
        },ct);
    }

    private async Task<T> CatalogWriteAsync<T>(Func<SqliteConnection,SqliteTransaction,Task<T>> work,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();await _writer.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var db=await OpenAsync(ct);using var tx=db.BeginTransaction(deferred:false);
            await RequireCurrentAccountSchemaAsync(db,tx,ct);
            T result=await work(db,tx);ct.ThrowIfCancellationRequested();tx.Commit();return result;
        }
        finally{_writer.Release();}
    }
    private static async Task CreateGroupCatalogAsync(SqliteConnection db,SqliteTransaction tx,CancellationToken ct)
    {
        using(var create=db.CreateCommand())
        {
            create.Transaction=tx;create.CommandText="""
                CREATE TABLE account_group_catalog(singleton INTEGER PRIMARY KEY CHECK(singleton=1),revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0));
                INSERT INTO account_group_catalog VALUES(1,1);
                CREATE TABLE account_groups(id TEXT PRIMARY KEY,name TEXT NOT NULL COLLATE ZENITH_GROUP UNIQUE,
                    color_hex TEXT NULL,sort_order INTEGER NOT NULL UNIQUE CHECK(typeof(sort_order)='integer' AND sort_order>=0),
                    revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),updated_utc TEXT NOT NULL,
                    CHECK(length(name) BETWEEN 1 AND 60 AND length(CAST(name AS BLOB))<=240),
                    CHECK(color_hex IS NULL OR (length(color_hex)=7 AND substr(color_hex,1,1)='#' AND substr(color_hex,2) NOT GLOB '*[^0-9A-F]*')));
                """;await create.ExecuteNonQueryAsync(ct);
        }
        string? after=null;
        while(true)
        {
            var page=await ReadGroupChangePageAsync(db,tx,after,ct);if(page.Count==0)break;
            foreach(var row in page)
            {
                ct.ThrowIfCancellationRequested();after=row.Id;
                foreach(string name in AccountGroups.Names(row.Primary,row.Additional))await EnsureGroupDefinitionAsync(db,tx,name,ct);
            }
        }
        // Seed order alphabetically after all validated names have been found; one bounded page,
        // no picker LIMIT and no all-profile/all-group list in managed memory.
        string? lastName=null;long order=0;
        while(true)
        {
            using var select=db.CreateCommand();select.Transaction=tx;
            select.CommandText="SELECT "+GroupCatalogColumns+" FROM account_groups WHERE ($after IS NULL OR name>$after COLLATE ZENITH_GROUP) ORDER BY name COLLATE ZENITH_GROUP LIMIT 128";
            select.Parameters.AddWithValue("$after",(object?)lastName??DBNull.Value);var page=new List<AccountGroupDefinition>(128);
            await using(var reader=await select.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))page.Add(ReadGroupDefinition(reader));
            if(page.Count==0)break;
            // A disjoint positive range avoids UNIQUE collisions with insertion order. The
            // whole migration stays in one transaction; normal readers never see this phase.
            foreach(var item in page)
            {
                ct.ThrowIfCancellationRequested();lastName=item.Name;
                using var update=db.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE account_groups SET sort_order=$order WHERE id=$id";
                update.Parameters.AddWithValue("$order",checked(++order));update.Parameters.AddWithValue("$id",item.Id.ToString("D"));
                // Defer final ordering to a positive disjoint range before compacting below.
                update.Parameters["$order"].Value=long.MaxValue-order;await update.ExecuteNonQueryAsync(ct);
            }
        }
        using(var finish=db.CreateCommand())
        {
            finish.Transaction=tx;finish.CommandText="UPDATE account_groups SET sort_order=9223372036854775807-sort_order-1; PRAGMA user_version=5";
            await finish.ExecuteNonQueryAsync(ct);
        }
    }
    private static async Task<bool> EnsureProfileGroupDefinitionsAsync(SqliteConnection db,SqliteTransaction tx,AccountProfile profile,CancellationToken ct)
    {
        bool changed=false;foreach(string name in profile.GroupNames)changed|=await EnsureGroupDefinitionAsync(db,tx,name,ct);return changed;
    }
    private static async Task<bool> EnsureGroupDefinitionAsync(SqliteConnection db,SqliteTransaction tx,string name,CancellationToken ct)
    {
        if(await FindGroupByNameAsync(db,tx,name,ct)is not null)return false;
        await InsertGroupAsync(db,tx,name,null,ct);return true;
    }
    private static async Task<AccountGroupDefinition> InsertGroupAsync(SqliteConnection db,SqliteTransaction tx,string name,string? color,CancellationToken ct)
    {
        var item=new AccountGroupDefinition(Guid.NewGuid(),AccountGroups.ValidateName(name),color,await NextGroupOrderAsync(db,tx,ct),1,DateTimeOffset.UtcNow).Validated();
        using var insert=db.CreateCommand();insert.Transaction=tx;
        insert.CommandText="INSERT INTO account_groups(id,name,color_hex,sort_order,revision,updated_utc) VALUES($id,$name,$color,$order,$revision,$updated)";
        GroupParameters(insert,item);await insert.ExecuteNonQueryAsync(ct);return item;
    }
    private static async Task<long> NextGroupOrderAsync(SqliteConnection db,SqliteTransaction tx,CancellationToken ct)
    {
        using var query=db.CreateCommand();query.Transaction=tx;query.CommandText="SELECT coalesce(max(sort_order),-1) FROM account_groups";
        long maximum=Convert.ToInt64(await query.ExecuteScalarAsync(ct));
        if(maximum==long.MaxValue)throw InvalidCatalog();return checked(maximum+1);
    }
    private static async Task<long> ReadCatalogRevisionAsync(SqliteConnection db,SqliteTransaction tx,CancellationToken ct)
    {
        using var query=db.CreateCommand();query.Transaction=tx;query.CommandText="SELECT revision FROM account_group_catalog WHERE singleton=1 AND typeof(revision)='integer'";
        object? value=await query.ExecuteScalarAsync(ct);if(value is not long revision||revision<1)throw InvalidCatalog();return revision;
    }
    private static async Task RequireCurrentAccountSchemaAsync(SqliteConnection db,SqliteTransaction tx,CancellationToken ct)
    {
        using var query=db.CreateCommand();query.Transaction=tx;query.CommandText="PRAGMA user_version";
        if(Convert.ToInt64(await query.ExecuteScalarAsync(ct))!=5)throw UnsupportedAccountSchema();
    }
    private static async Task RequireCatalogAsync(SqliteConnection db,SqliteTransaction tx,long expected,CancellationToken ct)
    {if(await ReadCatalogRevisionAsync(db,tx,ct)!=expected)throw new AccountGroupCatalogConflictException();}
    private static async Task AdvanceCatalogAsync(SqliteConnection db,SqliteTransaction tx,CancellationToken ct)
    {
        long previous=await ReadCatalogRevisionAsync(db,tx,ct),next=NextCatalogRevision(previous);
        using var update=db.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE account_group_catalog SET revision=$next WHERE singleton=1 AND revision=$previous";
        update.Parameters.AddWithValue("$next",next);update.Parameters.AddWithValue("$previous",previous);
        if(await update.ExecuteNonQueryAsync(ct)!=1)throw new AccountGroupCatalogConflictException();
    }
    private static long NextCatalogRevision(long previous){if(previous<1||previous==long.MaxValue)throw InvalidCatalog();return previous+1;}
    private static void RequireExpectedCatalog(long revision){if(revision<1)throw new ArgumentException("Reload the saved group catalog before applying this change.");}
    private static void RequireExpectedGroup(AccountGroupDefinition expected)
    {ArgumentNullException.ThrowIfNull(expected);if(expected.Validated()!=expected)throw new ArgumentException("Choose the exact saved group before applying this change.");}
    private static async Task<AccountGroupDefinition> RequireGroupAsync(SqliteConnection db,SqliteTransaction tx,AccountGroupDefinition expected,CancellationToken ct)
    {var current=await FindGroupByIdAsync(db,tx,expected.Id,ct);if(current is null||current!=expected)throw new AccountGroupCatalogConflictException();return current;}
    private static Task<AccountGroupDefinition?> FindGroupByNameAsync(SqliteConnection db,SqliteTransaction tx,string name,CancellationToken ct)
        => FindGroupDefinitionAsync(db,tx,"name=$value COLLATE ZENITH_GROUP",name,ct);
    private static Task<AccountGroupDefinition?> FindGroupByIdAsync(SqliteConnection db,SqliteTransaction tx,Guid id,CancellationToken ct)
        => FindGroupDefinitionAsync(db,tx,"id=$value",id.ToString("D"),ct);
    private static async Task<AccountGroupDefinition?> FindGroupDefinitionAsync(SqliteConnection db,SqliteTransaction tx,string predicate,string value,CancellationToken ct)
    {
        using var query=db.CreateCommand();query.Transaction=tx;query.CommandText="SELECT "+GroupCatalogColumns+" FROM account_groups WHERE "+predicate;
        query.Parameters.AddWithValue("$value",value);await using var reader=await query.ExecuteReaderAsync(ct);return await reader.ReadAsync(ct)?ReadGroupDefinition(reader):null;
    }
    private static AccountGroupDefinition ReadGroupDefinition(SqliteDataReader reader)
    {
        try
        {
            foreach(int index in new[]{0,1,3,4,5})if(reader.IsDBNull(index))throw InvalidCatalog();
            if(reader.GetInt64(6)!=1)throw InvalidCatalog();
            var item=new AccountGroupDefinition(Guid.Parse(reader.GetString(0)),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),
                reader.GetInt64(3),reader.GetInt64(4),DateTimeOffset.Parse(reader.GetString(5),CultureInfo.InvariantCulture));
            if(item.Validated()!=item)throw InvalidCatalog();return item;
        }
        catch(Exception error)when(error is ArgumentException or FormatException or InvalidCastException or OverflowException){throw InvalidCatalog();}
    }
    private static async Task UpdateGroupDefinitionAsync(SqliteConnection db,SqliteTransaction tx,AccountGroupDefinition previous,AccountGroupDefinition next,CancellationToken ct)
    {
        using var update=db.CreateCommand();update.Transaction=tx;
        update.CommandText="UPDATE account_groups SET name=$name,color_hex=$color,sort_order=$order,revision=$revision,updated_utc=$updated WHERE id=$id AND revision=$previous";
        GroupParameters(update,next);update.Parameters.AddWithValue("$previous",previous.Revision);
        if(await update.ExecuteNonQueryAsync(ct)!=1)throw new AccountGroupCatalogConflictException();
    }
    private static void GroupParameters(SqliteCommand command,AccountGroupDefinition item)
    {
        command.Parameters.AddWithValue("$id",item.Id.ToString("D"));command.Parameters.AddWithValue("$name",item.Name);
        command.Parameters.AddWithValue("$color",(object?)item.ColorHex??DBNull.Value);command.Parameters.AddWithValue("$order",item.SortOrder);
        command.Parameters.AddWithValue("$revision",item.Revision);command.Parameters.AddWithValue("$updated",item.UpdatedUtc.ToString("O",CultureInfo.InvariantCulture));
    }
    private static InvalidDataException InvalidCatalog()=>new("The saved group catalog is invalid or its revisions cannot advance. The library was preserved; review a verified backup before retrying.");
}
