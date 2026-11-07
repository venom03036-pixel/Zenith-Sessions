using System.Windows.Threading;
using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ViewModelTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-viewmodel-tests-"+Guid.NewGuid().ToString("N"));
    private async Task CheckAsync(Func<ShellViewModel,Task> check)
    {
        await OnDispatcherAsync(async ()=>
        {
            await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());
            await vm.InitializeAsync(); Assert.Equal("Settings saved",vm.SaveStatus);
            await check(vm);
        }).WaitAsync(TimeSpan.FromSeconds(15));
    }
    [Fact]
    public Task RestoringAFieldCannotHideAnUnsavedViewPreference()=>CheckAsync(async vm=>
    {
        vm.Dense=true; vm.SpareRam="4"; vm.SpareRam="3.0";
        Assert.Equal("Unsaved settings",vm.SaveStatus);
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved",vm.SaveStatus);
        using var store=new SettingsStore(_root); Assert.True((await store.LoadAsync(default)).Settings.DenseAccounts);
    });
    [Fact]
    public Task ThemeAutosaveCannotClaimThatUnappliedCapacityEditsAreSaved()=>CheckAsync(async vm=>
    {
        vm.SpareRam="4"; vm.ApplyTheme.Execute("ocean-blue"); await vm.ApplyTheme.Execution;
        Assert.Equal("Unsaved settings",vm.SaveStatus);
        using var store=new SettingsStore(_root); var loaded=(await store.LoadAsync(default)).Settings;
        Assert.Equal("ocean-blue",loaded.Theme); Assert.Equal(3,loaded.Capacity!.SparePhysicalGiB);
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal(4,(await store.LoadAsync(default)).Settings.Capacity!.SparePhysicalGiB);
        Assert.Equal("Settings saved",vm.SaveStatus);
    });
    [Fact]
    public Task ConcurrentThemeAndSettingsSavesPreserveTheLatestSnapshot()=>CheckAsync(async vm=>
    {
        vm.ApplyTheme.Execute("amber-night"); vm.SpareRam="5"; vm.SaveSettings.Execute(null);
        await Task.WhenAll(vm.ApplyTheme.Execution,vm.SaveSettings.Execution);
        using var store=new SettingsStore(_root); var loaded=(await store.LoadAsync(default)).Settings;
        Assert.Equal("amber-night",loaded.Theme); Assert.Equal(5,loaded.Capacity!.SparePhysicalGiB);
        Assert.Equal("Settings saved",vm.SaveStatus);
        vm.SpareRam="6"; Assert.Equal("Unsaved settings",vm.SaveStatus); vm.SpareRam="5.0"; Assert.Equal("Settings saved",vm.SaveStatus);
    });
    [Fact]
    public Task SaveFailureStaysVisibleAcrossAnUnrelatedProfileSaveAndRetrySucceeds()=>CheckAsync(async vm=>
    {
        string file=Path.Combine(_root,"settings.json"); File.SetAttributes(file,FileAttributes.ReadOnly);
        try
        {
            vm.SpareRam="4"; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Save failed — retry",vm.SaveStatus);
            await vm.SaveAccountAsync(AccountProfile.Create("Synthetic profile"));
            Assert.Equal("Save failed — retry",vm.SaveStatus);
            Assert.Equal("Synthetic profile",vm.SelectedAccount!.Label);
        }
        finally { File.SetAttributes(file,FileAttributes.Normal); }
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved",vm.SaveStatus);
    });
    [Fact]
    public Task SidebarNavigationPreservesSelectionChecksAndUnappliedSettings()=>CheckAsync(async vm=>
    {
        await vm.SaveAccountAsync(AccountProfile.Create("Selected fixture"));
        var selected=vm.SelectedAccount;Assert.NotNull(selected);selected.IsChecked=true;
        vm.SpareRam="4";vm.QueueExpanded=true;
        foreach(string tab in Enumerable.Range(0,100).SelectMany(_=>new[]{"Overview","Accounts","Sessions","Automation","Diagnostics","Settings","Guide"}))
        {
            vm.Navigate.Execute(tab);Assert.Equal(tab,vm.Tab);
            Assert.Same(selected,vm.SelectedAccount);Assert.True(selected.IsChecked);
            Assert.Equal("4",vm.SpareRam);Assert.Equal("Unsaved settings",vm.SaveStatus);Assert.True(vm.QueueExpanded);
        }
        vm.Lock.Execute(null);vm.Navigate.Execute("Accounts");Assert.Equal("Guide",vm.Tab);
        vm.Unlock.Execute(null);vm.Navigate.Execute("Accounts");Assert.Equal("Accounts",vm.Tab);
        using var store=new SettingsStore(_root);Assert.Equal(3,(await store.LoadAsync(default)).Settings.Capacity!.SparePhysicalGiB);
    });
    [Fact]
    public Task RapidQueriesKeepTheLastFilterAndShutdownIsIdempotent()=>CheckAsync(async vm=>
    {
        await vm.SaveAccountAsync(AccountProfile.Create("Alpha")); await vm.SaveAccountAsync(AccountProfile.Create("Beta"));
        for(int i=0;i<100;i++) vm.Search=i%2==0?"Alpha":"Beta";
        var until=DateTime.UtcNow.AddSeconds(3);
        while(vm.Accounts.Count!=1 || vm.Accounts[0].Label!="Beta")
        { if(DateTime.UtcNow>until) throw new TimeoutException("The final filter did not settle."); await Task.Delay(20); }
        Assert.Equal("Beta",vm.Search); Assert.Equal(2,vm.AccountCount);
        await vm.DisposeAsync(); await vm.DisposeAsync();
    });
    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,20),[])); }
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
    }
    [Fact]
    public Task RepeatLaunchUsesOneOwnerAndShutdownDoesNotNeedAPumpingUi()=>OnDispatcherAsync(async ()=>
    {
        using var owner=new SingleInstanceCoordinator(_root); Assert.True(owner.IsOwner);
        var restored=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Listen(_=> { restored.TrySetResult(); return Task.CompletedTask; });
        using var second=new SingleInstanceCoordinator(_root); Assert.False(second.IsOwner);
        Assert.True(await second.RestoreExistingAsync(default)); await restored.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Mirrors EntryPoint's final synchronous teardown, after the dispatcher has stopped pumping.
        owner.StopAsync().GetAwaiter().GetResult();
    }).WaitAsync(TimeSpan.FromSeconds(5));
    [Fact]
    public Task IsolatedSamplePreviewCannotClaimAGlobalDesktopShortcut()=>OnDispatcherAsync(async ()=>
    {
        await using var vm=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        vm.SetPanicRegistration(true);Assert.Equal("Panic stop",vm.PanicLabel);Assert.Contains("do not register global shortcuts",vm.PanicShortcutStatus);
        Assert.Equal(16,vm.AccountCount);
    }).WaitAsync(TimeSpan.FromSeconds(10));
    [Fact]
    public Task FavoritesAndNotesRoundTripThroughTheActualMetadataCommands()=>CheckAsync(async vm=>
    {
        await vm.SaveAccountAsync(AccountProfile.Create("Source") with { Notes="Synthetic note\nSecond line" });
        vm.ToggleFavorite.Execute(null); await vm.ToggleFavorite.Execution; Assert.True(vm.SelectedAccount!.Profile.IsFavorite);
        string path=Path.Combine(_root,"metadata.json"); await vm.ExportAsync(path);
        string json=await File.ReadAllTextAsync(path); Assert.Contains("\"Version\":3",json); Assert.DoesNotContain("AccessToken",json);
        await vm.ImportAsync(path); Assert.Equal(2,vm.AccountCount);
        Assert.All(vm.Accounts,row=> { Assert.True(row.Profile.IsFavorite);Assert.Equal("Synthetic note\nSecond line",row.Profile.Notes);Assert.Equal("Not linked",row.Identity); });
        Assert.Equal(2,vm.Accounts.Select(a=>a.Profile.Id).Distinct().Count());
    });
    [Fact]
    public Task LegacyMetadataDoesNotAcquireAClaimedVerifiedIdentity()=>CheckAsync(async vm=>
    {
        string path=Path.Combine(_root,"legacy.json");
        await File.WriteAllTextAsync(path,"{\"Version\":1,\"Profiles\":[{\"Id\":\"1ba20278-d37d-42fb-97df-4b101f727d0c\",\"Label\":\"Legacy\",\"Username\":\"user\",\"Group\":\"General\",\"Priority\":0,\"RobloxUserId\":123,\"UpdatedUtc\":\"2026-11-05T00:00:00Z\"}]}");
        await vm.ImportAsync(path);var row=Assert.Single(vm.Accounts);
        Assert.Equal("",row.Profile.Notes);Assert.False(row.Profile.IsFavorite);Assert.Null(row.Profile.RobloxUserId);Assert.Equal("Not linked",row.Identity);
    });
    [Fact]
    public Task GroupRefreshKeepsTheActualComboSelectionAndAnUnavailableLaunchStaysBlocked()=>CheckAsync(async vm=>
    {
        var combo=new System.Windows.Controls.ComboBox { DisplayMemberPath="Label" };
        combo.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty,new System.Windows.Data.Binding("Groups") { Source=vm });
        combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,new System.Windows.Data.Binding("Group") { Source=vm,Mode=System.Windows.Data.BindingMode.TwoWay });
        await vm.SaveAccountAsync(AccountProfile.Create("Group profile") with { Group="First" });
        await Dispatcher.Yield(DispatcherPriority.DataBind);Assert.Equal(GroupFilter.All,combo.SelectedItem);
        vm.Group=vm.Groups.First(g=>g.Value=="First");
        await vm.SaveAccountAsync(AccountProfile.Create("Second profile") with { Group="Other" });
        await Dispatcher.Yield(DispatcherPriority.DataBind);Assert.Equal("First",((GroupFilter)combo.SelectedItem).Value);
        vm.SelectedAccount=vm.Accounts.First();vm.QueueSelected.Execute(null);await vm.QueueSelected.Execution;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var launch=Assert.Single(vm.Launches);Assert.Equal(LaunchState.Blocked,launch.Operation.State);Assert.Equal(LaunchProblem.ProviderUnavailable,launch.Operation.Problem);
        Assert.Contains("no client was started",vm.Notice);Assert.True(vm.QueueExpanded);
    });
    [Fact]
    public Task OversizedExportPreservesTheDestinationAndDeletesItsPartialFile()=>CheckAsync(async vm=>
    {
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        await repository.CreateManyAsync(Enumerable.Range(0,4500).Select(i=>AccountProfile.Create("Synthetic "+i) with { Notes=new string('x',4000) }).ToArray(),default);
        string path=Path.Combine(_root,"preserved-export.json");await File.WriteAllTextAsync(path,"previous export");
        await Assert.ThrowsAsync<InvalidDataException>(()=>vm.ExportAsync(path));
        Assert.Equal("previous export",await File.ReadAllTextAsync(path));Assert.False(File.Exists(path+".tmp"));
    });
    [Fact]
    public void StartupParsingUsesOneCanonicalDataFolderAndCannotSeedTheNormalLibrary()
    {
        Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--sample-profiles"]));
        Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--data-root",StartupOptions.DefaultRoot,"--sample-profiles"]));
        Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--data-root",_root,"--data-root",_root+"different"]));
        Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--data-root"]));
        Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--self-check"]));
        Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--data-root",_root,"--self-check","--sample-profiles"]));
        Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--unknown"]));
        var options=StartupOptions.Parse(["--data-root",_root,"--sample-profiles"]);
        Assert.Equal(Path.GetFullPath(_root),options.DataRoot); Assert.True(options.SampleProfiles);
    }
    [Fact]
    public void AuthorizationRowsExpireWithoutKeepingTokensOrReloadingTheVault()
    {
        var row=new AccountRow(AccountProfile.Create("A")); var now=DateTimeOffset.UtcNow;
        row.SetAuthorization("Linked","Verified identity",now.AddSeconds(1)); row.UpdateAuthorizationClock(now);
        Assert.Equal("Linked",row.Identity); row.UpdateAuthorizationClock(now.AddSeconds(2)); Assert.Equal("Expired",row.Identity);
    }
    [Fact]
    public void AccountRowUsesLastLinkedIdentityInsteadOfAPossiblyIncorrectEnteredUsername()
    {
        var profile=AccountProfile.Create("Personal alias") with { Username="entered_name" };
        var summary=new AuthorizationMetadata(profile.Id,profile.Id.ToString("N"),"roblox-oauth",RobloxOAuthIdentity.Issuer,"123",123,"linked_name",8,
            AuthorizationHealth.Linked,DateTimeOffset.UtcNow.AddMinutes(5),DateTimeOffset.UtcNow);
        var row=new AccountRow(profile);row.SetCachedAuthorization(summary);
        Assert.Equal("linked_name",row.Label);Assert.Equal("@linked_name",row.Username);Assert.Equal("Personal alias",row.Alias);
        Assert.Equal(123,row.KnownUserId);Assert.Equal(8,row.AuthorizationGeneration);Assert.Equal("Saved link",row.Identity);
        Assert.Contains("checked before use",row.LinkedAccount);Assert.False(summary.IsCurrentAuthorization);
    }
    [Fact]
    public Task TenThousandMetadataProfilesRemainPagedWhenACredentialCannotBeDecrypted()=>CheckAsync(async vm=>
    {
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        var profiles=Enumerable.Range(0,10000).Select(i=>AccountProfile.Create("Synthetic "+i.ToString("D5"))).ToArray();
        await repository.CreateManyAsync(profiles,default);
        string vaultDirectory=Path.Combine(_root,"accounts.vault");Directory.CreateDirectory(vaultDirectory);
        string cipher=Path.Combine(vaultDirectory,profiles[0].Id.ToString("N")+".dpapi");await File.WriteAllBytesAsync(cipher,[1,2,3,4]);
        vm.Refresh.Execute(null);await vm.Refresh.Execution;
        Assert.Equal(10000,vm.AccountCount);Assert.Equal(100,vm.Accounts.Count);
        Assert.Equal("Synthetic 00000",vm.Accounts[0].Label);Assert.Equal(new byte[]{1,2,3,4},await File.ReadAllBytesAsync(cipher));
        Assert.DoesNotContain(vm.Accounts,row=>row.Identity=="Linked");Assert.True(vm.IsReady);
    });
    [Fact]
    public Task SafeClientCloseRemainsAvailableWhileTheAccountDashboardIsLocked()=>CheckAsync(async vm=>
    {
        vm.LockDashboard();Assert.True(vm.IsLocked);
        var observed=new SessionObservation(123,1,"Synthetic",1,"Synthetic test only",false,true,0,0,null,DateTimeOffset.UtcNow);
        await vm.RequestCloseAsync(observed);Assert.True(vm.IsLocked);Assert.Contains("Close was not sent",vm.Notice);
    });
    [Fact]
    public Task AGroupNamedAllGroupsRemainsDistinctFromTheAllProfilesFilter()=>CheckAsync(async vm=>
    {
        await vm.SaveAccountAsync(AccountProfile.Create("One") with { Group="All groups" });
        await vm.SaveAccountAsync(AccountProfile.Create("Two") with { Group="Different" });
        Assert.Equal(2,vm.Groups.Count(g=>g.Label=="All groups"));
        vm.Group=vm.Groups.Single(g=>g.Value=="All groups");
        var until=DateTime.UtcNow.AddSeconds(3);
        while(vm.Accounts.Count!=1 || vm.Accounts[0].Label!="One")
        { if(DateTime.UtcNow>until) throw new TimeoutException("The selected named group did not settle."); await Task.Delay(20); }
        vm.Group=GroupFilter.All;
        until=DateTime.UtcNow.AddSeconds(3);
        while(vm.Accounts.Count!=2)
        { if(DateTime.UtcNow>until) throw new TimeoutException("The all-profiles filter did not settle."); await Task.Delay(20); }
    });
    [Fact]
    public Task CorruptSettingsCannotPresentAnApparentlyEmptyReadyLibrary()=>OnDispatcherAsync(async ()=>
    {
        Directory.CreateDirectory(_root); string path=Path.Combine(_root,"settings.json"); await File.WriteAllTextAsync(path,"damaged settings");
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor()); await vm.InitializeAsync();
        Assert.Equal("Startup needs attention",vm.SaveStatus); Assert.Contains("Library unavailable",vm.EmptyAccountsText);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>vm.SaveAccountAsync(AccountProfile.Create("Blocked")));
        Assert.Equal("damaged settings",await File.ReadAllTextAsync(path));
    });
    [Fact]
    public Task RecoveredBackupIsMarkedForRepairUntilAValidSaveCompletes()=>OnDispatcherAsync(async ()=>
    {
        using(var store=new SettingsStore(_root)) { await store.LoadAsync(default); await store.SaveAsync(new(DenseAccounts:true),default); await File.WriteAllTextAsync(store.Path,"damaged settings"); }
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor()); await vm.InitializeAsync();
        Assert.Equal("Recovered settings — save to repair",vm.SaveStatus);
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved",vm.SaveStatus); Assert.True(File.Exists(Path.Combine(_root,"settings.json.damaged")));
    });
    [Fact]
    public Task ACorruptStoredProfileCannotLeaveTheLibraryMarkedReady()=>OnDispatcherAsync(async ()=>
    {
        using(var repository=new AccountRepository(_root))
        { await repository.InitializeAsync(default); await repository.SaveAsync(AccountProfile.Create("Corrupt fixture"),default); }
        using(var db=new SqliteConnection($"Data Source={Path.Combine(_root,"accounts.db")};Pooling=False"))
        { await db.OpenAsync(); using var command=db.CreateCommand(); command.CommandText="UPDATE profiles SET id='not-a-guid'"; await command.ExecuteNonQueryAsync(); }
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor()); await vm.InitializeAsync();
        Assert.False(vm.IsReady); Assert.Contains("Library unavailable",vm.EmptyAccountsText); Assert.Equal("Startup needs attention",vm.SaveStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>vm.SaveAccountAsync(AccountProfile.Create("Must not replace data")));
    });
    [Fact]
    public Task CorruptVaultFileOfADeletedProfileDoesNotMakeTheLibraryUnavailable()=>OnDispatcherAsync(async ()=>
    {
        using(var repository=new AccountRepository(_root))
        { await repository.InitializeAsync(default); await CleanupRecoveryTests.DeletedProfileWithDamagedPointerAsync(repository,_root); }
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor()); await vm.InitializeAsync();
        Assert.True(vm.IsReady); Assert.NotEqual("Startup needs attention",vm.SaveStatus); Assert.DoesNotContain("Library unavailable",vm.EmptyAccountsText);
        Assert.Contains("1 unused protected record",vm.CredentialCleanupStatus); Assert.Contains("is damaged",vm.CredentialCleanupStatus);
        Assert.DoesNotContain("Check storage access",vm.CredentialCleanupStatus);
    });
    private static Task OnDispatcherAsync(Func<Task> work)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            // A tracked delegate owns the async work; no window or desktop input is created.
            Task operation=Task.CompletedTask;
            dispatcher.BeginInvoke(()=> { operation=RunAsync(); });
            async Task RunAsync()
            {
                try { await work(); completion.TrySetResult(); }
                catch(Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-viewmodel-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
