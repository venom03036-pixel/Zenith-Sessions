using System.IO;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class LaunchAuthorizationValidatorTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-launch-auth-tests-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task LinkedVaultIdentityWorksWithoutCopyingIdentityIntoAnUnrelatedMetadataTransaction()
    {
        using var profiles=new AccountRepository(_root);await profiles.InitializeAsync(default);
        var profile=await profiles.SaveAsync(AccountProfile.Create("Optional alias") with { PreferredPlaceId=6564161046 },default);
        var vault=new SelectedVault(Grant(profile.Id));var validator=new LaunchAuthorizationValidator(profiles,vault);
        var intent=new LaunchIntent(Guid.NewGuid(),profile.Id,profile.Revision,123,6564161046,0,DateTimeOffset.UtcNow,7);
        Assert.Null(profile.RobloxUserId);Assert.True(await validator.ValidateAsync(intent,default));
        Assert.Equal(new[]{profile.Id},vault.Reads);
        Assert.False(await validator.ValidateAsync(intent with { AuthorizationGeneration=6 },default));
        Assert.False(await validator.ValidateAsync(intent with { UserId=456 },default));
        Assert.False(await validator.ValidateAsync(intent with { ProfileRevision=profile.Revision+1 },default));
        Assert.False(await validator.ValidateAsync(intent with { PlaceId=1 },default));
    }
    [Fact]
    public async Task MissingExpiredOrInterruptedAuthorizationCannotBeGrantedByTheCachedQueueBinding()
    {
        using var profiles=new AccountRepository(_root);await profiles.InitializeAsync(default);
        var profile=await profiles.SaveAsync(AccountProfile.Create("Synthetic") with { PreferredPlaceId=6564161046 },default);
        var vault=new SelectedVault(null);var validator=new LaunchAuthorizationValidator(profiles,vault);
        var intent=new LaunchIntent(Guid.NewGuid(),profile.Id,profile.Revision,123,6564161046,0,DateTimeOffset.UtcNow,7);
        Assert.False(await validator.ValidateAsync(intent,default));
        vault.Record=Grant(profile.Id) with { ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(-1) };Assert.False(await validator.ValidateAsync(intent,default));
        vault.Record=Grant(profile.Id) with { Health=AuthorizationHealth.RefreshPending };Assert.False(await validator.ValidateAsync(intent,default));
    }
    [Fact]
    public async Task ExplicitDestinationOverridesPreferenceButKeepsSelectedGrantAndProfileRevisionChecks()
    {
        using var profiles=new AccountRepository(_root);await profiles.InitializeAsync(default);
        var profile=await profiles.SaveAsync(AccountProfile.Create("Synthetic") with { PreferredPlaceId=6564161046 },default);
        var vault=new SelectedVault(Grant(profile.Id));var validator=new LaunchAuthorizationValidator(profiles,vault);
        var intent=new LaunchIntent(Guid.NewGuid(),profile.Id,profile.Revision,123,42,0,DateTimeOffset.UtcNow,7,
            "12345678-1234-4234-8234-123456789abc",RobloxServerKind.Public,true);
        Assert.True(await validator.ValidateAsync(intent,default));Assert.Equal(new[]{profile.Id},vault.Reads);
        Assert.False(await validator.ValidateAsync(intent with { HasExplicitDestination=false },default));
        Assert.False(await validator.ValidateAsync(intent with { ProfileRevision=profile.Revision+1 },default));
        vault.Record=Grant(Guid.NewGuid());Assert.False(await validator.ValidateAsync(intent,default));
        Assert.Equal(6564161046,(await profiles.FindAsync(profile.Id,default))!.PreferredPlaceId);
    }
    [Fact]
    public async Task InvalidDestinationMetadataIsRejectedBeforeOpeningAnyCredential()
    {
        using var profiles=new AccountRepository(_root);await profiles.InitializeAsync(default);
        var profile=await profiles.SaveAsync(AccountProfile.Create("Synthetic"),default);
        var vault=new SelectedVault(Grant(profile.Id));var validator=new LaunchAuthorizationValidator(profiles,vault);
        var intent=new LaunchIntent(Guid.NewGuid(),profile.Id,profile.Revision,123,42,0,DateTimeOffset.UtcNow,7,
            "not-a-job",RobloxServerKind.Unknown,true);
        Assert.False(await validator.ValidateAsync(intent,default));
        Assert.False(await validator.ValidateAsync(intent with { RequestedJobId=null,RequestedServerKind=RobloxServerKind.Public },default));
        Assert.False(await validator.ValidateAsync(intent with { RequestedJobId=null,PlaceId=0 },default));
        Assert.Empty(vault.Reads);
    }
    private static AuthorizationRecord Grant(Guid id)=>new(id,"roblox-oauth",123,7,"","synthetic-refresh",DateTimeOffset.UtcNow.AddMinutes(5),"verified-name",
        Issuer:RobloxOAuthIdentity.Issuer,Subject:"123");
    private sealed class SelectedVault(AuthorizationRecord? record) : IAccountCredentialVault
    {
        public AuthorizationRecord? Record=record;
        public List<Guid> Reads {get;}=[];
        public Task<AuthorizationRecord?> ReadAsync(Guid id,CancellationToken ct) {ct.ThrowIfCancellationRequested();Reads.Add(id);return Task.FromResult(Record);}
        public Task SaveAsync(AuthorizationRecord value,CancellationToken ct)=>throw new NotSupportedException();
        public Task DeleteAsync(Guid id,CancellationToken ct)=>throw new NotSupportedException();
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-launch-auth-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
