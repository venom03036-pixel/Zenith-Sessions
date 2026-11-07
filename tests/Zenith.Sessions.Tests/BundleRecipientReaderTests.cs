using System.IO;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class BundleRecipientReaderTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-bundle-reader-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task VerifiedSavedIdentityOverridesEnteredMetadataButNeverBecomesCurrentAuthorization()
    {
        using var profiles=new AccountRepository(_root);await profiles.InitializeAsync(default);
        var profile=await profiles.SaveAsync(AccountProfile.Create("Alias") with{Username="EnteredWrong",RobloxUserId=999},default);
        var identity=new IdentityReader();var reader=new BundleRecipientReader(profiles,identity);
        var row=Assert.Single(await reader.ReadAsync([profile.Id],default));
        Assert.Equal("VerifiedName",row.Label);Assert.Equal(123,row.KnownUserId);Assert.Equal(new LaunchAccountBinding(123,7),row.Binding);
        Assert.Contains("Last-known",row.Identity);Assert.Contains("checked again",row.SignInState);
        identity.ReturnNone=true;row=Assert.Single(await reader.ReadAsync([profile.Id],default));
        Assert.Null(row.KnownUserId);Assert.Null(row.Binding);Assert.Equal("Alias",row.Label);
    }
    [Fact]
    public async Task MissingProfilesAndExpiredGrantsRemainExplicitAndMetadataReadsAreBatched()
    {
        using var profiles=new AccountRepository(_root);await profiles.InitializeAsync(default);
        var first=await profiles.SaveAsync(AccountProfile.Create("Saved"),default);
        var ids=new[]{first.Id}.Concat(Enumerable.Range(0,400).Select(_=>Guid.NewGuid())).ToArray();
        var identity=new IdentityReader{Expired=true};var rows=await new BundleRecipientReader(profiles,identity).ReadAsync(ids,default);
        Assert.Equal(401,rows.Count);Assert.False(rows[0].IsMissing);Assert.Null(rows[0].Binding);Assert.NotNull(rows[0].KnownUserId);
        Assert.All(rows.Skip(1),row=>{Assert.True(row.IsMissing);Assert.Null(row.Binding);});
        Assert.Equal(new[]{200,200,1},identity.Batches);
    }
    [Fact]
    public async Task WrongSummaryOwnershipRejectsTheLookupInsteadOfAttachingAnotherAccount()
    {
        using var profiles=new AccountRepository(_root);await profiles.InitializeAsync(default);
        var identity=new IdentityReader{WrongOwner=true};var reader=new BundleRecipientReader(profiles,identity);
        await Assert.ThrowsAsync<InvalidDataException>(()=>reader.ReadAsync([Guid.NewGuid()],default));
    }
    [Fact]
    public async Task DuplicateOrUnboundedIdsAndPreCancellationDoNotReadIdentities()
    {
        using var profiles=new AccountRepository(_root);var identity=new IdentityReader();var reader=new BundleRecipientReader(profiles,identity);var id=Guid.NewGuid();
        Assert.Throws<ArgumentException>(()=>{_=reader.ReadAsync([id,id],default);});
        Assert.Throws<ArgumentException>(()=>{_=reader.ReadAsync(Enumerable.Range(0,1001).Select(_=>Guid.NewGuid()).ToArray(),default);});
        using var stop=new CancellationTokenSource();stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reader.ReadAsync([id],stop.Token));
        Assert.Empty(identity.Batches);
    }
    private sealed class IdentityReader:IAuthorizationMetadataReader
    {
        public bool Expired,ReturnNone,WrongOwner;public List<int> Batches=[];
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> ids,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Batches.Add(ids.Count);var now=DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyList<AuthorizationMetadata>>(ReturnNone?[]:ids.Select(id=>new AuthorizationMetadata(WrongOwner?Guid.NewGuid():id,"synthetic-ref",RobloxSessionIdentity.Provider,
                RobloxSessionIdentity.Issuer,"123",123,"VerifiedName",7,AuthorizationHealth.Linked,Expired?now.AddMinutes(-1):now.AddHours(1),now)).ToArray());
        }
        public Task<AuthorizationMetadata?> GetSummaryAsync(Guid id,CancellationToken ct)=>throw new InvalidOperationException("Only bounded explicit batches expected.");
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string p,string i,string s,CancellationToken ct)=>throw new InvalidOperationException("No inventory or secret lookup expected.");
    }
    public void Dispose()
    {
        string path=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(path)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(path).StartsWith("zenith-bundle-reader-",StringComparison.Ordinal))throw new InvalidOperationException();
        if(Directory.Exists(path))Directory.Delete(path,true);
    }
}
