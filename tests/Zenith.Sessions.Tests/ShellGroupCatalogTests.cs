using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>Actual SQLite and STA Shell integration with synthetic metadata only; no native client or sign-in.</summary>
public sealed class ShellGroupCatalogTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-shell-catalog-"+Guid.NewGuid().ToString("N"));

    [Fact]
    public Task EmptyGroupCreationColorOrderingAndLifecycleReachTheActualShell() =>OnUi(async()=>
    {
        var repository=new AccountRepository(_root);var auth=new ForbiddenAuthorization();
        await using var shell=await Ready(repository,auth);
        await shell.SaveAccountAsync(AccountProfile.Create("Kept account") with{Group="General",Notes="Preserve",Priority=42,PreferredPlaceId=6564161046});
        var profile=Assert.Single(shell.Accounts).Profile;
        var page=await shell.ReadGroupCatalogAsync(new(),default);
        var empty=await shell.CreateGroupAsync("Empty raid","#40b8a0",page.CatalogRevision,default);
        Assert.Equal("#40B8A0",empty.ColorHex);
        Assert.Equal("#40B8A0",shell.Groups.Single(group=>group.Value=="Empty raid").ColorHex);
        Assert.Equal(profile,await repository.FindAsync(profile.Id,default));
        shell.Group=shell.Groups.Single(group=>group.Value=="Empty raid");await Refresh(shell);
        Assert.Empty(shell.Accounts);Assert.Equal("Empty raid",shell.Group.Value);
        page=await shell.ReadGroupCatalogAsync(new(),default);
        empty=await shell.SetGroupColorAsync(empty,"#ffaa00",page.CatalogRevision,default);
        Assert.Equal("#FFAA00",shell.Group.ColorHex);Assert.Equal("Empty raid",shell.Group.Value);
        page=await shell.ReadGroupCatalogAsync(new(),default);
        empty=await shell.MoveGroupAsync(empty,AccountGroupMoveDirection.Up,page.CatalogRevision,default);
        Assert.Equal("Empty raid",shell.Groups[1].Value);Assert.Equal(profile,await repository.FindAsync(profile.Id,default));
        var review=await shell.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename,"Empty raid","Renamed empty"),default);
        Assert.True(review.SourceExists);Assert.Equal(0,review.AffectedCount);
        Assert.Equal(0,await shell.ApplyGroupChangeAsync(review,default));
        Assert.Equal("Renamed empty",shell.Group.Value);Assert.Equal(empty.Id,(await shell.FindGroupAsync("Renamed empty",default))!.Id);
        review=await shell.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Remove,"Renamed empty","General"),default);
        Assert.Equal(0,await shell.ApplyGroupChangeAsync(review,default));
        Assert.Null(shell.Group.Value);Assert.Single(shell.Accounts);Assert.Null(await shell.FindGroupAsync(empty.Id,default));
        Assert.Equal(profile,await repository.FindAsync(profile.Id,default));Assert.Equal(0,auth.Calls);Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task PickerPinsAnExplicitGroupBeyondItsFirstThousandWithoutAStorageLimit() =>OnUi(async()=>
    {
        var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        await repository.CreateManyAsync(Enumerable.Range(0,1001).Select(index=>AccountProfile.Create("Profile "+index) with{Group=$"Group {index:D4}"}).ToArray(),default);
        var auth=new ForbiddenAuthorization();await using var shell=await Ready(repository,auth);
        Assert.Equal(1000,shell.GroupCatalogChoices.Count);Assert.DoesNotContain(shell.Groups,group=>group.Value=="Group 1000");
        var outside=await shell.FindGroupAsync("Group 1000",default);Assert.NotNull(outside);
        shell.Search="not present";await Refresh(shell);
        await shell.ShowGroupAccountsAsync(outside.Id,default);
        Assert.Equal("",shell.Search);Assert.True(shell.IsAccounts);
        Assert.Equal("Group 1000",shell.Group.Value);Assert.Equal("Profile 1000",Assert.Single(shell.Accounts).Alias);
        Assert.Equal(1000,shell.GroupCatalogChoices.Count);Assert.Contains(shell.GroupCatalogChoices,group=>group.Name=="Group 1000");
        Assert.Contains("Manage groups",shell.GroupChoicesNotice);
        var catalog=await shell.ReadGroupCatalogAsync(new(Search:"Group 1000"),default);
        Assert.Equal(1,catalog.Total);var exact=Assert.Single(catalog.Items);
        await shell.SetGroupColorAsync(exact,"#123456",catalog.CatalogRevision,default);
        Assert.Equal("Group 1000",shell.Group.Value);Assert.Equal("#123456",shell.Group.ColorHex);
        Assert.Equal(1001,(await shell.ReadGroupCatalogAsync(new(),default)).Total);
        Assert.Equal(0,auth.Calls);Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task ExportCarriesIndependentCatalogPresentationWithoutAuthorization() =>OnUi(async()=>
    {
        var repository=new AccountRepository(_root);var auth=new ForbiddenAuthorization();await using var shell=await Ready(repository,auth);
        var page=await shell.ReadGroupCatalogAsync(new(),default);await shell.CreateGroupAsync("Empty only","#FFAA00",page.CatalogRevision,default);
        string export=Path.Combine(_root,"metadata.json");await shell.ExportAsync(export);
        Assert.Contains("empty ones",shell.Notice);Assert.Contains("colors",shell.Notice);Assert.Contains("group order",shell.Notice);
        Assert.Contains("Authorization and credentials are excluded",shell.Notice);
        var exported=ProfileMetadataImport.DecodeDocument(await File.ReadAllBytesAsync(export));
        Assert.Equal(new ProfileMetadataGroup("Empty only","#FFAA00",0),Assert.Single(exported.Groups));Assert.Empty(exported.Profiles);Assert.Equal(0,auth.Calls);
    });

    [Fact]
    public Task RenamedAndReusedNameCannotRedirectASelectedStableGroupToOtherAccounts() =>OnUi(async()=>
    {
        var repository=new CatalogRaceRepository(_root);await repository.InitializeAsync(default);
        await repository.CreateManyAsync([AccountProfile.Create("Original group member") with{Group="Raid"}],default);
        var selected=(await repository.FindGroupAsync("Raid",default))!;
        var auth=new ForbiddenAuthorization();await using var shell=await Ready(repository,auth);
        var priorGroup=shell.Group;var priorRows=shell.Accounts.Select(row=>row.Profile.Id).ToArray();
        repository.ReplaceNameAfterLookup=true;
        await Assert.ThrowsAsync<RevisionConflictException>(()=>shell.ShowGroupAccountsAsync(selected.Id,default));
        Assert.Equal(priorGroup,shell.Group);Assert.Equal(priorRows,shell.Accounts.Select(row=>row.Profile.Id));
        Assert.DoesNotContain(shell.Accounts,row=>row.Alias=="Different group member");
        Assert.NotEqual(selected.Id,(await repository.FindGroupAsync("Raid",default))!.Id);
        Assert.Equal("Renamed",(await repository.FindGroupAsync(selected.Id,default))!.Name);
        Assert.DoesNotContain("Showing accounts",shell.Notice);Assert.Empty(shell.Launches);Assert.Equal(0,auth.Calls);
    });

    private async Task<ShellViewModel> Ready(IAccountRepository repository,ForbiddenAuthorization auth)
    {
        var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),accounts:repository,signIn:auth,sessionClient:auth);
        try{await shell.InitializeAsync();Assert.True(shell.IsReady,shell.Notice);Assert.True(shell.HasGroupCatalog);return shell;}
        catch{await shell.DisposeAsync();throw;}
    }
    private static async Task Refresh(ShellViewModel shell){shell.Refresh.Execute(null);await shell.Refresh.Execution;}
    private sealed class CatalogRaceRepository(string root):IAccountRepository,IAccountGroupCatalog,IDisposable
    {
        private readonly AccountRepository _inner=new(root);
        public bool ReplaceNameAfterLookup;
        public Task InitializeAsync(CancellationToken ct)=>_inner.InitializeAsync(ct);
        public Task<AccountPage> ReadAsync(AccountQuery query,CancellationToken ct)=>_inner.ReadAsync(query,ct);
        public Task<AccountProfile?> FindAsync(Guid id,CancellationToken ct)=>_inner.FindAsync(id,ct);
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct)=>_inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile profile,CancellationToken ct)=>_inner.SaveAsync(profile,ct);
        public Task DeleteAsync(Guid id,long revision,CancellationToken ct)=>_inner.DeleteAsync(id,revision,ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles,CancellationToken ct)=>_inner.CreateManyAsync(profiles,ct);
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles,CancellationToken ct)=>_inner.DeleteManyAsync(profiles,ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct)=>_inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id,CancellationToken ct)=>_inner.CompleteCredentialCleanupAsync(id,ct);
        public Task<AccountGroupCatalogPage> ReadGroupCatalogAsync(AccountGroupCatalogQuery query,CancellationToken ct)=>_inner.ReadGroupCatalogAsync(query,ct);
        public Task<AccountGroupDefinition?> FindGroupAsync(string name,CancellationToken ct)=>_inner.FindGroupAsync(name,ct);
        public async Task<AccountGroupDefinition?> FindGroupAsync(Guid id,CancellationToken ct)
        {
            var selected=await _inner.FindGroupAsync(id,ct);
            if(ReplaceNameAfterLookup&&selected is not null)
            {
                ReplaceNameAfterLookup=false;
                await _inner.ApplyGroupChangeAsync(await _inner.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename,selected.Name,"Renamed"),ct),ct);
                await _inner.CreateManyAsync([AccountProfile.Create("Different group member") with{Group=selected.Name}],ct);
            }
            return selected;
        }
        public Task<AccountGroupDefinition> CreateGroupAsync(string name,string? color,long revision,CancellationToken ct)=>_inner.CreateGroupAsync(name,color,revision,ct);
        public Task<AccountGroupDefinition> SetGroupColorAsync(AccountGroupDefinition expected,string? color,long revision,CancellationToken ct)=>_inner.SetGroupColorAsync(expected,color,revision,ct);
        public Task<AccountGroupDefinition> MoveGroupAsync(AccountGroupDefinition expected,AccountGroupMoveDirection direction,long revision,CancellationToken ct)=>_inner.MoveGroupAsync(expected,direction,revision,ct);
        public void Dispose()=>_inner.Dispose();
    }
    private sealed class ForbiddenAuthorization:IRobloxSessionSignIn,IRobloxSessionClient
    {
        public int Calls;
        public Task<RobloxSessionCredential> SignInAsync(Guid id,CancellationToken ct){Calls++;throw new InvalidOperationException("Group metadata must not sign in.");}
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential,CancellationToken ct){Calls++;throw new InvalidOperationException("Group metadata must not read authorization.");}
    }
    private sealed class Monitor:IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10),[]));}
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No native action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No native action expected.");
    }
    private static Task OnUi(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async()=>{try{await action();done.TrySetResult();}catch(Exception error){done.TrySetException(error);}finally{dispatcher.InvokeShutdown();}});Dispatcher.Run();}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(full).StartsWith("zenith-shell-catalog-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected fixture path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
