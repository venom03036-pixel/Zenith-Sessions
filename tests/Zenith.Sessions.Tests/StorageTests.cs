using System.Security.Cryptography;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _name = "zenith-sessions-tests-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private async Task<AccountRepository> Repository()
    { var repository = new AccountRepository(Root); await repository.InitializeAsync(default); return repository; }
    [Fact]
    public async Task ProfilesPersistAndStaleEditsCannotOverwriteNewerData()
    {
        var repository = await Repository();
        var first = await repository.SaveAsync(AccountProfile.Create("  Account A  ") with { Username="player_A", PreferredPlaceId=6564161046 }, default);
        var updated = await repository.SaveAsync(first with { Label="Updated" }, default);
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.SaveAsync(first with { Label="Stale" }, default));
        var reopened = await Repository(); var page = await reopened.ReadAsync(new(), default);
        Assert.Equal(updated, Assert.Single(page.Items)); Assert.Equal("Not linked", page.Items[0].IdentityState);
    }
    [Fact]
    public async Task SearchUsesLiteralParametersAndGroupingDoesNotLeakRows()
    {
        var repository = await Repository();
        await repository.CreateManyAsync([AccountProfile.Create("A ' OR 1=1 --") with { Group="A" }, AccountProfile.Create("Other") with { Group="B" }], default);
        Assert.Single((await repository.ReadAsync(new("' OR 1=1 --"), default)).Items);
        Assert.Equal("Other", Assert.Single((await repository.ReadAsync(new(Group:"B"), default)).Items).Label);
        Assert.Equal(0, (await repository.ReadAsync(new("%"), default)).Total);
    }
    [Fact]
    public async Task FailedBatchInsertRollsBackAllNewRows()
    {
        var repository = await Repository(); var existing = await repository.SaveAsync(AccountProfile.Create("Existing"), default);
        await Assert.ThrowsAsync<SqliteException>(() => repository.CreateManyAsync([AccountProfile.Create("Should roll back"), existing with { Revision=0 }], default));
        Assert.Equal("Existing", Assert.Single((await repository.ReadAsync(new(), default)).Items).Label);
    }
    [Fact]
    public async Task FailedBulkDeletePreservesAllProfiles()
    {
        var repository = await Repository();
        var a = await repository.SaveAsync(AccountProfile.Create("A"), default); var b = await repository.SaveAsync(AccountProfile.Create("B"), default);
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.DeleteManyAsync([a,b with { Revision=999 }], default));
        Assert.Equal(2, (await repository.ReadAsync(new(), default)).Total);
        await repository.DeleteManyAsync([a,b], default); Assert.Equal(0, (await repository.ReadAsync(new(), default)).Total);
    }
    [Fact]
    public async Task TenThousandProfilesArePagedWithoutLoadingTheWholeLibrary()
    {
        var repository = await Repository();
        await repository.CreateManyAsync(Enumerable.Range(0,10000).Select(i => AccountProfile.Create($"Account {i:00000}") with { Group=i%2==0?"Even":"Odd" }).ToArray(), default);
        var page = await repository.ReadAsync(new(Page:99), default);
        Assert.Equal(10000,page.Total); Assert.Equal(100,page.Items.Count); Assert.Equal("Account 09900",page.Items[0].Label);
        var filtered=await repository.ReadAsync(new(Group:"Even",Page:49),default); Assert.Equal(5000,filtered.Total); Assert.Equal(100,filtered.Items.Count);
    }
    [Fact]
    public async Task FutureSchemaIsRejectedWithoutRewritingTheLibrary()
    {
        await Repository(); using var db = new SqliteConnection($"Data Source={Path.Combine(Root,"accounts.db")};Pooling=False"); await db.OpenAsync();
        using var command=db.CreateCommand(); command.CommandText="PRAGMA user_version=99"; await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new AccountRepository(Root).InitializeAsync(default));
        command.CommandText="PRAGMA user_version"; Assert.Equal(99L,await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task CancelledBatchDoesNotCreateProfiles()
    {
        var repository=await Repository(); using var stop=new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.CreateManyAsync([AccountProfile.Create("Never committed")],stop.Token));
        Assert.Equal(0,(await repository.ReadAsync(new(),default)).Total);
    }
    [Fact]
    public async Task SettingsRoundTripAndCorruptionUseTheLastValidBackup()
    {
        Directory.CreateDirectory(Root); var store=new SettingsStore(Root); var initial=await store.LoadAsync(default);
        await store.SaveAsync(initial.Settings with { Theme="amber-night" },default);
        await File.WriteAllTextAsync(store.Path,"broken JSON"); var recovered=await store.LoadAsync(default);
        Assert.Equal("graphite-mint",recovered.Settings.Theme); Assert.NotNull(recovered.Notice);
        Assert.Equal("broken JSON",await File.ReadAllTextAsync(store.Path));
        await store.SaveAsync(recovered.Settings,default); Assert.True(File.Exists(store.Path+".damaged"));
    }
    [Fact]
    public async Task InvalidOrCancelledSettingsCannotReplaceValidPreferences()
    {
        Directory.CreateDirectory(Root); var store=new SettingsStore(Root); await store.LoadAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(OAuthClientId:"not-a-client-id"),default));
        using var stop=new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(Theme:"amber-night"),stop.Token));
        Assert.Equal("graphite-mint",(await store.LoadAsync(default)).Settings.Theme);
    }
    [Fact]
    public async Task DpapiRecordsAreEncryptedBoundToProfileAndRejectOldGenerations()
    {
        using var vault=new ProtectedCredentialVault(Root); var id=Guid.NewGuid();
        var record=new AuthorizationRecord(id,"roblox-oauth",123,1,"synthetic-access-secret","synthetic-refresh-secret",DateTimeOffset.UtcNow.AddMinutes(15),
            Issuer:RobloxOAuthIdentity.Issuer,Subject:"123");
        await vault.SaveAsync(record,default); Assert.Equal(record with { AccessToken="" },await vault.ReadAsync(id,default));
        string path=Path.Combine(Root,"accounts.vault",id.ToString("N")+".dpapi");
        Assert.DoesNotContain("synthetic-access-secret",Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(record,default));
        var other=Guid.NewGuid(); File.Copy(path,Path.Combine(Root,"accounts.vault",other.ToString("N")+".dpapi"));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync(other,default));
        await vault.SaveAsync(record with { Generation=2 },default); Assert.False(File.Exists(path+".bak"));
        await File.WriteAllBytesAsync(path,[1,2,3]);
        await Assert.ThrowsAsync<CryptographicException>(() => vault.ReadAsync(id,default));
        await Assert.ThrowsAsync<CryptographicException>(() => vault.SaveAsync(record with { Generation=3 },default));
        Assert.Equal(new byte[]{1,2,3},await File.ReadAllBytesAsync(path));
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(Root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full)!=_name)
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full,recursive:true);
    }
}
