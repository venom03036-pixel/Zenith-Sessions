using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Owned temporary SQLite metadata only. No account/vault/browser/native or installed data.
public sealed class AccountGroupCatalogTests : IDisposable
{
    private readonly string _name="zenith-group-catalog-"+Guid.NewGuid().ToString("N");
    private string Root=>Path.Combine(Path.GetTempPath(),_name);
    private string DatabasePath=>Path.Combine(Root,"accounts.db");
    private static AccountProfile Profile(string label,string group="Raid",string extras="")=>AccountProfile.Create(label)with{Group=group,AdditionalGroups=extras};
    private async Task<AccountRepository> Open()
    {var repository=new AccountRepository(Root);try{await repository.InitializeAsync(default);return repository;}catch{repository.Dispose();throw;}}
    private async Task<SqliteConnection> Database(string? path=null,bool readOnly=false)
    {
        Directory.CreateDirectory(Root);var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path??DatabasePath,Pooling=false,
            Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWriteCreate,DefaultTimeout=5}.ToString());
        try{await db.OpenAsync();db.CreateCollation("ZENITH_GROUP",(a,b)=>StringComparer.OrdinalIgnoreCase.Compare(a,b));return db;}catch{await db.DisposeAsync();throw;}
    }
    private static async Task<long> Revision(AccountRepository repository)=>(await repository.ReadGroupCatalogAsync(new(),default)).CatalogRevision;
    private static async Task<AccountGroupDefinition> Create(AccountRepository repository,string name,string? color=null)
        =>await repository.CreateGroupAsync(name,color,await Revision(repository),default);

    [Fact]
    public async Task FreshCatalogIsEmptyAndIndependentDefinitionsSurviveReopenWithoutProfiles()
    {
        AccountGroupDefinition group;
        using(var repository=await Open())
        {
            var initial=await repository.ReadGroupCatalogAsync(new(),default);Assert.Equal(0,initial.Total);Assert.Equal(1,initial.CatalogRevision);
            group=await repository.CreateGroupAsync(" Empty team "," #abcdef ",initial.CatalogRevision,default);
            Assert.Equal("Empty team",group.Name);Assert.Equal("#ABCDEF",group.ColorHex);Assert.NotEqual(Guid.Empty,group.Id);Assert.Equal(1,group.Revision);Assert.Equal(0,group.SortOrder);
            Assert.Equal(0,(await repository.ReadAsync(new(),default)).Total);Assert.Equal(new[]{"Empty team"},await repository.ReadGroupsAsync(default));
            Assert.Empty(Directory.GetFiles(Root,"*.schema*.bak"));
        }
        using var reopened=await Open();Assert.Equal(group,await reopened.FindGroupAsync(group.Id,default));Assert.Equal(group,await reopened.FindGroupAsync("empty TEAM",default));
        var page=await reopened.ReadGroupCatalogAsync(new(),default);Assert.Equal(group,Assert.Single(page.Items));Assert.Equal(2,page.CatalogRevision);
        Assert.Throws<NotSupportedException>(()=>((IList<AccountGroupDefinition>)page.Items).Clear());
        Assert.Equal(5L,await SchemaVersion(DatabasePath));
    }

    [Fact]
    public async Task ColorAndAdjacentMovesUseCasAndDoNotReviseAnyProfile()
    {
        using var repository=await Open();var profile=await repository.SaveAsync(Profile("Metadata retained"),default);
        var first=(await repository.FindGroupAsync("Raid",default))!;var second=await Create(repository,"Second");var third=await Create(repository,"Third","#123456");
        long revision=await Revision(repository);var colored=await repository.SetGroupColorAsync(first,"#abcdef",revision,default);
        Assert.Equal(first.Id,colored.Id);Assert.Equal(first.Revision+1,colored.Revision);Assert.Equal("#ABCDEF",colored.ColorHex);Assert.Equal(revision+1,await Revision(repository));
        Assert.Equal(profile,await repository.FindAsync(profile.Id,default));
        long beforeMove=await Revision(repository);var moved=await repository.MoveGroupAsync(colored,AccountGroupMoveDirection.Down,beforeMove,default);
        Assert.Equal(second.SortOrder,moved.SortOrder);Assert.Equal(colored.Revision+1,moved.Revision);Assert.Equal("#ABCDEF",moved.ColorHex);
        Assert.Equal(new[]{second.Id,first.Id,third.Id},(await repository.ReadGroupCatalogAsync(new(),default)).Items.Select(item=>item.Id));
        var adjacent=(await repository.FindGroupAsync(second.Id,default))!;Assert.Equal(second.Revision+1,adjacent.Revision);Assert.Equal(first.SortOrder,adjacent.SortOrder);
        Assert.Equal(third,await repository.FindGroupAsync(third.Id,default));Assert.Equal(profile,await repository.FindAsync(profile.Id,default));
        await Assert.ThrowsAsync<AccountGroupCatalogConflictException>(()=>repository.SetGroupColorAsync(colored,null,beforeMove,default));
    }

    [Fact]
    public async Task BoundaryMovesAndUnchangedColorsAreExactNoopsButStillCheckTheSnapshot()
    {
        using var repository=await Open();var first=await Create(repository,"Only");long revision=await Revision(repository);
        Assert.Equal(first,await repository.SetGroupColorAsync(first,null,revision,default));
        Assert.Equal(first,await repository.MoveGroupAsync(first,AccountGroupMoveDirection.Up,revision,default));
        Assert.Equal(first,await repository.MoveGroupAsync(first,AccountGroupMoveDirection.Down,revision,default));Assert.Equal(revision,await Revision(repository));
        await Create(repository,"Newer");
        await Assert.ThrowsAsync<AccountGroupCatalogConflictException>(()=>repository.SetGroupColorAsync(first,null,revision,default));
        await Assert.ThrowsAsync<AccountGroupCatalogConflictException>(()=>repository.MoveGroupAsync(first,AccountGroupMoveDirection.Up,revision,default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#12345G")]
    [InlineData("#12345678")]
    [InlineData("https://invalid.test/")]
    public async Task InvalidColorsDoNotCreateOrAlterAGroup(string color)
    {
        using var repository=await Open();var first=await Create(repository,"Saved");long revision=await Revision(repository);
        await Assert.ThrowsAsync<ArgumentException>(()=>repository.CreateGroupAsync("Rejected",color,revision,default));
        await Assert.ThrowsAsync<ArgumentException>(()=>repository.SetGroupColorAsync(first,color,revision,default));
        Assert.Null(await repository.FindGroupAsync("Rejected",default));Assert.Equal(first,await repository.FindGroupAsync(first.Id,default));Assert.Equal(revision,await Revision(repository));
    }

    [Fact]
    public async Task UnicodeDuplicateNamesAndForgedOrStaleDefinitionsDoNotLoseCatalogUpdates()
    {
        using var first=await Open();using var second=await Open();var team=await Create(first,"Équipe");long revision=await Revision(first);
        await Assert.ThrowsAsync<ArgumentException>(()=>first.CreateGroupAsync("éQUIPE",null,revision,default));
        await Assert.ThrowsAsync<AccountGroupCatalogConflictException>(()=>first.SetGroupColorAsync(team with{Name="Different"},"#112233",revision,default));
        var changed=await second.SetGroupColorAsync(team,"#112233",revision,default);
        await Assert.ThrowsAsync<AccountGroupCatalogConflictException>(()=>first.MoveGroupAsync(team,AccountGroupMoveDirection.Up,revision,default));
        Assert.Equal(changed,await first.FindGroupAsync("ÉQUIPE",default));
    }

    [Fact]
    public async Task CatalogPagesAndUnicodeSearchAreBoundedWithoutLimitingGroupsOrProfiles()
    {
        using var repository=await Open();
        await repository.CreateManyAsync(Enumerable.Range(0,1005).Select(index=>Profile("Profile "+index,"Group "+index.ToString("D4"))).ToArray(),default);
        var special=await Create(repository,"Équipe empty");var first=await repository.ReadGroupCatalogAsync(new(PageSize:1000),default);
        Assert.Equal(1006,first.Total);Assert.Equal(1000,first.Items.Count);var last=await repository.ReadGroupCatalogAsync(new(Page:1,PageSize:1000),default);
        Assert.Equal(6,last.Items.Count);Assert.Equal(first.CatalogRevision,last.CatalogRevision);Assert.Equal(special,await repository.FindGroupAsync(special.Id,default));
        Assert.Equal(special,Assert.Single((await repository.ReadGroupCatalogAsync(new("éQUIPE"),default)).Items));
        Assert.Equal(1000,(await repository.ReadGroupsAsync(default)).Count);Assert.Equal(1005,(await repository.ReadAsync(new(),default)).Total);
        await Assert.ThrowsAsync<ArgumentException>(()=>repository.ReadGroupCatalogAsync(new(PageSize:1001),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>repository.ReadGroupCatalogAsync(new(Page:-1),default));
    }

    [Fact]
    public async Task MoveResolvesAnOffPageNeighborAgainstTheGlobalCatalogRevision()
    {
        using var repository=await Open();var a=await Create(repository,"A");var b=await Create(repository,"B");var c=await Create(repository,"C");
        var page=await repository.ReadGroupCatalogAsync(new(Page:1,PageSize:1),default);Assert.Equal(b,Assert.Single(page.Items));
        await repository.MoveGroupAsync(b,AccountGroupMoveDirection.Up,page.CatalogRevision,default);
        Assert.Equal(new[]{b.Id,a.Id,c.Id},(await repository.ReadGroupCatalogAsync(new(),default)).Items.Select(item=>item.Id));
    }

    [Fact]
    public async Task ProfileSaveImportAndMembershipAddsEnsureDefinitionsOnlyAfterTheirRevisionsSucceed()
    {
        using var repository=await Open();var profile=await repository.SaveAsync(Profile("A","Primary","Extra"),default);
        Assert.NotNull(await repository.FindGroupAsync("Primary",default));Assert.NotNull(await repository.FindGroupAsync("Extra",default));
        var newer=await repository.SaveAsync(profile with{Notes="Newer"},default);long revision=await Revision(repository);
        await Assert.ThrowsAsync<RevisionConflictException>(()=>repository.SaveAsync(profile with{Group="Ghost"},default));
        await Assert.ThrowsAsync<RevisionConflictException>(()=>repository.ApplyGroupAsync([profile],"Ghost",default));
        Assert.Null(await repository.FindGroupAsync("Ghost",default));Assert.Equal(revision,await Revision(repository));
        await Assert.ThrowsAsync<SqliteException>(()=>repository.CreateManyAsync([Profile("Would be inserted","Ghost"),profile with{Revision=0}],default));
        Assert.Null(await repository.FindGroupAsync("Ghost",default));Assert.Equal(1,(await repository.ReadAsync(new(),default)).Total);
        Assert.Equal(1,await repository.ApplyGroupAsync([newer],"Raid",default));Assert.NotNull(await repository.FindGroupAsync("Raid",default));
        var changed=(await repository.FindAsync(profile.Id,default))!;await repository.DeleteAsync(changed.Id,changed.Revision,default);
        Assert.Equal(3,(await repository.ReadGroupCatalogAsync(new(),default)).Total);Assert.Equal(0,(await repository.ReadAsync(new(),default)).Total);
    }

    [Fact]
    public async Task EmptyRenameAndRemovalKeepStableIdentityAndOnlyCreateFallbacksWhenPrimaryMembersNeedThem()
    {
        using var repository=await Open();var source=await Create(repository,"Empty","#ABCDEF");
        var preview=await repository.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename,"Empty","Renamed"),default);
        Assert.True(preview.SourceExists);Assert.Equal(0,preview.AffectedCount);Assert.Equal(await Revision(repository),preview.CatalogRevision);
        Assert.Equal(0,await repository.ApplyGroupChangeAsync(preview,default));var renamed=(await repository.FindGroupAsync("Renamed",default))!;
        Assert.Equal(source.Id,renamed.Id);Assert.Equal(source.ColorHex,renamed.ColorHex);Assert.Equal(source.SortOrder,renamed.SortOrder);Assert.Equal(source.Revision+1,renamed.Revision);
        Assert.Equal(0,await repository.ApplyGroupChangeAsync(await repository.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Remove,"Renamed","Unused fallback"),default),default));
        Assert.Null(await repository.FindGroupAsync(source.Id,default));Assert.Null(await repository.FindGroupAsync("Unused fallback",default));
        var profile=await repository.SaveAsync(Profile("Primary member","Primary"),default);
        Assert.Equal(1,await repository.ApplyGroupChangeAsync(await repository.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Remove,"Primary","Materialized"),default),default));
        Assert.NotNull(await repository.FindGroupAsync("Materialized",default));Assert.Equal("Materialized",(await repository.FindAsync(profile.Id,default))!.Group);
    }

    [Fact]
    public async Task MergeConservesDestinationIdentityAppearanceAndOrderIncludingEmptySources()
    {
        using var repository=await Open();var source=await Create(repository,"Source","#111111");var destination=await Create(repository,"Destination","#ABCDEF");
        var profile=await repository.SaveAsync(Profile("Source account","Source","Keep\nDestination"),default);
        var preview=await repository.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename,"Source","destination",true),default);
        Assert.Equal(1,await repository.ApplyGroupChangeAsync(preview,default));Assert.Equal(destination,await repository.FindGroupAsync(destination.Id,default));
        Assert.Null(await repository.FindGroupAsync(source.Id,default));var current=(await repository.FindAsync(profile.Id,default))!;
        Assert.Equal("Destination",current.Group);Assert.Equal("Keep",current.AdditionalGroups);
        var empty=await Create(repository,"Empty");
        var unconfirmed=await repository.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename,"Empty","Destination"),default);
        await Assert.ThrowsAsync<ArgumentException>(()=>repository.ApplyGroupChangeAsync(unconfirmed,default));
        Assert.Equal(0,await repository.ApplyGroupChangeAsync(await repository.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename,"Empty","Destination",true),default),default));
        Assert.Null(await repository.FindGroupAsync(empty.Id,default));Assert.Equal(destination,await repository.FindGroupAsync(destination.Id,default));
    }

    [Theory]
    [InlineData("source-color")]
    [InlineData("destination-color")]
    [InlineData("order")]
    [InlineData("new-empty-destination")]
    public async Task LifecyclePreviewBindsCatalogRevisionAndFullSourceDestinationDefinitions(string mutation)
    {
        using var repository=await Open();var source=await Create(repository,"Source");AccountGroupDefinition? target=null;
        if(mutation!="new-empty-destination")target=await Create(repository,"Target");
        var preview=await repository.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename,"Source","Target",true),default);
        switch(mutation)
        {
            case "source-color":await repository.SetGroupColorAsync(source,"#123456",await Revision(repository),default);break;
            case "destination-color":await repository.SetGroupColorAsync(target!,"#123456",await Revision(repository),default);break;
            case "order":await repository.MoveGroupAsync(source,AccountGroupMoveDirection.Down,await Revision(repository),default);break;
            default:await Create(repository,"Target");break;
        }
        string before=await CatalogState();await Assert.ThrowsAsync<RevisionConflictException>(()=>repository.ApplyGroupChangeAsync(preview,default));Assert.Equal(before,await CatalogState());
    }

    [Fact]
    public async Task AFailureAfterMembershipWritesRollsBackTheCatalogAndMaterializedFallbackAsWell()
    {
        using var repository=await Open();var profile=await repository.SaveAsync(Profile("Member"),default);var definition=(await repository.FindGroupAsync("Raid",default))!;
        await Execute("CREATE TRIGGER synthetic_catalog_delete_failure BEFORE DELETE ON account_groups WHEN OLD.name='Raid' BEGIN SELECT RAISE(ABORT,'synthetic catalog failure'); END");
        var preview=await repository.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Remove,"Raid","New fallback"),default);long revision=await Revision(repository);
        await Assert.ThrowsAsync<SqliteException>(()=>repository.ApplyGroupChangeAsync(preview,default));
        Assert.Equal(profile,await repository.FindAsync(profile.Id,default));Assert.Equal(definition,await repository.FindGroupAsync("Raid",default));
        Assert.Null(await repository.FindGroupAsync("New fallback",default));Assert.Equal(revision,await Revision(repository));
    }

    [Fact]
    public async Task CompetingInstancesHaveOneCatalogCasWinnerWithoutLostColors()
    {
        using var one=await Open();using var two=await Open();var group=await Create(one,"Shared");long revision=await Revision(one);
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> Run(AccountRepository repository,string color){await gate.Task;return await Record.ExceptionAsync(()=>repository.SetGroupColorAsync(group,color,revision,default));}
        var a=Run(one,"#112233");var b=Run(two,"#AABBCC");gate.TrySetResult();var results=await Task.WhenAll(a,b).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Single(results,error=>error is null);Assert.Single(results,error=>error is AccountGroupCatalogConflictException);
        Assert.Contains((await one.FindGroupAsync(group.Id,default))!.ColorHex,new[]{"#112233","#AABBCC"});Assert.Equal(revision+1,await Revision(one));
    }

    [Fact]
    public async Task CancelledCatalogMutationsCreateNoGhostDefinitionsAndKeepRevisions()
    {
        using var repository=await Open();var group=await Create(repository,"Saved");long revision=await Revision(repository);using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>repository.CreateGroupAsync("Ghost",null,revision,stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>repository.SetGroupColorAsync(group,"#123456",revision,stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>repository.MoveGroupAsync(group,AccountGroupMoveDirection.Down,revision,stop.Token));
        Assert.Null(await repository.FindGroupAsync("Ghost",default));Assert.Equal(group,await repository.FindGroupAsync(group.Id,default));Assert.Equal(revision,await Revision(repository));
    }

    [Theory]
    [InlineData("color")]
    [InlineData("name")]
    [InlineData("revision")]
    [InlineData("order")]
    [InlineData("catalog-revision")]
    public async Task CorruptCatalogMetadataFailsWithoutRepairOrProfileChanges(string corruption)
    {
        using var repository=await Open();var profile=await repository.SaveAsync(Profile("Member"),default);var group=(await repository.FindGroupAsync("Raid",default))!;
        await Execute(corruption switch
        {
            "color"=>"PRAGMA ignore_check_constraints=ON; UPDATE account_groups SET color_hex='bad'",
            "name"=>"PRAGMA ignore_check_constraints=ON; UPDATE account_groups SET name=' bad '",
            "revision"=>"PRAGMA ignore_check_constraints=ON; UPDATE account_groups SET revision=0",
            "order"=>"PRAGMA ignore_check_constraints=ON; UPDATE account_groups SET sort_order=-1",
            _=>"PRAGMA ignore_check_constraints=ON; UPDATE account_group_catalog SET revision=0"
        });
        string before=await CatalogState();
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.ReadGroupCatalogAsync(new(),default));Assert.Equal(before,await CatalogState());
        Assert.Equal(profile,await repository.FindAsync(profile.Id,default));
        if(corruption!="catalog-revision")await Assert.ThrowsAsync<InvalidDataException>(()=>repository.FindGroupAsync(group.Id,default));
    }

    [Fact]
    public async Task RevisionOrOrderExhaustionRollsBackRatherThanWrapping()
    {
        using var repository=await Open();var group=await Create(repository,"Saved");long revision=await Revision(repository);
        await Execute("UPDATE account_group_catalog SET revision=9223372036854775807");string before=await CatalogState();
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.CreateGroupAsync("Ghost",null,long.MaxValue,default));Assert.Equal(before,await CatalogState());
        await Execute("UPDATE account_group_catalog SET revision=$revision; UPDATE account_groups SET sort_order=9223372036854775807",("$revision",revision));
        before=await CatalogState();await Assert.ThrowsAsync<InvalidDataException>(()=>repository.CreateGroupAsync("Ghost",null,revision,default));Assert.Equal(before,await CatalogState());
        await Execute("UPDATE account_groups SET sort_order=0,revision=9223372036854775807");var exhausted=(await repository.FindGroupAsync(group.Id,default))!;
        before=await CatalogState();await Assert.ThrowsAsync<InvalidDataException>(()=>repository.SetGroupColorAsync(exhausted,"#123456",revision,default));Assert.Equal(before,await CatalogState());
    }

    [Fact]
    public async Task SchemaFourMigrationSeedsAllNamesBeyondThePickerAndKeepsExactProfileRevisionsAndCleanup()
    {
        await CreateLegacyFour(1005);string before=await ProfileState();
        using var repository=await Open();Assert.Equal(before,await ProfileState());var page=await repository.ReadGroupCatalogAsync(new(PageSize:1000),default);
        Assert.Equal(1007,page.Total);Assert.Equal(1000,page.Items.Count);Assert.NotNull(await repository.FindGroupAsync("Group 1004",default));
        Assert.All(page.Items,item=>{Assert.Null(item.ColorHex);Assert.Equal(1,item.Revision);});
        await using var backup=await Database(DatabasePath+".schema4.bak",true);using var verify=backup.CreateCommand();verify.CommandText="PRAGMA integrity_check";Assert.Equal("ok",await verify.ExecuteScalarAsync());
        verify.CommandText="PRAGMA user_version";Assert.Equal(4L,await verify.ExecuteScalarAsync());verify.CommandText="SELECT count(*) FROM sqlite_master WHERE name='account_groups'";Assert.Equal(0L,await verify.ExecuteScalarAsync());
        Assert.Equal(before,await ProfileState(DatabasePath+".schema4.bak"));Assert.Equal(1,(await repository.ReadCredentialCleanupAsync(default)).Total);Assert.Equal(5L,await SchemaVersion(DatabasePath));
    }

    [Fact]
    public async Task MigrationBackupIncludesTheLastForeignWriterBeforeTheMigrationLease()
    {
        await CreateLegacyFour(2);await using var foreign=await Database();using var writer=foreign.BeginTransaction();using var edit=foreign.CreateCommand();edit.Transaction=writer;
        edit.CommandText="UPDATE profiles SET notes='Committed foreign note',revision=revision+1";await edit.ExecuteNonQueryAsync();
        using var repository=new AccountRepository(Root);var migration=repository.InitializeAsync(default);Assert.False(migration.IsCompleted);writer.Commit();
        await migration.WaitAsync(TimeSpan.FromSeconds(15));Assert.Equal(await ProfileState(),await ProfileState(DatabasePath+".schema4.bak"));
        Assert.All((await repository.ReadAsync(new(),default)).Items,item=>Assert.Equal("Committed foreign note",item.Notes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FutureOrDamagedRollbackCopiesAndForeignInterruptedTemporaryFilesArePreserved(bool damaged)
    {
        await CreateLegacyFour(1);string before=await ProfileState();string backup=DatabasePath+".schema4.bak",temporary=DatabasePath+".schema4.tmp";
        if(damaged)await File.WriteAllTextAsync(backup,"synthetic preserved damaged rollback");
        else{await using var future=await Database(backup);using var version=future.CreateCommand();version.CommandText="PRAGMA user_version=6";await version.ExecuteNonQueryAsync();}
        await File.WriteAllTextAsync(temporary,"synthetic preserved interrupted migration");byte[] preserved=await File.ReadAllBytesAsync(backup);
        using var repository=new AccountRepository(Root);
        if(damaged){var error=await Assert.ThrowsAsync<SqliteException>(()=>repository.InitializeAsync(default));Assert.Equal(26,error.SqliteErrorCode);}
        else await Assert.ThrowsAsync<InvalidDataException>(()=>repository.InitializeAsync(default));
        Assert.Equal(preserved,await File.ReadAllBytesAsync(backup));Assert.Equal("synthetic preserved interrupted migration",await File.ReadAllTextAsync(temporary));
        Assert.Equal(before,await ProfileState());Assert.Equal(4L,await SchemaVersion(DatabasePath));
    }

    [Fact]
    public async Task FutureSchemaIsRejectedByExistingRepositoryReadsAndWritesWithoutReinitializing()
    {
        using var repository=await Open();var group=await Create(repository,"Saved");
        var profile=await repository.SaveAsync(Profile("Preserved profile"),default);
        var removed=await repository.SaveAsync(Profile("Pending cleanup"),default);await repository.DeleteAsync(removed.Id,removed.Revision,default);
        await Execute("PRAGMA user_version=6");byte[] before=await File.ReadAllBytesAsync(DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.FindGroupAsync(group.Id,default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.ReadAsync(new(),default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.CreateGroupAsync("Rejected",null,2,default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.DeleteManyAsync([profile],default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.CompleteCredentialCleanupAsync(removed.Id,default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.InitializeAsync(default));Assert.Equal(before,await File.ReadAllBytesAsync(DatabasePath));
    }

    [Fact]
    public async Task ReadOnlyAccountPageAndCleanupReadsDoNotWaitForAForeignWriteReservation()
    {
        using var repository=await Open();await repository.SaveAsync(Profile("Listed"),default);
        var removed=await repository.SaveAsync(Profile("Pending cleanup"),default);await repository.DeleteAsync(removed.Id,removed.Revision,default);
        // BEGIN IMMEDIATE on a foreign connection holds only the RESERVED lock (nothing written or spilled), so plain readers can still take SHARED.
        await using var foreign=await Database();using var reservation=foreign.BeginTransaction(deferred:false);
        Assert.Equal(1,(await repository.ReadAsync(new(),default).WaitAsync(TimeSpan.FromSeconds(2))).Total);
        Assert.Equal(1,(await repository.ReadCredentialCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(2))).Total);
        reservation.Rollback();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[\"Raid\"]")]
    [InlineData("[\" Crew\"]")]
    public async Task ExistingCatalogDoesNotHideOrRepairCorruptRawMembershipData(string invalid)
    {
        using var repository=await Open();var profile=await repository.SaveAsync(Profile("Member"),default);await Create(repository,"Independent empty");
        var preview=await repository.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename,"Raid","Renamed"),default);
        await Execute("PRAGMA ignore_check_constraints=ON; UPDATE profiles SET additional_groups=$invalid WHERE id=$id",("$invalid",invalid),("$id",profile.Id.ToString("D")));
        string profiles=await ProfileState(),catalog=await CatalogState();
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.ReadGroupsAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.PreviewGroupChangeAsync(preview.Change,default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>repository.ApplyGroupChangeAsync(preview,default));
        Assert.Equal(profiles,await ProfileState());Assert.Equal(catalog,await CatalogState());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptLegacyMembershipRollsBackTheWholeCatalogMigration(bool primary)
    {
        await CreateLegacyFour(2);
        await Execute(primary?"UPDATE profiles SET group_name=' bad '":"UPDATE profiles SET additional_groups='{}'");string before=await ProfileState();
        using var repository=new AccountRepository(Root);await Assert.ThrowsAsync<InvalidDataException>(()=>repository.InitializeAsync(default));
        Assert.Equal(4L,await SchemaVersion(DatabasePath));Assert.Equal(before,await ProfileState());Assert.Equal(before,await ProfileState(DatabasePath+".schema4.bak"));
        await using var db=await Database();using var query=db.CreateCommand();query.CommandText="SELECT count(*) FROM sqlite_master WHERE name IN('account_groups','account_group_catalog')";
        Assert.Equal(0L,await query.ExecuteScalarAsync());
    }

    private async Task CreateLegacyFour(int count)
    {
        await using var db=await Database();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
        command.CommandText="""
            CREATE TABLE profiles(id TEXT PRIMARY KEY,label TEXT NOT NULL,username TEXT NOT NULL,group_name TEXT NOT NULL,priority INTEGER NOT NULL,
                place_id INTEGER,user_id INTEGER,updated_utc TEXT NOT NULL,revision INTEGER NOT NULL,notes TEXT NOT NULL DEFAULT '',is_favorite INTEGER NOT NULL DEFAULT 0,
                additional_groups TEXT NOT NULL DEFAULT '[]');
            CREATE INDEX profiles_group_label ON profiles(group_name,label COLLATE NOCASE);
            CREATE TABLE credential_cleanup(profile_id TEXT PRIMARY KEY); PRAGMA user_version=4;
            """;await command.ExecuteNonQueryAsync();
        command.CommandText="INSERT INTO profiles VALUES($id,$name,'legacy_player',$group,7,42,456,$time,9,'Legacy note',1,'[\"Équipe\",\"Shared\"]')";
        for(int index=0;index<count;index++)
        {
            command.Parameters.Clear();command.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));command.Parameters.AddWithValue("$name","Legacy "+index);
            command.Parameters.AddWithValue("$group","Group "+index.ToString("D4"));command.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToString("O"));await command.ExecuteNonQueryAsync();
        }
        command.Parameters.Clear();command.CommandText="INSERT INTO credential_cleanup VALUES($id)";command.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));await command.ExecuteNonQueryAsync();tx.Commit();
    }
    private async Task Execute(string sql,params(string Name,object Value)[] parameters)
    {await using var db=await Database();using var command=db.CreateCommand();command.CommandText=sql;foreach(var parameter in parameters)command.Parameters.AddWithValue(parameter.Name,parameter.Value);await command.ExecuteNonQueryAsync();}
    private async Task<long> SchemaVersion(string path)
    {await using var db=await Database(path,true);using var query=db.CreateCommand();query.CommandText="PRAGMA user_version";return Convert.ToInt64(await query.ExecuteScalarAsync());}
    private async Task<string> ProfileState(string? path=null)
    {await using var db=await Database(path,path is not null);using var query=db.CreateCommand();query.CommandText="SELECT * FROM profiles ORDER BY id";var rows=new List<object[]>();await using var reader=await query.ExecuteReaderAsync();while(await reader.ReadAsync()){var values=new object[reader.FieldCount];reader.GetValues(values);rows.Add(values);}return JsonSerializer.Serialize(rows);}
    private async Task<string> CatalogState()
    {await using var db=await Database();using var query=db.CreateCommand();query.CommandText="SELECT id,name,color_hex,sort_order,revision,updated_utc FROM account_groups ORDER BY id";var rows=new List<object[]>();await using(var reader=await query.ExecuteReaderAsync())while(await reader.ReadAsync()){var values=new object[reader.FieldCount];reader.GetValues(values);rows.Add(values);}query.CommandText="SELECT revision FROM account_group_catalog";return JsonSerializer.Serialize(rows)+":"+await query.ExecuteScalarAsync();}
    public void Dispose()
    {string path=Path.GetFullPath(Root),temp=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));if(!string.Equals(Path.GetDirectoryName(path),temp,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(path)!=_name)throw new InvalidOperationException("Unexpected synthetic catalog cleanup path.");if(Directory.Exists(path))Directory.Delete(path,true);}
}
