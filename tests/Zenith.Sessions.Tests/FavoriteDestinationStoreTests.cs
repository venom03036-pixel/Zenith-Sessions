using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class FavoriteDestinationStoreTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-favorites-"+Guid.NewGuid().ToString("N"));
    private static FavoriteDestination Draft(string name="Favorite",long place=123,string? job=null)=>new(Guid.NewGuid(),name,place,job);
    private FavoriteDestinationsStore Store() {Directory.CreateDirectory(_root);return new(_root);}

    [Fact]
    public void UnicodeNamesAndInt64DestinationsNormalizeWithoutClaimingPublicAccess()
    {
        var draft=Draft("  友達 👑 — Weekend! (2) / A&B  ",long.MaxValue,"ABCDEFAB-1234-4234-8234-123456789ABC").Validated();
        Assert.Equal("友達 👑 — Weekend! (2) / A&B",draft.Name);Assert.Equal("abcdefab-1234-4234-8234-123456789abc",draft.JobId);
        Assert.Equal(long.MaxValue,draft.RequestedDestination.PlaceId);Assert.Equal(RobloxServerKind.Unknown,draft.RequestedDestination.ServerKind);
        Assert.DoesNotContain(draft.Name,draft.ToString());Assert.DoesNotContain("authorized",draft.ToString());
        Assert.Throws<ArgumentException>(()=>(draft with {Id=Guid.Empty}).Validated());
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name\nsecond line")]
    [InlineData("name\0hidden")]
    [InlineData("name\u2028second line")]
    [InlineData("https://www.roblox.com/games/123?privateServerLinkCode=secret-marker")]
    [InlineData("roblox://placeId=123")]
    [InlineData("privateServerLinkCode=secret-marker")]
    [InlineData("accessCode=secret-marker")]
    [InlineData("Bearer secret-marker")]
    [InlineData(".ROBLOSECURITY secret-marker")]
    public void NamesCannotBecomeRawLinkCodeOrCredentialStorage(string name)
    {
        var error=Assert.Throws<ArgumentException>(()=>Draft(name).Validated());Assert.DoesNotContain("secret-marker",error.ToString());
    }
    [Fact]
    public void InvalidIdentityRevisionNameAndServerValuesAreRejected()
    {
        foreach(var draft in new[] {Draft(place:0),Draft(place:-1),Draft(new string('x',81)),Draft("bad\ud800"),Draft(job:"not-a-job"),
            Draft(job:Guid.Empty.ToString("D")),Draft() with {Revision=-1},Draft() with {Revision=1},Draft() with {ChangeId=Guid.NewGuid()}})
            Assert.Throws<ArgumentException>(()=>draft.Validated());
    }
    [Fact]
    public async Task InitialEmptyFileIsVersionedAndReturnedCollectionsCannotMutateTheStore()
    {
        using var store=Store();var initial=await store.LoadAsync(default);
        Assert.Equal(1,initial.Revision);Assert.Empty(initial.Destinations);Assert.Null(initial.Notice);Assert.True(File.Exists(store.Path));
        var items=Assert.IsAssignableFrom<IList<FavoriteDestination>>(initial.Destinations);
        Assert.Throws<NotSupportedException>(()=>items.Add(Draft()));
        Assert.Equal(1,(await store.LoadAsync(default)).Revision);Assert.False(File.Exists(store.Path+".bak"));
        using var json=JsonDocument.Parse(await File.ReadAllBytesAsync(store.Path));
        Assert.Equal(1,json.RootElement.GetProperty("Version").GetInt32());Assert.Empty(json.RootElement.GetProperty("Items").EnumerateArray());
    }
    [Fact]
    public async Task SaveRenameDeleteAndReopenRetainStableIdentityAndCheckStaleRevisions()
    {
        using var store=Store();var first=await store.SaveAsync(Draft("Game",long.MaxValue),default);
        Assert.Equal(1,first.Revision);Assert.NotEqual(Guid.Empty,first.ChangeId);
        using(var json=JsonDocument.Parse(await File.ReadAllBytesAsync(store.Path)))
        {
            var item=Assert.Single(json.RootElement.GetProperty("Items").EnumerateArray());
            Assert.Equal(new[] {"ChangeId","Id","JobId","Name","PlaceId","Revision"},item.EnumerateObject().Select(property=>property.Name).Order());
        }
        var renamed=await store.SaveAsync(first with {Name="New name"},default);
        Assert.Equal(first.Id,renamed.Id);Assert.Equal(2,renamed.Revision);Assert.NotEqual(first.ChangeId,renamed.ChangeId);
        await Assert.ThrowsAsync<FavoriteDestinationConflictException>(()=>store.SaveAsync(first with {Name="Stale"},default));
        await Assert.ThrowsAsync<FavoriteDestinationConflictException>(()=>store.DeleteAsync(first.Id,first.Revision,first.ChangeId,default));
        using(var reopened=new FavoriteDestinationsStore(_root)) Assert.Equal(renamed,Assert.Single((await reopened.LoadAsync(default)).Destinations));
        await store.DeleteAsync(renamed.Id,renamed.Revision,renamed.ChangeId,default);
        Assert.Empty((await store.LoadAsync(default)).Destinations);
        await Assert.ThrowsAsync<FavoriteDestinationConflictException>(()=>store.SaveAsync(renamed,default));
        using var backup=JsonDocument.Parse(await File.ReadAllBytesAsync(store.Path+".bak"));
        Assert.Equal(renamed.Id,Assert.Single(backup.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetGuid());
    }
    [Fact]
    public async Task DifferentStoreInstancesSerializeAddsAndOnlyOneConcurrentStaleRenameWins()
    {
        using var first=Store();using var second=new FavoriteDestinationsStore(_root);
        var saved=await Task.WhenAll(first.SaveAsync(Draft("A"),default),second.SaveAsync(Draft("B"),default));
        Assert.Equal(2,(await first.LoadAsync(default)).Destinations.Count);
        var results=await Task.WhenAll(TrySave(first,saved[0] with {Name="Changed A"}),TrySave(second,saved[0] with {Name="Changed B"}));
        Assert.Single(results.Where(success=>success));Assert.Single(results.Where(success=>!success));
        Assert.Equal(2,(await second.LoadAsync(default)).Destinations.Count);
        static async Task<bool> TrySave(IFavoriteDestinationStore store,FavoriteDestination draft)
        {try {await store.SaveAsync(draft,default);return true;} catch(FavoriteDestinationConflictException) {return false;}}
    }
    [Theory]
    [InlineData("")]
    [InlineData("not json secret-marker")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Revision\":1,\"Items\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Items\":[],\"Cookie\":\"secret-marker\"}")]
    public async Task DamagedOrUnexpectedStorageNeverSilentlyBecomesAnEmptyLibrary(string payload)
    {
        using var store=Store();await File.WriteAllTextAsync(store.Path,payload);var before=await File.ReadAllBytesAsync(store.Path);
        var error=await Assert.ThrowsAsync<FavoriteDestinationStorageException>(()=>store.LoadAsync(default));
        Assert.Equal(payload=="{\"Version\":1,\"Version\":1,\"Revision\":1,\"Items\":[]}"
            ?FavoriteDestinationStorageProblem.UnsupportedVersion:FavoriteDestinationStorageProblem.Damaged,error.Problem);Assert.DoesNotContain("secret-marker",error.ToString());
        Assert.Equal(before,await File.ReadAllBytesAsync(store.Path));Assert.False(File.Exists(store.Path+".bak"));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task UnsupportedVersionsNeverDowngradeOrFallBackOverAnExistingValidBackup(int version)
    {
        using var store=Store();var saved=await store.SaveAsync(Draft(),default);
        string payload=$"{{\"Version\":{version},\"Revision\":99,\"Items\":[],\"FutureField\":true}}";
        await File.WriteAllTextAsync(store.Path,payload);var backup=await File.ReadAllBytesAsync(store.Path+".bak");
        var error=await Assert.ThrowsAsync<FavoriteDestinationStorageException>(()=>store.LoadAsync(default));
        Assert.Equal(FavoriteDestinationStorageProblem.UnsupportedVersion,error.Problem);
        await Assert.ThrowsAsync<FavoriteDestinationStorageException>(()=>store.SaveAsync(saved with {Name="No downgrade"},default));
        Assert.Equal(payload,await File.ReadAllTextAsync(store.Path));Assert.Equal(backup,await File.ReadAllBytesAsync(store.Path+".bak"));
    }
    [Fact]
    public async Task BackupRecoveryPreservesTheDamagedOriginalAndDoesNotReuseAChangeIdentity()
    {
        using var store=Store();var original=await store.SaveAsync(Draft("Original"),default);
        var beforeDamage=await store.SaveAsync(original with {Name="Second revision"},default);
        byte[] backup=await File.ReadAllBytesAsync(store.Path+".bak");const string damaged="truncated secret-marker";
        await File.WriteAllTextAsync(store.Path,damaged);
        var recovered=await store.LoadAsync(default);Assert.NotNull(recovered.Notice);Assert.Equal(original,Assert.Single(recovered.Destinations));
        Assert.Equal(damaged,await File.ReadAllTextAsync(store.Path));
        var repaired=await store.SaveAsync(original with {Name="Repaired revision"},default);
        Assert.Equal(beforeDamage.Revision,repaired.Revision);Assert.NotEqual(beforeDamage.ChangeId,repaired.ChangeId);
        Assert.Equal(damaged,await File.ReadAllTextAsync(store.Path+".damaged"));Assert.Equal(backup,await File.ReadAllBytesAsync(store.Path+".bak"));
        await Assert.ThrowsAsync<FavoriteDestinationConflictException>(()=>store.SaveAsync(beforeDamage with {Name="Stale before corruption"},default));
        await Assert.ThrowsAsync<FavoriteDestinationConflictException>(()=>store.DeleteAsync(beforeDamage.Id,beforeDamage.Revision,beforeDamage.ChangeId,default));
        Assert.Equal(repaired,Assert.Single((await store.LoadAsync(default)).Destinations));
        await File.WriteAllTextAsync(store.Path,"different damaged original");
        var blocked=await Assert.ThrowsAsync<FavoriteDestinationStorageException>(()=>store.SaveAsync(original with {Name="Cannot discard old damage"},default));
        Assert.Equal(FavoriteDestinationStorageProblem.RecoveryBlocked,blocked.Problem);
        Assert.Equal("different damaged original",await File.ReadAllTextAsync(store.Path));Assert.Equal(damaged,await File.ReadAllTextAsync(store.Path+".damaged"));
    }
    [Fact]
    public async Task MissingMainUsesAnExistingBackupAndUncommittedDataIsNeverInventedAsFavorites()
    {
        using var store=Store();await store.SaveAsync(Draft(),default);File.Delete(store.Path);
        var recovered=await store.LoadAsync(default);Assert.NotNull(recovered.Notice);Assert.Empty(recovered.Destinations);
        Assert.False(File.Exists(store.Path));File.Delete(store.Path+".bak");await File.WriteAllTextAsync(store.Path+".tmp","unfinished");
        await Assert.ThrowsAsync<FavoriteDestinationStorageException>(()=>store.LoadAsync(default));
        Assert.Equal("unfinished",await File.ReadAllTextAsync(store.Path+".tmp"));Assert.False(File.Exists(store.Path));
    }
    [Fact]
    public async Task OversizedAndMalformedRecordsAreBoundedWithoutLosingTheOriginal()
    {
        using var store=Store();await File.WriteAllBytesAsync(store.Path,new byte[FavoriteDestinationsStore.MaximumFileBytes+1]);
        await Assert.ThrowsAsync<FavoriteDestinationStorageException>(()=>store.LoadAsync(default));
        Assert.Equal(FavoriteDestinationsStore.MaximumFileBytes+1,new FileInfo(store.Path).Length);
        var item=Draft() with {Revision=1,ChangeId=Guid.NewGuid()};
        foreach(string malformed in new[] {Serialize([item,item]),Serialize([item with {JobId="secret-marker"}]),Serialize([item with {PlaceId=0}]),
            Serialize([item with {Revision=2}]),Serialize([item with {ChangeId=Guid.Empty}]),Serialize([item]).Replace("123","9223372036854775808",StringComparison.Ordinal)})
        {
            await File.WriteAllTextAsync(store.Path,malformed);
            var error=await Assert.ThrowsAsync<FavoriteDestinationStorageException>(()=>store.LoadAsync(default));Assert.DoesNotContain("secret-marker",error.ToString());
            Assert.Equal(malformed,await File.ReadAllTextAsync(store.Path));
        }
    }
    [Fact]
    public async Task OperationalCapacityIsExplicitAndRenamesOrDeletesStillWorkAtTheLimit()
    {
        using var store=Store();var entries=Enumerable.Range(1,FavoriteDestination.MaximumCount)
            .Select(id=>new FavoriteDestination(Guid.NewGuid(),"Destination "+id,123,null,1,Guid.NewGuid())).ToArray();
        await File.WriteAllTextAsync(store.Path,Serialize(entries));
        var error=await Assert.ThrowsAsync<FavoriteDestinationCapacityException>(()=>store.SaveAsync(Draft("One more"),default));
        Assert.Contains("saved accounts are not limited",error.Message);
        var renamed=await store.SaveAsync(entries[0] with {Name="Still renameable"},default);
        await store.DeleteAsync(renamed.Id,renamed.Revision,renamed.ChangeId,default);await store.SaveAsync(Draft("Replacement"),default);
        Assert.Equal(FavoriteDestination.MaximumCount,(await store.LoadAsync(default)).Destinations.Count);
    }
    [Fact]
    public async Task CancellationWhileAnotherInstanceOwnsTheLockPreservesCommittedBytes()
    {
        using var store=Store();await store.SaveAsync(Draft(),default);byte[] before=await File.ReadAllBytesAsync(store.Path);
        await using var held=new FileStream(store.Path+".lock",FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.SaveAsync(Draft("Not committed"),stop.Token));
        Assert.Equal(before,await File.ReadAllBytesAsync(store.Path));
    }
    [Fact]
    public async Task AFailedAtomicReplacementLeavesTheAcceptedMainAndBackupIntact()
    {
        using var store=Store();var saved=await store.SaveAsync(Draft(),default);
        byte[] before=await File.ReadAllBytesAsync(store.Path),backup=await File.ReadAllBytesAsync(store.Path+".bak");
        await using(var held=new FileStream(store.Path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.SaveAsync(saved with {Name="Unaccepted"},stop.Token));
        }
        Assert.Equal(before,await File.ReadAllBytesAsync(store.Path));Assert.Equal(backup,await File.ReadAllBytesAsync(store.Path+".bak"));
        Assert.Equal(saved,Assert.Single((await store.LoadAsync(default)).Destinations));
        var repaired=await store.SaveAsync(saved with {Name="Accepted after retry"},default);Assert.Equal(2,repaired.Revision);
        Assert.False(File.Exists(store.Path+".tmp"));
    }
    private static string Serialize(FavoriteDestination[] items)=>JsonSerializer.Serialize(new {Version=1,Revision=1,
        Items=items.Select(item=>new {item.Id,item.Name,item.PlaceId,item.JobId,item.Revision,item.ChangeId}).ToArray()});
    public void Dispose()
    {
        string resolved=Path.GetFullPath(_root),temporary=Path.GetFullPath(Path.GetTempPath());
        if(Path.GetDirectoryName(resolved)!=temporary.TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(resolved).StartsWith("zenith-favorites-",StringComparison.Ordinal))
            throw new InvalidOperationException("The owned favorites fixture path is invalid.");
        if(Directory.Exists(resolved)) Directory.Delete(resolved,true);
    }
}
