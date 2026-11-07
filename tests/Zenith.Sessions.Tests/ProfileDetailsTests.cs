using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ProfileDetailsTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-profile-tests-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task NotesAndFavoritesPersistSearchLiterallyAndRetainOptimisticRevisions()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var favorite=await repository.SaveAsync(AccountProfile.Create("Zed") with { Notes="Line one\nShared group ' 100%",IsFavorite=true },default);
        await repository.SaveAsync(AccountProfile.Create("Alpha"),default);
        Assert.Equal(favorite,await repository.FindAsync(favorite.Id,default));
        Assert.Equal(favorite.Id,(await repository.ReadAsync(new(Order:AccountOrder.FavoritesFirst),default)).Items[0].Id);
        Assert.Equal(favorite.Id,Assert.Single((await repository.ReadAsync(new("100%"),default)).Items).Id);
        var edited=await repository.SaveAsync(favorite with { Notes="Changed",IsFavorite=false },default);
        await Assert.ThrowsAsync<RevisionConflictException>(()=>repository.SaveAsync(favorite,default));
        Assert.Equal(edited,await repository.FindAsync(favorite.Id,default));
    }
    [Fact]
    public async Task SchemaTwoMigrationPreservesPendingCredentialCleanupAndVerifiesBackup()
    {
        Directory.CreateDirectory(_root); string path=Path.Combine(_root,"accounts.db"); var id=Guid.NewGuid(); var pending=Guid.NewGuid();
        using(var db=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync(); using var command=db.CreateCommand();
            command.CommandText="""
                CREATE TABLE profiles(id TEXT PRIMARY KEY,label TEXT NOT NULL,username TEXT NOT NULL,group_name TEXT NOT NULL,
                priority INTEGER NOT NULL,place_id INTEGER,user_id INTEGER,updated_utc TEXT NOT NULL,revision INTEGER NOT NULL);
                CREATE INDEX profiles_group_label ON profiles(group_name,label COLLATE NOCASE);
                CREATE TABLE credential_cleanup(profile_id TEXT PRIMARY KEY);
                INSERT INTO profiles VALUES($id,'Previous','Tester','General',10,6564161046,NULL,$time,7);
                INSERT INTO credential_cleanup VALUES($pending);
                PRAGMA user_version=2;
                """;
            command.Parameters.AddWithValue("$id",id.ToString("D")); command.Parameters.AddWithValue("$pending",pending.ToString("D"));
            command.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync();
        }
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile=await repository.FindAsync(id,default); Assert.NotNull(profile); Assert.Equal(7,profile.Revision);
        Assert.Equal("",profile.Notes); Assert.False(profile.IsFavorite);
        Assert.Equal(pending,Assert.Single((await repository.ReadCredentialCleanupAsync(default)).ProfileIds));
        using var backup=new SqliteConnection($"Data Source={path}.schema2.bak;Mode=ReadOnly;Pooling=False"); await backup.OpenAsync();
        using var verify=backup.CreateCommand(); verify.CommandText="PRAGMA integrity_check"; Assert.Equal("ok",await verify.ExecuteScalarAsync());
        verify.CommandText="PRAGMA user_version"; Assert.Equal(2L,await verify.ExecuteScalarAsync());
        verify.CommandText="SELECT count(*) FROM credential_cleanup"; Assert.Equal(1L,await verify.ExecuteScalarAsync());
        await repository.InitializeAsync(default); Assert.Equal(profile,await repository.FindAsync(id,default));
    }
    [Theory]
    [InlineData("bad\0notes")]
    [InlineData("bad\u001bnotes")]
    public void ControlCharactersCannotEnterNotes(string notes)=>Assert.Throws<ArgumentException>(()=>(AccountProfile.Create("Test") with { Notes=notes }).Validated());
    [Fact]
    public async Task InvalidNotesCannotPartiallyCommitABatch()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        await Assert.ThrowsAsync<ArgumentException>(()=>repository.CreateManyAsync([AccountProfile.Create("Valid"),AccountProfile.Create("Oversized") with { Notes=new string('x',4001) }],default));
        Assert.Equal(0,(await repository.ReadAsync(new(),default)).Total);
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-profile-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
