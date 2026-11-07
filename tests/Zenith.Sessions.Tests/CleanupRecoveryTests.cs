using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class CleanupRecoveryTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-cleanup-tests-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task DeletedProfileCannotReattachBeforeCrashRecoveryRemovesItsUnusedProtectedFiles()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default);
        using var vault=new ProtectedCredentialVault(_root);
        var authorization=new AuthorizationRecord(profile.Id,"roblox-oauth",123,1,"synthetic-access","synthetic-refresh",DateTimeOffset.UtcNow.AddMinutes(10),
            Issuer:RobloxOAuthIdentity.Issuer,Subject:"123");
        await vault.SaveAsync(authorization,default); await vault.SaveAsync(authorization with { Generation=2 },default);
        await repository.DeleteAsync(profile.Id,profile.Revision,default);
        Assert.Null(await repository.FindAsync(profile.Id,default)); Assert.NotNull(await vault.ReadAsync(profile.Id,default));
        Assert.Equal(profile.Id,Assert.Single((await repository.ReadCredentialCleanupAsync(default)).ProfileIds));
        await Assert.ThrowsAsync<RevisionConflictException>(()=>repository.SaveAsync(profile with { Revision=0 },default));
        using var reopened=new AccountRepository(_root); await reopened.InitializeAsync(default);
        using var cleanup=new CredentialCleanupService(reopened,vault); Assert.Equal(0,(await cleanup.RunAsync(default)).Remaining);
        Assert.Null(await vault.ReadAsync(profile.Id,default));
        var remaining=Assert.Single(Directory.GetFiles(Path.Combine(_root,"accounts.vault")));
        Assert.Equal(ProtectedCredentialVault.WriterLockFileName,Path.GetFileName(remaining));Assert.Equal(0,new FileInfo(remaining).Length);
        Assert.Equal(0,(await reopened.ReadCredentialCleanupAsync(default)).Total);
    }
    [Fact]
    public async Task CleanupFailureRetainsDurableIntentAndRetryCompletesIt()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var a=await repository.SaveAsync(AccountProfile.Create("A"),default); var b=await repository.SaveAsync(AccountProfile.Create("B"),default);
        await repository.DeleteManyAsync([a,b],default);
        var page=await repository.ReadCredentialCleanupAsync(default); var vault=new FailingCleanupVault { Failure=page.ProfileIds[0] };
        using var service=new CredentialCleanupService(repository,vault);
        var result=await service.RunAsync(default); Assert.Equal(2,result.Remaining); Assert.Equal(nameof(IOException),result.ErrorType);
        Assert.Equal(2,(await repository.ReadCredentialCleanupAsync(default)).Total);
        vault.Failure=null; Assert.Equal(0,(await service.RunAsync(default)).Remaining);
    }
    [Fact]
    public async Task FailedBulkDeleteRollsBackCleanupIntentAsWellAsProfiles()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var a=await repository.SaveAsync(AccountProfile.Create("A"),default); var b=await repository.SaveAsync(AccountProfile.Create("B"),default);
        await Assert.ThrowsAsync<RevisionConflictException>(()=>repository.DeleteManyAsync([a,b with { Revision=99 }],default));
        Assert.Equal(2,(await repository.ReadAsync(new(),default)).Total); Assert.Equal(0,(await repository.ReadCredentialCleanupAsync(default)).Total);
    }
    [Fact]
    public async Task SchemaOneMigrationPreservesProfilesAndCreatesAVerifiedRollbackCopy()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile=await repository.SaveAsync(AccountProfile.Create("Preserved"),default);
        string path=Path.Combine(_root,"accounts.db");
        using(var old=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await old.OpenAsync(); using var command=old.CreateCommand(); command.CommandText="DROP TABLE account_groups; DROP TABLE account_group_catalog; DROP TABLE credential_cleanup; ALTER TABLE profiles DROP COLUMN notes; ALTER TABLE profiles DROP COLUMN is_favorite; ALTER TABLE profiles DROP COLUMN additional_groups; PRAGMA user_version=1;"; await command.ExecuteNonQueryAsync();
        }
        await repository.InitializeAsync(default); Assert.Equal(profile,await repository.FindAsync(profile.Id,default));
        using var backup=new SqliteConnection($"Data Source={path}.schema1.bak;Mode=ReadOnly;Pooling=False"); await backup.OpenAsync();
        using var verify=backup.CreateCommand(); verify.CommandText="PRAGMA user_version"; Assert.Equal(1L,await verify.ExecuteScalarAsync());
        verify.CommandText="SELECT label FROM profiles"; Assert.Equal("Preserved",await verify.ExecuteScalarAsync());
        Assert.Equal(0,(await repository.ReadCredentialCleanupAsync(default)).Total);
    }
    private sealed class FailingCleanupVault : IAccountCredentialVault
    {
        public Guid? Failure;
        public Task<AuthorizationRecord?> ReadAsync(Guid id,CancellationToken ct)=>throw new NotSupportedException("Synthetic cleanup-only test");
        public Task SaveAsync(AuthorizationRecord record,CancellationToken ct)=>throw new NotSupportedException("Synthetic cleanup-only test");
        public Task DeleteAsync(Guid id,CancellationToken ct) { ct.ThrowIfCancellationRequested(); if(id==Failure) throw new IOException("Synthetic storage failure"); return Task.CompletedTask; }
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-cleanup-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
