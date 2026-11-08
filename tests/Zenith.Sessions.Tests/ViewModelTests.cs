using System.Windows.Threading;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
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
        // Ownership is held per thread, so a second launch (another process in production) is built off the owner's thread.
        using var second=await Task.Run(()=>new SingleInstanceCoordinator(_root)); Assert.False(second.IsOwner);
        Assert.True(await second.RestoreExistingAsync(default)); await restored.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Mirrors EntryPoint's final synchronous teardown, after the dispatcher has stopped pumping.
        owner.StopAsync().GetAwaiter().GetResult();
    }).WaitAsync(TimeSpan.FromSeconds(5));
    [Fact]
    public void SecondLaunchAgainstAnInstanceMutexItMayNotUseExplainsDifferentPermissions()
    {
        string name; using(var probe=new SingleInstanceCoordinator(_root)) name=probe.MutexName;
        // Stands in for a copy started as administrator: its mutex's security refuses this launch's token.
        var refuseAll=new MutexSecurity(); refuseAll.SetSecurityDescriptorSddlForm("D:");
        using var elevated=MutexAcl.Create(false,name,out bool created,refuseAll); Assert.True(created);
        Assert.True(EntryPoint.TryOpen(_root,out var second,out string? problem),$"The second launch stopped with: {problem}");
        using(second)
        {
            Assert.False(second.IsOwner);
            Assert.Equal(EntryPoint.DifferentPermissionsMessage,EntryPoint.RestoreOrExplain(second));
        }
    }
    [Fact]
    public void SecondLaunchRefusedByTheRestorePipeExplainsInsteadOfThrowing()
    {
        using var owner=new SingleInstanceCoordinator(_root); Assert.True(owner.IsOwner);
        // Stands in for the owner's restore pipe when the two copies run with different permissions: Windows refuses the connection.
        var refuseAll=new PipeSecurity(); refuseAll.SetSecurityDescriptorSddlForm("D:");
        using var refusing=NamedPipeServerStreamAcl.Create(owner.PipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.None,0,0,refuseAll);
        using var second=OnOwnThread(()=>new SingleInstanceCoordinator(_root)); Assert.False(second.IsOwner);
        Assert.Equal(EntryPoint.DifferentPermissionsMessage,EntryPoint.RestoreOrExplain(second));
    }
    [Fact]
    public void ALingeringSecondLaunchCannotKeepTheAppFromOpeningAfterTheOwnerExits()
    {
        var owner=new SingleInstanceCoordinator(_root); Assert.True(owner.IsOwner);
        // A second launch's "already starting" box stays open, so its process keeps a handle to the instance mutex.
        using var lingering=OnOwnThread(()=>new SingleInstanceCoordinator(_root)); Assert.False(lingering.IsOwner);
        owner.Dispose(); // No listener was started, so Dispose is the whole owner exit here.
        Assert.True(OnOwnThread(()=> { using var next=new SingleInstanceCoordinator(_root); return next.IsOwner; }),
            "The next launch could not take ownership after the owner exited.");
    }
    [Fact]
    public void ACrashedOwnerCannotKeepTheAppFromOpening()
    {
        string name; using(var probe=new SingleInstanceCoordinator(_root)) name=probe.MutexName;
        // The owner's thread ends without releasing (a crash) while an open handle keeps the named mutex alive.
        bool created=false;
        using var crashed=OnOwnThread(()=>new Mutex(true,name,out created)); Assert.True(created);
        Assert.True(OnOwnThread(()=> { using var next=new SingleInstanceCoordinator(_root); return next.IsOwner; }),
            "The next launch could not take over an abandoned instance.");
    }
    [Fact]
    public void DisposingTheInstanceCoordinatorTwiceIsHarmless()
    {
        var owner=new SingleInstanceCoordinator(_root); Assert.True(owner.IsOwner);
        owner.Dispose(); owner.Dispose();
        Assert.True(OnOwnThread(()=> { using var next=new SingleInstanceCoordinator(_root); return next.IsOwner; }));
    }
    [Fact]
    public async Task AnAliasOfTheOwnersDataFolderCannotStartASecondOwner()
    {
        string library=Path.Combine(_root,"library"),alias=Path.Combine(_root,"alias"); Directory.CreateDirectory(library);
        await CreateJunctionAsync(alias,library);
        try
        {
            using var owner=new SingleInstanceCoordinator(library); Assert.True(owner.IsOwner);
            // GetFullPath leaves "\\?\" text as it is and does not follow junctions, so path text alone would give each spelling its own mutex.
            foreach(string spelling in new[]{@"\\?\"+library,alias,library.ToUpperInvariant()})
                Assert.False(OnOwnThread(()=> { using var second=new SingleInstanceCoordinator(spelling); return second.IsOwner; }),
                    "Another spelling of the owner's data folder became a second owner.");
        }
        finally { if(Directory.Exists(alias)) Directory.Delete(alias,false); }
    }
    [Fact]
    public void ALaunchThatCannotTakeTheLibraryLockIsNotTheOwnerAndLeavesNoPhantomOwner()
    {
        Directory.CreateDirectory(_root);
        // Stands in for an owner the mutex cannot see: a spelling no canonical form unifies (\\localhost\C$) or another sign-in session.
        var other=new FileStream(Path.Combine(_root,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        using var blocked=new SingleInstanceCoordinator(_root);
        try { Assert.False(blocked.IsOwner,"A launch became the owner while another process held the library lock."); }
        finally { other.Dispose(); }
        // blocked is still alive on this thread, so a mutex it kept would refuse this next launch.
        Assert.True(OnOwnThread(()=> { using var next=new SingleInstanceCoordinator(_root); return next.IsOwner; }),
            "The refused launch kept the instance mutex, so the next launch could not open.");
    }
    [Fact]
    public Task RestoreListenerSurvivesAFailedRestoreAndStopsCleanly()=>OnDispatcherAsync(async ()=>
    {
        using var owner=new SingleInstanceCoordinator(_root);
        int calls=0; var restored=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first restore fails like Window.Show() on a closing window; later launches must still be served.
        owner.Listen(_=>
        {
            if(Interlocked.Increment(ref calls)==1) throw new InvalidOperationException("Synthetic restore failure.");
            restored.TrySetResult(); return Task.CompletedTask;
        });
        using var second=await Task.Run(()=>new SingleInstanceCoordinator(_root));
        Assert.True(await second.RestoreExistingAsync(default));
        Assert.True(await second.RestoreExistingAsync(default)); await restored.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(typeof(InvalidOperationException),owner.LastListenerFault); Assert.False(owner.IsListenerFaulted);
        await owner.StopAsync();
    }).WaitAsync(TimeSpan.FromSeconds(10));
    [Fact]
    public Task AnIdlePeerThatKeepsItsHandleCannotEndTheRestoreListener()=>OnDispatcherAsync(async ()=>
    {
        using var owner=new SingleInstanceCoordinator(_root);
        var restored=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Listen(_=> { restored.TrySetResult(); return Task.CompletedTask; });
        // A same-user peer connects, never writes and keeps its handle past the listener's 2 s read deadline.
        using(var idle=new NamedPipeClientStream(".",owner.PipeName,PipeDirection.Out,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly))
        {
            await idle.ConnectAsync(1500); await Task.Delay(TimeSpan.FromSeconds(2.5));
            using var second=await Task.Run(()=>new SingleInstanceCoordinator(_root));
            Assert.True(await second.RestoreExistingAsync(default)); await restored.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Null(owner.LastListenerFault);
        await owner.StopAsync();
    }).WaitAsync(TimeSpan.FromSeconds(10));
    [Fact]
    public Task RestoreListenerRetriesWhileItsPipeCannotBeCreated()=>OnDispatcherAsync(async ()=>
    {
        using var owner=new SingleInstanceCoordinator(_root);
        var restored=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Another instance briefly holds the pipe name, so the listener's first-instance creation is refused.
        using(new NamedPipeServerStream(owner.PipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly))
        {
            owner.Listen(_=> { restored.TrySetResult(); return Task.CompletedTask; });
            await Task.Delay(TimeSpan.FromMilliseconds(600));
        }
        using var second=await Task.Run(()=>new SingleInstanceCoordinator(_root));
        Assert.True(await second.RestoreExistingAsync(default)); await restored.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(owner.LastListenerFault); Assert.False(owner.IsListenerFaulted);
        await owner.StopAsync();
    }).WaitAsync(TimeSpan.FromSeconds(10));
    [Fact]
    public Task APersistentListenerFaultStopsRetryingAndIsReportedWithoutFailingExit()=>OnDispatcherAsync(async ()=>
    {
        using var owner=new SingleInstanceCoordinator(_root);
        using var squatter=new NamedPipeServerStream(owner.PipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var started=DateTime.UtcNow;
        owner.Listen(_=>Task.CompletedTask);
        while(!owner.IsListenerFaulted)
        { if(DateTime.UtcNow-started>TimeSpan.FromSeconds(12)) throw new TimeoutException("The listener kept retrying a persistent fault."); await Task.Delay(50); }
        // Bounded: the retry pause keeps the consecutive-fault cap from spinning.
        Assert.True(DateTime.UtcNow-started>=TimeSpan.FromSeconds(3));
        Assert.NotNull(owner.LastListenerFault);
        await owner.StopAsync();
    }).WaitAsync(TimeSpan.FromSeconds(15));
    [Fact]
    public Task IsolatedSamplePreviewCannotClaimAGlobalDesktopShortcut()=>OnDispatcherAsync(async ()=>
    {
        await using var vm=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new SyntheticMonitor());await vm.InitializeAsync();
        bool asked=false;vm.AttachGlobalPanicShortcut(_=>asked=true);Assert.False(asked);
        Assert.Equal("Panic stop",vm.PanicLabel);Assert.Contains("do not register global shortcuts",vm.PanicShortcutStatus);
        Assert.Equal(16,vm.AccountCount);
    }).WaitAsync(TimeSpan.FromSeconds(10));
    [Fact]
    public Task InstanceTitleDistinguishesPersonalSampleAndOtherLibrariesWithinTrayLimit()=>OnDispatcherAsync(async ()=>
    {
        // Personal, sample and other-folder managers can run side by side, so each tray icon and window says which one it is.
        Assert.Equal("Personal",StartupOptions.DescribeInstance(StartupOptions.DefaultRoot,false));
        Assert.Equal("Personal",StartupOptions.DescribeInstance(StartupOptions.DefaultRoot+Path.DirectorySeparatorChar,false));
        Assert.Equal("Sample preview",StartupOptions.DescribeInstance(_root,true));
        string other=StartupOptions.DescribeInstance(_root,false);
        Assert.Contains(Path.GetFileName(_root),other); Assert.NotEqual("Personal",other);
        // A separate folder that happens to be named Personal is not the personal library.
        Assert.Equal("Library Personal",StartupOptions.DescribeInstance(Path.Combine(_root,"Personal"),false));
        string personal=Path.Combine(_root,"personal-stand-in"); Directory.CreateDirectory(personal);
        Assert.Equal("Personal",StartupOptions.DescribeInstance(personal.ToUpperInvariant()+"\\",false,personal));
        await using(var sample=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new SyntheticMonitor()))
            Assert.Equal("Zenith Sessions · Sample preview",sample.InstanceTitle);
        await using var library=new ShellViewModel(Path.Combine(_root,new string('n',200)),false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());
        Assert.InRange(library.InstanceTitle.Length,1,127); Assert.StartsWith("Zenith Sessions · Library nnn",library.InstanceTitle);
        // The real tray icon refuses text over its limit, which would stop the window from opening.
        using var tray=new System.Windows.Forms.NotifyIcon { Text=library.InstanceTitle };
        // Taskbar and Alt-Tab entries use the window title, so it carries the same name as the tray icon after the open page.
        Assert.Equal("Overview · "+library.InstanceTitle,library.WindowTitle);
        using var xaml=typeof(ViewModelTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        Assert.Equal("{Binding WindowTitle}",(string?)System.Xml.Linq.XDocument.Load(xaml).Root!.Attribute("Title"));
    }).WaitAsync(TimeSpan.FromSeconds(10));
    [Fact]
    public Task FavoritesAndNotesRoundTripThroughTheActualMetadataCommands()=>CheckAsync(async vm=>
    {
        await vm.SaveAccountAsync(AccountProfile.Create("Source") with { Notes="Synthetic note\nSecond line" });
        vm.ToggleFavorite.Execute(null); await vm.ToggleFavorite.Execution; Assert.True(vm.SelectedAccount!.Profile.IsFavorite);
        string path=Path.Combine(_root,"metadata.json"); await vm.ExportAsync(path);
        string json=await File.ReadAllTextAsync(path); Assert.Contains("\"Version\":4",json); Assert.DoesNotContain("AccessToken",json);
        // A row already in the library (same name and username) is skipped on import, so rename the source first.
        await vm.SaveAccountAsync(vm.SelectedAccount!.Profile with { Label="Renamed source" });
        await vm.ImportAsync(path); Assert.Equal(2,vm.AccountCount);
        Assert.All(vm.Accounts,row=> { Assert.True(row.Profile.IsFavorite);Assert.Equal("Synthetic note\nSecond line",row.Profile.Notes);Assert.Equal("Not linked",row.Identity); });
        Assert.Equal(2,vm.Accounts.Select(a=>a.Profile.Id).Distinct().Count());
    });
    [Fact]
    public Task SecondRemovalWhileTheFirstIsPendingDoesNotReportAConflict()=>CheckAsync(async vm=>
    {
        await vm.SaveAccountAsync(AccountProfile.Create("Alt"));
        var row=vm.SelectedAccount!;
        var first=vm.DeleteAccountsAsync([row]); var second=vm.DeleteAccountsAsync([row]);
        Assert.True(vm.IsRemovalPending(row)); Assert.Contains("already being removed",vm.Notice);
        await first;
        Assert.Null(await Record.ExceptionAsync(()=>second));
        Assert.DoesNotContain("changed elsewhere",vm.Notice); Assert.Contains("Removed 1 profile",vm.Notice);
        Assert.DoesNotContain(vm.Accounts,a=>a.Profile.Id==row.Profile.Id);
        // The in-flight mark is released once the removal finishes, so a later removal is not refused.
        Assert.False(vm.IsRemovalPending(row));
        // A removal that faults (here the stale row no longer matches the library) also releases its mark.
        Assert.NotNull(await Record.ExceptionAsync(()=>vm.DeleteAccountsAsync([row])));
        Assert.False(vm.IsRemovalPending(row));
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
        // The export escapes '<' as the six-byte \u003C, so 750 profiles of 4,000 such characters make at least
        // 18 MB of JSON from about 3 MB of stored notes. Storing 18 MB of plain notes made this test time out under load.
        using var repository=new AccountRepository(_root); await repository.InitializeAsync(default);
        await repository.CreateManyAsync(Enumerable.Range(0,750).Select(i=>AccountProfile.Create("Synthetic "+i) with { Notes=new string('<',4000) }).ToArray(),default);
        string path=Path.Combine(_root,"preserved-export.json");await File.WriteAllTextAsync(path,"previous export");
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>vm.ExportAsync(path));
        Assert.Contains("exceeds 16 MiB",error.Message);
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
    public async Task TestModesRejectAnAliasOfThePersonalLibrary()
    {
        // A temporary folder stands in for %LOCALAPPDATA%\ZenithSessions\Personal.
        string personal=Path.Combine(_root,"personal"),alias=Path.Combine(_root,"alias"),samples=Path.Combine(_root,"samples");
        Directory.CreateDirectory(personal);
        Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--data-root",@"\\?\"+personal,"--sample-profiles"],personal));
        Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--data-root",@"\\?\"+samples],personal));
        await CreateJunctionAsync(alias,personal);
        try
        {
            Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--data-root",alias,"--sample-profiles"],personal));
            Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--data-root",alias,"--self-check"],personal));
            Assert.Throws<ArgumentException>(()=>StartupOptions.Parse(["--data-root",personal.ToUpperInvariant()+"\\","--sample-profiles"],personal));
        }
        finally { if(Directory.Exists(alias)) Directory.Delete(alias,false); }
        var options=StartupOptions.Parse(["--data-root",samples,"--sample-profiles"],personal);
        Assert.Equal(samples,options.DataRoot); Assert.True(options.SampleProfiles);
        Assert.Equal(personal,StartupOptions.Parse([],personal).DataRoot);
    }
    [Fact]
    public void AuthorizationRowsExpireWithoutKeepingTokensOrReloadingTheVault()
    {
        var row=new AccountRow(AccountProfile.Create("A")); var now=DateTimeOffset.UtcNow;
        row.SetAuthorization("Linked","Verified identity",now.AddSeconds(1)); row.UpdateAuthorizationClock(now);
        Assert.Equal("Linked",row.Identity); row.UpdateAuthorizationClock(now.AddSeconds(2)); Assert.Equal("Expired",row.Identity);
    }
    // The row button decides between sign-in and launch from the cached saved sign-in, never from display wording.
    [Theory]
    [InlineData(AuthorizationHealth.Linked,5,"Launch")]
    [InlineData(AuthorizationHealth.Linked,-5,"Sign in")]
    [InlineData(AuthorizationHealth.SignInRequired,5,"Sign in")]
    [InlineData(AuthorizationHealth.RefreshPending,5,"Sign in")]
    public void AccountPowerFollowsTheSavedSignInHealthAndExpiry(AuthorizationHealth health,int expiresInMinutes,string expected)
    {
        var profile=AccountProfile.Create("A");
        var row=new AccountRow(profile);
        row.SetCachedAuthorization(new AuthorizationMetadata(profile.Id,profile.Id.ToString("N"),RobloxSessionIdentity.Provider,RobloxSessionIdentity.Issuer,"123",123,"linked_name",8,
            health,DateTimeOffset.UtcNow.AddMinutes(expiresInMinutes),DateTimeOffset.UtcNow));
        Assert.Equal(expected,row.PowerLabel); Assert.Equal(expected=="Sign in",row.NeedsSignIn);
        Assert.Equal(expected=="Launch",row.PowerExplanation.Contains("Launch this account's preferred game",StringComparison.Ordinal));
    }
    [Fact]
    public void ASecurityCheckRowStillOffersSignInAndItsButtonIsRefreshedWhenTheWordingChanges()
    {
        var profile=AccountProfile.Create("A");
        var row=new AccountRow(profile);
        row.SetCachedAuthorization(new AuthorizationMetadata(profile.Id,profile.Id.ToString("N"),RobloxSessionIdentity.Provider,RobloxSessionIdentity.Issuer,"123",123,"linked_name",8,
            AuthorizationHealth.SignInRequired,DateTimeOffset.UtcNow.AddMinutes(5),DateTimeOffset.UtcNow));
        var raised=new List<string?>(); row.PropertyChanged+=(_,e)=>raised.Add(e.PropertyName);
        row.SetAuthorization("Security check required","Roblox asked for a human security check.");
        Assert.Equal("Sign in",row.PowerLabel); Assert.True(row.NeedsSignIn);
        Assert.DoesNotContain("Launch this account's preferred game",row.PowerExplanation);
        Assert.Contains(nameof(AccountRow.PowerLabel),raised); Assert.Contains(nameof(AccountRow.PowerExplanation),raised);
    }
    [Fact]
    public void ALinkedRowOffersSignInOnceItsSavedSignInExpiresOnTheClock()
    {
        var profile=AccountProfile.Create("A"); var now=DateTimeOffset.UtcNow;
        var row=new AccountRow(profile);
        row.SetCachedAuthorization(new AuthorizationMetadata(profile.Id,profile.Id.ToString("N"),RobloxSessionIdentity.Provider,RobloxSessionIdentity.Issuer,"123",123,"linked_name",8,
            AuthorizationHealth.Linked,now.AddMinutes(1),now));
        Assert.Equal("Launch",row.PowerLabel);
        row.UpdateAuthorizationClock(now.AddMinutes(2));
        Assert.Equal("Expired",row.Identity); Assert.Equal("Sign in",row.PowerLabel); Assert.True(row.NeedsSignIn);
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
        // Retry is the only button here, so the status says it will not help and where the kept file is.
        Assert.Contains("Retrying will not repair it",vm.CredentialCleanupStatus); Assert.Contains("More options › Open data folder",vm.CredentialCleanupStatus);
    });
    [Fact]
    public Task DamagedVaultCoordinationFileIsReportedAsStorageNotAsADamagedRemovedProfileFile()=>OnDispatcherAsync(async ()=>
    {
        using(var repository=new AccountRepository(_root))
        { await repository.InitializeAsync(default); var removed=await repository.SaveAsync(AccountProfile.Create("Removed"),default); await repository.DeleteAsync(removed.Id,removed.Revision,default); }
        await CleanupRecoveryTests.DamageVaultCoordinationFileAsync(_root);
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor()); await vm.InitializeAsync();
        Assert.Contains("1 unused protected record",vm.CredentialCleanupStatus); Assert.Contains("Check storage access",vm.CredentialCleanupStatus);
        Assert.DoesNotContain("is damaged",vm.CredentialCleanupStatus);
    });
    // A dedicated thread stands in for another process: mutex ownership is per thread, and this thread ends before it returns.
    private static T OnOwnThread<T>(Func<T> create)
    {
        T? value=default; System.Runtime.ExceptionServices.ExceptionDispatchInfo? error=null;
        var thread=new Thread(()=> { try { value=create(); } catch(Exception ex) { error=System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); } });
        thread.Start(); thread.Join(); error?.Throw(); return value!;
    }
    // A real directory junction (no elevation needed) built from literal synthetic paths inside this test's folder.
    private static async Task CreateJunctionAsync(string link,string target)
    {
        static string Quote(string path)=>"'"+path.Replace("'","''")+"'";
        string script="$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path "+Quote(link)+" -Target "+Quote(target)+" | Out-Null";
        var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32/WindowsPowerShell/v1.0/powershell.exe")) { UseShellExecute=false,CreateNoWindow=true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var process=System.Diagnostics.Process.Start(start)!;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); Assert.Equal(0,process.ExitCode); }
        finally { if(!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
    }
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
