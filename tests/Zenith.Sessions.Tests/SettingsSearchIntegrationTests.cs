using System.Reflection;
using System.IO;
using System.Windows.Threading;
using System.Xml.Linq;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task FailedLibraryStartupStillAllowsLocalSettingsRecoveryNavigation()=>OnUi(async()=>
    {
        Directory.CreateDirectory(_root);string broken=Path.Combine(_root,"accounts.db");await File.WriteAllTextAsync(broken,"synthetic invalid database");
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor());await shell.InitializeAsync();
        Assert.False(shell.IsReady);Assert.Equal("Startup needs attention",shell.SaveStatus);shell.Navigate.Execute("Settings");int navigations=0;
        shell.SettingsSearchNavigationRequested+=_=>navigations++;
        shell.SettingsSearchQuery="protected recovery";var result=Assert.Single(shell.SettingsSearchResults.Where(e=>e.Id=="protected-recovery"));
        shell.OpenSettingSearchResult.Execute(result);Assert.Equal(1,navigations);Assert.Equal("Startup needs attention",shell.SaveStatus);
        Assert.Equal("synthetic invalid database",await File.ReadAllTextAsync(broken));Assert.Empty(shell.Launches);Assert.False(shell.IsAuthorizing);
    });
    [Fact]
    public Task SettingsSearchPreservesInvalidDraftsAndDoesNotSaveOrExecuteOperations()=>OnUi(async()=>
    {
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor());
        await shell.InitializeAsync();shell.Navigate.Execute("Settings");shell.SpareRam="unfinished draft";
        string originalStatus=shell.SaveStatus;var performance=shell.SelectedPerformanceProfile;int navigations=0;
        shell.SettingsSearchNavigationRequested+=_=>navigations++;
        shell.SettingsSearchQuery="launch spacing";
        var entry=Assert.Single(shell.SettingsSearchResults.Where(e=>e.Id=="launch-spacing"));
        Assert.Equal(SettingsSearchTarget.CustomTiming,entry.Target);
        shell.OpenSettingSearchResult.Execute(entry);shell.ReportSettingSearchNavigation(entry,true);
        Assert.Equal(1,navigations);Assert.Same(performance,shell.SelectedPerformanceProfile);Assert.Equal("unfinished draft",shell.SpareRam);
        Assert.Equal(originalStatus,shell.SaveStatus);Assert.Contains("did not change",shell.SettingsSearchStatus);
        Assert.Empty(shell.Launches);Assert.False(shell.IsAuthorizing);
        shell.ClearSettingsSearch.Execute(null);Assert.Empty(shell.SettingsSearchResults);Assert.False(shell.HasSettingsSearchQuery);
        Assert.Equal("unfinished draft",shell.SpareRam);Assert.Equal(originalStatus,shell.SaveStatus);
    });
    [Fact]
    public Task SettingsSearchRejectsForeignRowsAndDoesNotRouteWhileLockedOrOnAnotherPage()=>OnUi(async()=>
    {
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor());await shell.InitializeAsync();int navigations=0;
        shell.SettingsSearchNavigationRequested+=_=>navigations++;
        shell.SettingsSearchQuery="RAM";var entry=shell.SettingsSearchResults.First();
        shell.OpenSettingSearchResult.Execute(entry);Assert.Equal(0,navigations);
        shell.Navigate.Execute("Settings");
        var unrelated=SettingsSearchCatalog.Search("theme").First();shell.OpenSettingSearchResult.Execute(unrelated);Assert.Equal(0,navigations);
        shell.OpenSettingSearchResult.Execute(entry);Assert.Equal(1,navigations);
        shell.LockDashboard();Assert.False(shell.HasSettingsSearchQuery);Assert.Empty(shell.SettingsSearchResults);
        shell.OpenSettingSearchResult.Execute(entry);Assert.Equal(1,navigations);
    });
    [Fact]
    public void ProductionSettingsSearchHasReachableAnchorsAndNoPreferenceChangingAction()
    {
        using var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml");Assert.NotNull(source);
        var document=XDocument.Load(source!);XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
        foreach(string name in new[]{"SettingsSearchBox","SettingsSearchList","SettingsAppearanceAnchor","SettingsPerformanceAnchor","SettingsPerformanceProfile",
            "SettingsCustomTiming","SettingsDashboardTiming","SettingsLaunchSpacing","SettingsStabilizationTiming","SettingsPerformanceTransfer","SettingsPerformanceTransferInput",
            "SettingsMemoryAnchor","SettingsSpareRam","SettingsPhysicalPerClient","SettingsCommitPerClient","SettingsMaximumClients",
            "SettingsInstallationsAnchor","SettingsSignInAnchor","SettingsSaveButton","SettingsRecoveryAnchor","SettingsRecoveryButton"})
            Assert.Single(document.Descendants().Where(e=>(string?)e.Attribute(x+"Name")==name));
        var list=document.Descendants().Single(e=>(string?)e.Attribute(x+"Name")=="SettingsSearchList");
        Assert.Contains("OpenSettingSearchResult",list.ToString());Assert.DoesNotContain("ApplyTheme",list.ToString());Assert.DoesNotContain("SaveSettings",list.ToString());
        Assert.Equal("260",(string?)list.Attribute("MaxHeight"));
        var installation=document.Descendants().Single(e=>(string?)e.Attribute(x+"Name")=="SettingsInstallationsAnchor");
        Assert.Equal("True",(string?)installation.Attribute("Focusable"));
    }
}
