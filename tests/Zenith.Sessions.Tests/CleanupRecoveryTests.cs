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
        var result=await service.RunAsync(default); Assert.Equal(1,result.Remaining); Assert.Equal(nameof(IOException),result.ErrorType);
        Assert.Equal(page.ProfileIds[0],Assert.Single((await repository.ReadCredentialCleanupAsync(default)).ProfileIds));
        vault.Failure=null; Assert.Equal(0,(await service.RunAsync(default)).Remaining);
    }
    [Fact]
    public async Task CorruptVaultIndexForADeletedProfileIsReportedAndRetainedNotThrown()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile=await DeletedProfileWithDamagedPointerAsync(repository,_root);
        using var vault=new ProtectedCredentialVault(_root);
        using var cleanup=new CredentialCleanupService(repository,vault);
        var result=await cleanup.RunAsync(default);
        // Only a failure confined to this record's files may blame a removed profile's file.
        Assert.Equal(1,result.Remaining); Assert.Equal(nameof(InvalidDataException),result.ErrorType); Assert.True(result.RecordDamaged);
        Assert.False(result.StoreDamaged);
        Assert.Equal(profile.Id,Assert.Single((await repository.ReadCredentialCleanupAsync(default)).ProfileIds));
        string record=Path.Combine(_root,"accounts.vault",profile.Id.ToString("N")+".dpapi");
        Assert.True(File.Exists(record)); Assert.True(File.Exists(record+".identity"));
    }
    [Fact]
    public async Task DamagedVaultCoordinationFileEndsTheRunWithoutBlamingARemovedProfile()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var a=await repository.SaveAsync(AccountProfile.Create("A"),default); var b=await repository.SaveAsync(AccountProfile.Create("B"),default);
        await repository.DeleteManyAsync([a,b],default);
        await DamageVaultCoordinationFileAsync(_root);
        using var inner=new ProtectedCredentialVault(_root); var vault=new CountingCleanupVault(inner);
        using var service=new CredentialCleanupService(repository,vault); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result=await service.RunAsync(timeout.Token);
        // The store itself is unusable, so every record would fail the same way: one attempt, and the store's failure is reported.
        Assert.Equal(2,result.Remaining); Assert.Equal(nameof(InvalidDataException),result.ErrorType); Assert.False(result.RecordDamaged);
        Assert.True(result.StoreDamaged);
        Assert.Single(vault.Calls);
        Assert.Equal(2,(await repository.ReadCredentialCleanupAsync(default)).Total);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoreWideDamageLaterInTheRunStillReportsTheFirstFailure(bool firstIsRecordDamage)
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var a=await repository.SaveAsync(AccountProfile.Create("A"),default); var b=await repository.SaveAsync(AccountProfile.Create("B"),default);
        var c=await repository.SaveAsync(AccountProfile.Create("C"),default);
        await repository.DeleteManyAsync([a,b,c],default);
        Exception first=firstIsRecordDamage?AuthorizationRecordDamage.Mark(new InvalidDataException("Synthetic record damage")):new IOException("Synthetic storage failure");
        var vault=new ScriptedCleanupVault(first,new InvalidDataException("Synthetic store damage"));
        using var service=new CredentialCleanupService(repository,vault); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result=await service.RunAsync(timeout.Token);
        // The store-wide failure still ends the run, but the first failure is the one reported, as on the busy path.
        Assert.Equal(3,result.Remaining); Assert.Equal(first.GetType().Name,result.ErrorType);
        Assert.Equal(firstIsRecordDamage,result.RecordDamaged); Assert.True(result.StoreDamaged);
        Assert.Equal(2,vault.Calls.Count);
    }
    /// <summary>The n-th delete throws the n-th scripted failure; a null entry, or a call past the script, succeeds.</summary>
    private sealed class ScriptedCleanupVault(params Exception?[] failures) : IAccountCredentialVault
    {
        public readonly List<Guid> Calls=[];
        public Task<AuthorizationRecord?> ReadAsync(Guid id,CancellationToken ct)=>throw new NotSupportedException("Synthetic cleanup-only test");
        public Task SaveAsync(AuthorizationRecord record,CancellationToken ct)=>throw new NotSupportedException("Synthetic cleanup-only test");
        public Task DeleteAsync(Guid id,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add(id);
            return Calls.Count<=failures.Length && failures[Calls.Count-1] is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }
    }
    /// <summary>Writes content into the vault's coordination file, which must be empty, so every vault operation is refused.</summary>
    internal static Task DamageVaultCoordinationFileAsync(string root)
    {
        string folder=Path.Combine(root,"accounts.vault"); Directory.CreateDirectory(folder);
        return File.WriteAllTextAsync(Path.Combine(folder,ProtectedCredentialVault.WriterLockFileName),"synthetic damage");
    }
    private sealed class CountingCleanupVault(IAccountCredentialVault inner) : IAccountCredentialVault
    {
        public readonly List<Guid> Calls=[];
        public Task<AuthorizationRecord?> ReadAsync(Guid id,CancellationToken ct)=>throw new NotSupportedException("Synthetic cleanup-only test");
        public Task SaveAsync(AuthorizationRecord record,CancellationToken ct)=>throw new NotSupportedException("Synthetic cleanup-only test");
        public Task DeleteAsync(Guid id,CancellationToken ct) { Calls.Add(id); return inner.DeleteAsync(id,ct); }
    }
    /// <summary>Deletes a profile whose synthetic protected record remains, then damages its vault pointer file.</summary>
    internal static async Task<AccountProfile> DeletedProfileWithDamagedPointerAsync(AccountRepository repository,string root)
    {
        var profile=await repository.SaveAsync(AccountProfile.Create("A"),default);
        using(var vault=new ProtectedCredentialVault(root))
            await vault.SaveAsync(new AuthorizationRecord(profile.Id,"roblox-oauth",123,1,"synthetic-access","synthetic-refresh",DateTimeOffset.UtcNow.AddMinutes(10),
                Issuer:RobloxOAuthIdentity.Issuer,Subject:"123"),default);
        await repository.DeleteAsync(profile.Id,profile.Revision,default);
        await File.WriteAllTextAsync(Path.Combine(root,"accounts.vault",profile.Id.ToString("N")+".dpapi.identity"),"{");
        return profile;
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
            // Every column a later schema added is dropped too (schema 6 added extra_client_arguments), so this is a real schema 1 library.
            await old.OpenAsync(); using var command=old.CreateCommand(); command.CommandText="DROP TABLE account_groups; DROP TABLE account_group_catalog; DROP TABLE credential_cleanup; ALTER TABLE profiles DROP COLUMN notes; ALTER TABLE profiles DROP COLUMN is_favorite; ALTER TABLE profiles DROP COLUMN additional_groups; ALTER TABLE profiles DROP COLUMN extra_client_arguments; PRAGMA user_version=1;"; await command.ExecuteNonQueryAsync();
        }
        await repository.InitializeAsync(default); Assert.Equal(profile,await repository.FindAsync(profile.Id,default));
        using var backup=new SqliteConnection($"Data Source={path}.schema1.bak;Mode=ReadOnly;Pooling=False"); await backup.OpenAsync();
        using var verify=backup.CreateCommand(); verify.CommandText="PRAGMA user_version"; Assert.Equal(1L,await verify.ExecuteScalarAsync());
        verify.CommandText="SELECT label FROM profiles"; Assert.Equal("Preserved",await verify.ExecuteScalarAsync());
        Assert.Equal(0,(await repository.ReadCredentialCleanupAsync(default)).Total);
    }
    [Fact]
    public async Task CleanupFailureOnOneRecordStillRemovesEveryOtherPendingRecord()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var a=await repository.SaveAsync(AccountProfile.Create("A"),default); var b=await repository.SaveAsync(AccountProfile.Create("B"),default);
        var c=await repository.SaveAsync(AccountProfile.Create("C"),default);
        await repository.DeleteManyAsync([a,b,c],default);
        var page=await repository.ReadCredentialCleanupAsync(default); var vault=new FailingCleanupVault { Failure=page.ProfileIds[0] };
        using var service=new CredentialCleanupService(repository,vault); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result=await service.RunAsync(timeout.Token);
        Assert.Equal(1,result.Remaining); Assert.Equal(nameof(IOException),result.ErrorType);
        Assert.Equal(page.ProfileIds[0],Assert.Single((await repository.ReadCredentialCleanupAsync(default)).ProfileIds));
        Assert.Equal(page.ProfileIds.Order(),vault.Calls.Order());
    }
    [Fact]
    public async Task CleanupThatFailsForEveryRecordTriesEachOnceThenReturns()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var a=await repository.SaveAsync(AccountProfile.Create("A"),default); var b=await repository.SaveAsync(AccountProfile.Create("B"),default);
        var c=await repository.SaveAsync(AccountProfile.Create("C"),default);
        await repository.DeleteManyAsync([a,b,c],default);
        var page=await repository.ReadCredentialCleanupAsync(default); var vault=new FailingCleanupVault { FailEvery=true };
        using var service=new CredentialCleanupService(repository,vault); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result=await service.RunAsync(timeout.Token);
        Assert.Equal(3,result.Remaining); Assert.Equal(nameof(IOException),result.ErrorType);
        Assert.Equal(page.ProfileIds.Order(),vault.Calls.Order());
        Assert.Equal(3,(await repository.ReadCredentialCleanupAsync(default)).Total);
    }
    [Fact]
    public async Task BusyVaultEndsTheCleanupRunInsteadOfWaitingOnEveryRecord()
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        var a=await repository.SaveAsync(AccountProfile.Create("A"),default); var b=await repository.SaveAsync(AccountProfile.Create("B"),default);
        await repository.DeleteManyAsync([a,b],default);
        var vault=new FailingCleanupVault { Busy=true };
        using var service=new CredentialCleanupService(repository,vault); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result=await service.RunAsync(timeout.Token);
        Assert.Equal(2,result.Remaining); Assert.Equal(nameof(AuthorizationVaultBusyException),result.ErrorType); Assert.False(result.StoreDamaged);
        Assert.Single(vault.Calls);
        vault.Busy=false; Assert.Equal(0,(await service.RunAsync(timeout.Token)).Remaining);
    }
    private sealed class FailingCleanupVault : IAccountCredentialVault
    {
        public Guid? Failure; public bool FailEvery; public bool Busy;
        public readonly List<Guid> Calls=[];
        public Task<AuthorizationRecord?> ReadAsync(Guid id,CancellationToken ct)=>throw new NotSupportedException("Synthetic cleanup-only test");
        public Task SaveAsync(AuthorizationRecord record,CancellationToken ct)=>throw new NotSupportedException("Synthetic cleanup-only test");
        public Task DeleteAsync(Guid id,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add(id);
            if(Busy) throw new AuthorizationVaultBusyException();
            if(FailEvery || id==Failure) throw new IOException("Synthetic storage failure");
            return Task.CompletedTask;
        }
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-cleanup-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
