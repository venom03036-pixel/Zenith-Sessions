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
    [Theory]
    [InlineData("tile columns","sessions-layout","Sessions")]
    [InlineData("window size","client-window-size","Sessions")]
    [InlineData("cpu priority","client-priority","Sessions")]
    [InlineData("previews","client-previews","Sessions")]
    [InlineData("mute","client-sound","Sessions")]
    [InlineData("diagnostics filter","diagnostics-filters","Diagnostics")]
    public Task SettingsSearchOpensThePageOfAPreferenceElsewhereAndLeavesTheDraftUnchanged(string query,string id,string page)=>OnUi(async()=>
    {
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor());
        await shell.InitializeAsync();shell.Navigate.Execute("Settings");
        shell.SpareRam="unfinished draft";shell.TileColumns="7";
        string saveStatus=shell.SaveStatus,width=shell.ClientContentWidth,filter=shell.DiagnosticFilter;
        bool previews=shell.IsClientPreviewMode,warnings=shell.ShowWarnings,muteArrivals=shell.MuteArrivingClients;
        string? pageWhenAsked=null;shell.SettingsSearchNavigationRequested+=_=>pageWhenAsked=shell.Tab;
        shell.SettingsSearchQuery=query;
        var entry=Assert.Single(shell.SettingsSearchResults.Where(e=>e.Id==id));
        shell.OpenSettingSearchResult.Execute(entry);
        // The page is already open when the window is asked to open the section and focus the control.
        Assert.Equal(page,pageWhenAsked);Assert.Equal(page,shell.Tab);
        shell.ReportSettingSearchNavigation(entry,false);
        Assert.Contains(entry.Title,shell.Notice);Assert.Contains("did not change",shell.Notice);
        Assert.Equal("unfinished draft",shell.SpareRam);Assert.Equal("7",shell.TileColumns);Assert.Equal(saveStatus,shell.SaveStatus);
        Assert.Equal(width,shell.ClientContentWidth);Assert.Equal(filter,shell.DiagnosticFilter);Assert.Equal(warnings,shell.ShowWarnings);
        Assert.Equal(previews,shell.IsClientPreviewMode);Assert.False(shell.IsChangingClientPriority);Assert.Equal(0,shell.PendingPriorityRestoreCount);
        Assert.Equal(muteArrivals,shell.MuteArrivingClients);Assert.False(shell.IsChangingClientSound);Assert.Equal(0,shell.PendingSoundRestoreCount);
        Assert.Empty(shell.Launches);Assert.False(shell.IsAuthorizing);
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
    [Fact]
    public void EveryPreferenceOutsideSettingsHasAProductionSectionAndControlOnItsPage()
    {
        using var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml");Assert.NotNull(source);
        var document=XDocument.Load(source!);XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name)=>Assert.Single(document.Descendants().Where(e=>(string?)e.Attribute(x+"Name")==name));
        var pages=new Dictionary<string,XElement>{["Sessions"]=Named("SessionsPage"),["Diagnostics"]=Named("DiagnosticsPage")};
        foreach(var target in Enum.GetValues<SettingsSearchTarget>())
        {
            string page=SettingsSearchCatalog.PageOf(target);
            var destination=SettingsSearchDestinations.For(target);
            if(page=="Settings"){Assert.Null(destination);continue;}
            Assert.NotNull(destination);
            var focus=Named(destination!.Focus);
            Assert.Contains(pages[page],focus.AncestorsAndSelf());
            if(destination.Section is { } sectionName)
            {
                var section=Named(sectionName);Assert.Equal("Expander",section.Name.LocalName);
                Assert.Contains(section,focus.AncestorsAndSelf());
            }
            // Search lands on a field, a section or the view-mode button, never on a button that starts work.
            if(focus.Name.LocalName=="Button")Assert.Equal("{Binding ShowClientPreviewMode}",(string?)focus.Attribute("Command"));
        }
        Assert.Equal("Expander",Named(SettingsSearchDestinations.For(SettingsSearchTarget.ClientPriority)!.Focus).Name.LocalName);
        // Client sound lands on the saved "Mute clients when they appear" option inside its section, never on Mute or Unmute.
        var sound=SettingsSearchDestinations.For(SettingsSearchTarget.ClientSound)!;
        Assert.Equal("ClientSoundTools",sound.Section);
        var option=Named(sound.Focus);
        Assert.Equal("CheckBox",option.Name.LocalName);
        Assert.Equal("{Binding MuteArrivingClients}",(string?)option.Attribute("IsChecked"));
    }
}
