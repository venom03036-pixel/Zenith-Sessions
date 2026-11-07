using System.IO;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class RecipientBundlesViewModelTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-bundle-view-"+Guid.NewGuid().ToString("N"));
    private static AccountProfile Profile(string name)=>AccountProfile.Create(name);
    private static BundleMemberReview Member(AccountProfile profile)=>new(profile.Id,profile,new(123,7),123,"VerifiedName","Saved sign-in · checked again before launch");
    private static RecipientBundlesViewModel Create(IRecipientBundleStore store,Func<IReadOnlyList<Guid>> selected,
        Func<IReadOnlyList<Guid>,CancellationToken,Task<IReadOnlyList<BundleMemberReview>>>? read=null,
        Func<RobloxRequestedDestination?>? target=null,
        Func<RobloxRequestedDestination,IReadOnlyList<BundleMemberReview>,CancellationToken,Task<string>>? queue=null,Func<bool>? canUse=null)
        =>new(store,selected,read??((ids,_)=>Task.FromResult<IReadOnlyList<BundleMemberReview>>(ids.Select(id=>Member(Profile("Fixture") with{Id=id})).ToArray())),
            target??(()=>new(42)),queue??((_,_,_)=>Task.FromResult("Synthetic request recorded; no native client started.")),canUse??(()=>true));

    [Fact]
    public async Task BundlesSurvivePageSelectionChangesAndExplicitReplacementKeepsStableIdentity()
    {
        using var store=new RecipientBundleStore(_root);var first=Profile("First");var second=Profile("Second");
        IReadOnlyList<Guid> selected=[first.Id,second.Id];
        await using var vm=Create(store,()=>selected);await vm.LoadAsync();
        await vm.SaveCheckedAsync("Farm 👑");var bundle=Assert.Single(vm.Bundles);
        selected=[Guid.NewGuid()];await vm.ReviewSelectedAsync();
        Assert.Equal(new[]{first.Id,second.Id},vm.Members.Select(m=>m.ProfileId));
        await vm.RenameSelectedAsync("Renamed");Assert.Equal(bundle.Id,Assert.Single(vm.Bundles).Id);
        await vm.ReplaceSelectedRecipientsAsync();Assert.Equal(bundle.Id,Assert.Single(vm.Bundles).Id);
        Assert.Equal(selected,vm.SelectedBundle!.ProfileIds);await vm.DeleteSelectedAsync();
        Assert.Empty(vm.Bundles);Assert.Empty((await store.LoadAsync(default)).Bundles);
    }
    [Fact]
    public async Task MissingReferencesArePreservedAndPreventTheEntireBundleRequest()
    {
        using var store=new RecipientBundleStore(_root);var found=Profile("Found");Guid missing=Guid.NewGuid();int queues=0;
        await using var vm=Create(store,()=>[found.Id,missing],(ids,_)=>Task.FromResult<IReadOnlyList<BundleMemberReview>>(
            [Member(found),new(missing,null,null,null,"","Profile removed")]),queue:(_,_,_)=>{queues++;return Task.FromResult("Should not happen");});
        await vm.LoadAsync();await vm.SaveCheckedAsync("Two accounts");await vm.QueueSelectedAsync();
        Assert.Equal(0,queues);Assert.Equal(2,vm.Members.Count);Assert.True(vm.Members[1].IsMissing);
        Assert.Equal(new[]{found.Id,missing},Assert.Single((await store.LoadAsync(default)).Bundles).ProfileIds);
        Assert.Contains("No launch request",vm.Status);
    }
    [Fact]
    public async Task AdditionalPagesCanExtendOneBundleAndMissingRemovalRequiresAnExplicitFreshLookup()
    {
        using var store=new RecipientBundleStore(_root);var first=Profile("First page");var second=Profile("Second page");Guid missing=Guid.NewGuid();
        IReadOnlyList<Guid> checkedIds=[first.Id,missing];int reads=0;
        await using var vm=Create(store,()=>checkedIds,(ids,_)=>
        {
            reads++;return Task.FromResult<IReadOnlyList<BundleMemberReview>>(ids.Select(id=>id==missing?new BundleMemberReview(id,null,null,null,"","Missing"):
                Member(id==first.Id?first:second)).ToArray());
        });
        await vm.LoadAsync();await vm.SaveCheckedAsync("Across pages");Guid stable=vm.SelectedBundle!.Id;
        checkedIds=[first.Id,second.Id];await vm.AddCheckedRecipientsAsync();
        Assert.Equal(stable,vm.SelectedBundle!.Id);Assert.Equal(new[]{first.Id,missing,second.Id},vm.SelectedBundle.ProfileIds);Assert.Equal(0,reads);
        await vm.RemoveMissingRecipientsAsync();Assert.Equal(1,reads);
        Assert.Equal(new[]{first.Id,second.Id},vm.SelectedBundle!.ProfileIds);Assert.Equal(stable,vm.SelectedBundle.Id);
    }
    [Fact]
    public async Task DestinationAndRecipientsFreezeBeforeAsyncLookupAndSelectionCannotRetargetThem()
    {
        using var store=new RecipientBundleStore(_root);var first=Profile("First");var second=Profile("Second");
        IReadOnlyList<Guid> selection=[first.Id];var gate=new TaskCompletionSource<IReadOnlyList<BundleMemberReview>>(TaskCreationOptions.RunContinuationsAsynchronously);
        RobloxRequestedDestination destination=new(42,"12345678-1234-4234-8234-123456789abc");RobloxRequestedDestination? sent=null;Guid[]? idsSent=null;
        await using var vm=Create(store,()=>selection,(_,_)=>gate.Task,()=>destination,(target,members,_)=>
        {sent=target;idsSent=members.Select(m=>m.ProfileId).ToArray();return Task.FromResult("Pinned request");});
        await vm.LoadAsync();await vm.SaveCheckedAsync("First bundle");var original=vm.SelectedBundle;
        selection=[second.Id];await vm.SaveCheckedAsync("Second bundle");var other=vm.SelectedBundle;
        vm.SelectedBundle=vm.Bundles.Single(bundle=>bundle.Id==original!.Id);var work=vm.QueueSelectedAsync();
        Assert.True(vm.IsBusy);Assert.False(vm.CanManage);destination=new(99);vm.SelectedBundle=other;
        gate.TrySetResult([Member(first)]);await work;
        Assert.Equal(42,sent!.PlaceId);Assert.Equal("12345678-1234-4234-8234-123456789abc",sent.JobId);
        Assert.Equal(new[]{first.Id},idsSent);Assert.Same(other,vm.SelectedBundle);Assert.Empty(vm.Members);
        Assert.Contains("First bundle",vm.Status);
    }
    [Fact]
    public async Task CancelledLateLookupCannotQueueOrRepopulateLockedRowsAndExitJoinsActualOwner()
    {
        using var store=new RecipientBundleStore(_root);var profile=Profile("Fixture");bool allowed=true;int queues=0;
        var gate=new TaskCompletionSource<IReadOnlyList<BundleMemberReview>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm=Create(store,()=>[profile.Id],(_,_)=>gate.Task,queue:(_,_,_)=>{queues++;return Task.FromResult("Should not happen");},canUse:()=>allowed);
        await vm.LoadAsync();await vm.SaveCheckedAsync("Pinned");var work=vm.QueueSelectedAsync();
        allowed=false;vm.CancelWork();var exit=vm.DisposeAsync().AsTask();
        try{Assert.False(exit.IsCompleted);Assert.True(vm.IsBusy);Assert.Empty(vm.Bundles);Assert.Empty(vm.Members);}
        finally{gate.TrySetResult([Member(profile)]);}
        await work;await exit;Assert.Equal(0,queues);Assert.Empty(vm.Bundles);Assert.Empty(vm.Members);
    }
    [Fact]
    public async Task CorruptedStoragePreservesLastKnownRowsAndDoesNotPretendToLoadAnEmptyLibrary()
    {
        using var store=new RecipientBundleStore(_root);var profile=Profile("Fixture");
        await using var vm=Create(store,()=>[profile.Id]);await vm.LoadAsync();await vm.SaveCheckedAsync("Saved");
        var old=vm.SelectedBundle;await File.WriteAllTextAsync(store.Path,"corrupt metadata");
        if(File.Exists(store.Path+".bak"))await File.WriteAllTextAsync(store.Path+".bak","corrupt backup");
        await vm.LoadAsync();Assert.Same(old,vm.SelectedBundle);Assert.Single(vm.Bundles);Assert.False(vm.CanUseSelected);
        Assert.Contains("preserved",vm.Status);Assert.DoesNotContain("loaded",vm.Status);
    }
    [Fact]
    public async Task InvalidNamesAndEmptySelectionsDoNotDamageLoadedStateOrChangeDestination()
    {
        using var store=new RecipientBundleStore(_root);var profile=Profile("Fixture");IReadOnlyList<Guid> selected=[profile.Id];
        var destination=new RobloxRequestedDestination(42);int queued=0;await using var vm=Create(store,()=>selected,target:()=>destination,
            queue:(_,_,_)=>{queued++;return Task.FromResult("Unexpected automatic request");});
        await vm.LoadAsync();await vm.SaveCheckedAsync("Valid");var saved=vm.SelectedBundle;
        await vm.RenameSelectedAsync("https://roblox.com/?accessCode=secret-marker");Assert.Same(saved,vm.SelectedBundle);Assert.True(vm.CanUseSelected);
        Assert.DoesNotContain("secret-marker",vm.Status);
        await vm.RenameSelectedAsync("cookie=secret-marker");Assert.Same(saved,vm.SelectedBundle);Assert.True(vm.CanUseSelected); // A saved bundle's rename meets the whole name rule up front.
        Assert.Contains("bundle name",vm.Status);Assert.DoesNotContain("secret-marker",vm.Status);selected=[];await vm.ReplaceSelectedRecipientsAsync();
        Assert.Equal(new[]{profile.Id},vm.SelectedBundle!.ProfileIds);Assert.Equal(0,queued);
    }
    [Fact]
    public async Task UnexpectedMemberIdentityCannotBecomeAQueuedRecipient()
    {
        using var store=new RecipientBundleStore(_root);var profile=Profile("Requested");int queues=0;
        await using var vm=Create(store,()=>[profile.Id],(_,_)=>Task.FromResult<IReadOnlyList<BundleMemberReview>>([Member(Profile("Wrong"))]),
            queue:(_,_,_)=>{queues++;return Task.FromResult("Bad");});
        await vm.LoadAsync();await vm.SaveCheckedAsync("Saved");await vm.QueueSelectedAsync();
        Assert.Equal(0,queues);Assert.False(vm.CanUseSelected);Assert.Empty(vm.Members);Assert.Contains("could not complete",vm.Status);
    }
    public void Dispose()
    {
        string path=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(path)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(path).StartsWith("zenith-bundle-view-",StringComparison.Ordinal))throw new InvalidOperationException();
        if(Directory.Exists(path))Directory.Delete(path,true);
    }
}
