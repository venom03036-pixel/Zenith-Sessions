using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using MacroSuite.App.Themes;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

[Collection(WpfProductionResourceCollection.Name)]
public sealed class JoinWorkspacePresentationTests
{
    private static readonly XNamespace Ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation",X="http://schemas.microsoft.com/winfx/2006/xaml";
    public static IEnumerable<object[]> Cases()
    {foreach(string theme in new[]{"graphite-mint","midnight","amber-night","ocean-blue","violet-dusk","custom-light"})foreach(double width in new[]{728d,1088d})yield return new object[]{theme,width};}

    [Fact]
    public void ProductionContainsExactlyOneJoiningSurfaceAndKeepsMacroAssignmentsOnAutomation()
    {
        var source=Source();var join=Page(source,"JoinGamePage");var automation=Page(source,"AutomationPage");var sessions=Page(source,"SessionsPage");
        Assert.Equal("{Binding IsJoinGame, Converter={StaticResource VisibleIf}}",(string?)join.Attribute("Visibility"));
        Assert.Equal("{Binding IsAutomation, Converter={StaticResource VisibleIf}}",(string?)automation.Attribute("Visibility"));
        Assert.Single(source.Descendants(Ui+"TextBox").Where(element=>(string?)element.Attribute(X+"Name")=="DestinationInput"));
        Assert.Single(join.Descendants(Ui+"TextBox").Where(element=>(string?)element.Attribute(X+"Name")=="DestinationInput"));
        Assert.Contains(join.Descendants(),element=>(string?)element.Attribute("DataContext")=="{Binding DestinationFavorites}");
        Assert.Contains(join.Descendants(),element=>(string?)element.Attribute("DataContext")=="{Binding RecipientBundles}");
        foreach(string handler in new[]{"StartNativeTrialClick","CopyLaunchEvidence","SaveLaunchEvidence","DeleteFavoriteDestination","ReplaceRecipientBundleMembers"})
            Assert.Single(join.Descendants(Ui+"Button").Where(button=>(string?)button.Attribute("Click")==handler));
        var controls = join.Descendants(Ui + "Button").ToList();
        foreach(string label in new[]{"Copy requested link","Queue checked accounts","Browse public servers"})
            Assert.Equal("{Binding CanUseDestination}",(string?)controls.Single(button=>(string?)button.Attribute("Content")==label).Attribute("IsEnabled"));
        Assert.Contains(controls,button=>(string?)button.Attribute("Content")=="Cancel / discard draft" && (string?)button.Attribute("Command")=="{Binding Cancel}");
        Assert.Contains(join.Descendants(Ui+"TextBlock"),block=>(string?)block.Attribute("Visibility")=="{Binding HasDestinationDraft, Converter={StaticResource VisibleIf}}");
        int review = controls.FindIndex(button => (string?)button.Attribute("Content") == "Review selected-account trial");
        Assert.InRange(review, 0, controls.FindIndex(button => (string?)button.Attribute("Content") == "Load game details") - 1);
        Assert.Contains(sessions.Descendants(Ui + "Button"), button => (string?)button.Attribute("Click") == "ReviewReportedServerClick");
        Assert.Contains(source.Descendants(Ui + "Button"), button => (string?)button.Attribute("Content") == "Choose server…"
            && (string?)button.Attribute("Command") == "{Binding OpenJoinGameForSelectedAccount}");
        Assert.Contains(join.Descendants(Ui+"Button"),button=>(string?)button.Attribute("Command")=="{Binding ShowEvidenceProcess}");
        foreach(string context in new[]{"{Binding MacroHostCatalog}","{Binding MacroAssignments}"})
        {Assert.Single(automation.Descendants().Where(element=>(string?)element.Attribute("DataContext")==context));Assert.DoesNotContain(join.Descendants(),element=>(string?)element.Attribute("DataContext")==context);}
        Assert.DoesNotContain(automation.Descendants(),element=>(string?)element.Attribute(X+"Name")=="DestinationInput");
        Assert.Contains(sessions.Descendants(Ui+"Button"),button=>(string?)button.Attribute("CommandParameter")=="JoinGame");
        var side=source.Descendants(Ui+"Button").Single(button=>(string?)button.Attribute("AutomationProperties.Name")=="Sessions");
        Assert.Equal("{Binding IsSessionWorkspace}",(string?)side.Attribute("Tag"));
        var title=join.Descendants(Ui+"TextBlock").Single(element=>(string?)element.Attribute("Text")=="{Binding EmptyTitle}");
        Assert.Equal("{Binding ShowPlaceholder, Converter={StaticResource VisibleIf}}",(string?)title.Parent!.Attribute("Visibility"));
        Assert.Contains(title.Parent!.Elements(),element=>(string?)element.Attribute("Text")=="{Binding EmptyDescription}");
        Assert.DoesNotContain(source.Descendants(Ui+"TextBlock"),block=>((string?)block.Attribute("Text"))?.Contains("choose Use link",StringComparison.Ordinal)==true);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task MovedProductionPageKeepsNavigationInputGalleryAndDistantActionsReachable(string theme,double width)=>SearchTest.OnUi(async()=>
    {
        await using var fixture=new Fixture(theme);var view=fixture.View;Layout(view,width,480);
        foreach(string label in new[]{"← Back to sessions","Overview","Search / Use"})
        {var button=FindButton(view,label);Reachable(view,button);Assert.True(button.ActualHeight>=36);}
        var back=FindButton(view,"← Back to sessions");Assert.Same(fixture.Model.Navigate,back.Command);Assert.Equal("Sessions",back.CommandParameter);
        var overview=FindButton(view,"Overview");Assert.Same(fixture.Model.Navigate,overview.Command);Assert.Equal("Overview",overview.CommandParameter);
        var input=Visual(view).OfType<TextBox>().Single(box=>box.Name=="DestinationInput");Assert.Equal(4096,input.MaxLength);Assert.Contains("Game name",AutomationProperties.GetName(input));
        Assert.Same(fixture.Model.DestinationSearch.SearchOrUse,FindButton(view,"Search / Use").Command);
        // Step 1: the advanced trial lives in an expander; the game panel is docked on the right.
        var trial=Visual(view).OfType<Expander>().Single(expander=>Equals(expander.Header,"Native account trial · advanced"));trial.IsExpanded=true;Layout(view,width,480);
        foreach(string label in new[]{"Review selected-account trial","Load game details","Open on Roblox.com","Copy link"})
        {
            var button=FindButton(view,label);ScrollTo(view,button,width,480);Reachable(view,button);Assert.True(button.ActualHeight>=36);
        }
        // Step 3 holds the launch actions; the step switch is presentation only.
        fixture.Model.ShowJoinStep.Execute("3");Layout(view,width,480);Assert.True(fixture.Model.IsJoinReadyStep);
        foreach(string label in new[]{"Queue checked accounts","Queue bundle for this destination"})
        {
            var button=FindButton(view,label);ScrollTo(view,button,width,480);Reachable(view,button);Assert.True(button.ActualHeight>=36);
        }
        fixture.Model.ShowJoinStep.Execute("1");Layout(view,width,480);Assert.True(fixture.Model.IsJoinFindStep);
        var galleryTitle=Visual(view).OfType<TextBlock>().Single(block=>BindingOperations.GetBinding(block,TextBlock.TextProperty)?.Path?.Path=="EmptyTitle");
        var placeholder=(StackPanel)galleryTitle.Parent;Assert.True(fixture.Model.GameDetails.Gallery.ShowPlaceholder);Assert.Equal(Visibility.Visible,placeholder.Visibility);
        Assert.Equal(fixture.Model.GameDetails.Gallery.EmptyTitle,galleryTitle.Text);Assert.False(string.IsNullOrWhiteSpace(galleryTitle.Text));
        Assert.Null(fixture.Model.GameDetails.Gallery.Image);Assert.Equal(0,fixture.Public.GameRequests);Assert.Empty(fixture.Public.Queries);Assert.Equal(0,fixture.Capture.Calls);
        view.ScrollToTop();Layout(view,width,480);var render=new RenderTargetBitmap((int)width,480,96,96,PixelFormats.Pbgra32);render.Render(view);Assert.Equal(480,render.PixelHeight);
    });

    [Fact]
    public void GamePanelOffersOpenOnRobloxAndCopyLink()
    {
        var join=Page(Source(),"JoinGamePage");var buttons=join.Descendants(Ui+"Button").ToList();
        var open=buttons.Single(button=>(string?)button.Attribute("Content")=="Open on Roblox.com");
        Assert.Equal("OpenGamePageClick",(string?)open.Attribute("Click"));Assert.Equal("{Binding CanUseDestination}",(string?)open.Attribute("IsEnabled"));
        var copy=buttons.Single(button=>(string?)button.Attribute("Content")=="Copy link");
        Assert.Equal("CopyGamePageLinkClick",(string?)copy.Attribute("Click"));Assert.Equal("{Binding CanUseDestination}",(string?)copy.Attribute("IsEnabled"));
        var results=join.Descendants(Ui+"ItemsControl").Single(element=>(string?)element.Attribute(X+"Name")=="DestinationSearchResults");
        Assert.Equal("{Binding VisibleResults}",(string?)results.Attribute("ItemsSource"));
        Assert.DoesNotContain(buttons,button=>(string?)button.Attribute("Content")=="Use selected experience");
        Assert.Contains(buttons,button=>(string?)button.Attribute("Content")=="Show more" && (string?)button.Attribute("Command")=="{Binding ShowMore}");
    }

    [Fact]
    public void FavoritesRowShowsSavedAndRobloxChipsAndManageLink()
    {
        var join=Page(Source(),"JoinGamePage");
        var saved=join.Descendants(Ui+"ItemsControl").Single(element=>(string?)element.Attribute(X+"Name")=="SavedFavoriteChips");
        Assert.Equal("{Binding Favorites}",(string?)saved.Attribute("ItemsSource"));Assert.Equal("{Binding DestinationFavorites}",(string?)saved.Attribute("DataContext"));
        Assert.Contains(saved.Descendants(Ui+"Button"),button=>((string?)button.Attribute("Command"))?.Contains("UseFavorite",StringComparison.Ordinal)==true);
        var roblox=join.Descendants(Ui+"ItemsControl").Single(element=>(string?)element.Attribute(X+"Name")=="RobloxFavoriteChips");
        Assert.Equal("{Binding Chips}",(string?)roblox.Attribute("ItemsSource"));
        Assert.Contains(roblox.Ancestors(),ancestor=>(string?)ancestor.Attribute("DataContext")=="{Binding RobloxFavorites}");
        Assert.Contains(join.Descendants(Ui+"Button"),button=>(string?)button.Attribute("Command")=="{Binding ShowAll}");
        var manage=join.Descendants(Ui+"Expander").Single(element=>(string?)element.Attribute("Header")=="Manage favorites");
        Assert.Single(manage.Descendants(Ui+"Button").Where(button=>(string?)button.Attribute("Click")=="DeleteFavoriteDestination"));
        Assert.Single(manage.Descendants(Ui+"ListView"));
        Assert.Contains(join.Descendants(Ui+"TextBlock"),block=>(string?)block.Attribute("Text")=="Recently played" || (string?)block.Attribute("Text")=="{Binding HeaderLabel}");
    }

    [Fact]
    public void RecentlyPlayedChipsAreListedUnderFavorites()
    {
        var join=Page(Source(),"JoinGamePage");
        var recent=join.Descendants(Ui+"ItemsControl").Single(element=>(string?)element.Attribute(X+"Name")=="RecentlyPlayedChips");
        Assert.Equal("{Binding Chips}",(string?)recent.Attribute("ItemsSource"));
        Assert.Contains(recent.Ancestors(),ancestor=>(string?)ancestor.Attribute("DataContext")=="{Binding RecentlyPlayed}");
        Assert.Contains(recent.Ancestors().SelectMany(ancestor=>ancestor.Elements(Ui+"TextBlock")),block=>(string?)block.Attribute("Text")=="Recently played"
            && ((string?)block.Attribute("ToolTip"))?.Contains("Not verified",StringComparison.Ordinal)==true);
        Assert.Contains(recent.Descendants(Ui+"Button"),button=>(string?)button.Attribute("Command")=="{Binding DataContext.Use, RelativeSource={RelativeSource AncestorType=ItemsControl}}");
    }

    [Fact]
    public void SessionsPanelAndRowMenuOfferJoinSameGameAndServer()
    {
        var sessions=Page(Source(),"SessionsPage");var buttons=sessions.Descendants(Ui+"Button").ToList();
        var game=buttons.Single(button=>(string?)button.Attribute("Content")=="Join same game");
        Assert.Equal("JoinSameGameClick",(string?)game.Attribute("Click"));Assert.Equal("{Binding Log.HasReportedDestination}",(string?)game.Attribute("IsEnabled"));
        var server=buttons.Single(button=>(string?)button.Attribute("Content")=="Join same server");
        Assert.Equal("ReviewReportedServerClick",(string?)server.Attribute("Click"));Assert.Equal("{Binding Log.HasReportedDestination}",(string?)server.Attribute("IsEnabled"));
        Assert.Contains("public servers only",(string?)server.Attribute("ToolTip"),StringComparison.Ordinal);
        Assert.Contains(buttons,button=>(string?)button.Attribute("Content")=="Copy reported server link" && (string?)button.Attribute("Click")=="CopyReportedServerLink");
        Assert.DoesNotContain(buttons,button=>(string?)button.Attribute("Content")=="Review reported server");
        var items=sessions.Descendants(Ui+"MenuItem").ToList();
        Assert.Contains(items,item=>(string?)item.Attribute("Header")=="Join same game" && (string?)item.Attribute("Click")=="JoinSameGameClick");
        Assert.Contains(items,item=>(string?)item.Attribute("Header")=="Join same server" && (string?)item.Attribute("Click")=="ReviewReportedServerClick");
    }

    [Fact]
    public void PrivateServerTabChecksLinksAndListsSavedServers()
    {
        var join=Page(Source(),"JoinGamePage");
        var tab=join.Descendants(Ui+"Border").Single(element=>(string?)element.Attribute("DataContext")=="{Binding DataContext.ShareLink, RelativeSource={RelativeSource AncestorType=Window}}");
        Assert.Single(tab.Descendants(Ui+"TextBox").Where(box=>((string?)box.Attribute("Text"))?.StartsWith("{Binding LinkInput",StringComparison.Ordinal)==true));
        var buttons=tab.Descendants(Ui+"Button").ToList();
        Assert.Equal("{Binding Check}",(string?)buttons.Single(button=>(string?)button.Attribute("Content")=="Check link").Attribute("Command"));
        Assert.Equal("{Binding SaveAsFavorite}",(string?)buttons.Single(button=>(string?)button.Attribute("Content")=="Save as favorite").Attribute("Command"));
        Assert.Equal("DeletePrivateServer",(string?)buttons.Single(button=>(string?)button.Attribute("Content")=="Remove selected").Attribute("Click"));
        Assert.Equal("{Binding CanJoinPrivate}",(string?)buttons.Single(button=>(string?)button.Attribute("Content")=="Join private server").Attribute("IsEnabled"));
        Assert.Contains(tab.Descendants(Ui+"ListView"),list=>(string?)list.Attribute("ItemsSource")=="{Binding Saved}" && (string?)list.Attribute("SelectedItem")=="{Binding SelectedSaved}");
        Assert.Contains(tab.Descendants(Ui+"TextBlock"),block=>(string?)block.Attribute("Text")=="Anyone with this link can join, so treat it like a password.");
        Assert.Contains(tab.Descendants(Ui+"TextBlock"),block=>(string?)block.Attribute("Text")=="{Binding JoinNote}");
        Assert.DoesNotContain(tab.Descendants(Ui+"TextBlock"),block=>((string?)block.Attribute("Text"))?.Contains("isn’t connected yet",StringComparison.Ordinal)==true);
    }

    [Theory]
    [InlineData("graphite-mint",960,640)]
    [InlineData("graphite-mint",1320,840)]
    [InlineData("midnight",960,640)]
    [InlineData("midnight",1320,840)]
    public Task RendersJoinPageSnapshotsForReview(string theme,int width,int height)=>SearchTest.OnUi(async()=>
    {
        // Review evidence: the page beside the 200 px sidebar and under the 44 px title bar / 32 px status bar, saved under the
        // repository's artifacts/game-links; a build with no repository above it keeps them in the fixture's own Temp root.
        await using var fixture=new Fixture(theme);var view=fixture.View;int pageWidth=width-200,pageHeight=height-76;
        Layout(view,pageWidth,pageHeight);
        var render=new RenderTargetBitmap(pageWidth,pageHeight,96,96,PixelFormats.Pbgra32);render.Render(view);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(render));
        string root=ReviewSnapshotFolder(AppContext.BaseDirectory)??Path.Combine(fixture.Root,"game-links");
        Directory.CreateDirectory(root);string path=Path.Combine(root,$"join-{theme}-{width}x{height}.png");
        await using(var file=File.Create(path)) encoder.Save(file);
        Assert.True(new FileInfo(path).Length>1024);Assert.Equal(pageHeight,render.PixelHeight);
    });

    [Fact]
    public void ReviewSnapshotFolderFollowsTheRepositoryNotTheOutputDepth()
    {
        string temp=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        string owned=Path.Combine(temp,"zenith-snapshot-root-"+Guid.NewGuid().ToString("N"));
        try
        {
            string repo=Path.Combine(owned,"repo");
            Directory.CreateDirectory(Path.Combine(repo,"src","Zenith.Sessions.App"));
            File.WriteAllBytes(Path.Combine(repo,"src","Zenith.Sessions.App","MainWindow.xaml"),[]);
            // The manager check script builds the tests into artifacts\sessions-tests-bin\, two levels below the repository.
            string runnerOutput=Path.Combine(repo,"artifacts","sessions-tests-bin")+Path.DirectorySeparatorChar;Directory.CreateDirectory(runnerOutput);
            Assert.Equal(Path.Combine(repo,"artifacts","game-links"),ReviewSnapshotFolder(runnerOutput));
            // The default layout is tests\Zenith.Sessions.Tests\bin\<Config>\<tfm>\, five levels below it.
            string defaultOutput=Path.Combine(repo,"tests","Zenith.Sessions.Tests","bin","Release","net10.0-windows10.0.19041.0")+Path.DirectorySeparatorChar;Directory.CreateDirectory(defaultOutput);
            Assert.Equal(Path.Combine(repo,"artifacts","game-links"),ReviewSnapshotFolder(defaultOutput));
            // No repository above the output: no review folder, so the theory keeps its snapshots in its own Temp root.
            string elsewhere=Path.Combine(owned,"elsewhere","a","b")+Path.DirectorySeparatorChar;Directory.CreateDirectory(elsewhere);
            Assert.Null(ReviewSnapshotFolder(elsewhere));
        }
        finally
        {
            string full=Path.GetFullPath(owned);
            if(Path.GetDirectoryName(full)!=temp||!Path.GetFileName(full).StartsWith("zenith-snapshot-root-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected snapshot-root cleanup path");
            if(Directory.Exists(full))Directory.Delete(full,true);
        }
    }

    /// <summary>
    /// The repository's artifacts\game-links folder for the review snapshots, found by walking up from the test output
    /// (at most 8 levels) to the folder that holds src\Zenith.Sessions.App\MainWindow.xaml, so the default bin\ layout
    /// and the check script's artifacts\sessions-tests-bin\ output both land in the same place. The marker is the file
    /// this project already embeds as ProductionMainWindow.xaml, so renaming it breaks the build first. Null when no
    /// repository is above the output; the caller then keeps the snapshots in its own Temp root.
    /// </summary>
    internal static string? ReviewSnapshotFolder(string baseDirectory)
    {
        string? directory=Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        for(int depth=0;directory is not null&&depth<8;depth++,directory=Path.GetDirectoryName(directory))
            if(File.Exists(Path.Combine(directory,"src","Zenith.Sessions.App","MainWindow.xaml")))return Path.Combine(directory,"artifacts","game-links");
        return null;
    }

    private static XDocument Source(){using var stream=typeof(JoinWorkspacePresentationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;return XDocument.Load(stream);}
    private static XElement Page(XDocument source,string name)=>source.Descendants().Single(element=>(string?)element.Attribute(X+"Name")==name);
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-join-layout-"+Guid.NewGuid().ToString("N"));
        private readonly Window _window;
        public readonly SearchDirectory Public=new(new());
        public readonly NoCapture Capture=new();
        public ShellViewModel Model {get;}
        public ScrollViewer View {get;}
        public string Root=>_root;
        public Fixture(string theme)
        {
            _=Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml",UriKind.Relative));
            var source=Source();var page=new XElement(Page(source,"JoinGamePage"));
            // Loose offscreen composition has no code-behind event connection. Assert the
            // actual handler names above; preserve every visual, binding and named control.
            foreach(var element in page.DescendantsAndSelf())foreach(var attribute in element.Attributes().Where(attribute=>attribute.Name.LocalName=="Click").ToArray())attribute.Remove();
            string[] keys=["Muted","SectionTitle","Panel","AccountItem"];
            var entries=source.Root!.Element(Ui+"Window.Resources")!.Elements().Where(element=>keys.Contains((string?)element.Attribute(X+"Key"))).Select(element=>new XElement(element));
            XNamespace common="clr-namespace:MacroSuite.App.Common;assembly=Zenith.Sessions.App";
            var resources=new XElement(Ui+"ResourceDictionary",new XElement(Ui+"ResourceDictionary.MergedDictionaries",new XElement(Ui+"ResourceDictionary",new XAttribute("Source","/Zenith.Sessions.App;component/Themes/SessionsControls.xaml"))),new XElement(Ui+"BooleanToVisibilityConverter",new XAttribute(X+"Key","VisibleIf")),new XElement(common+"InverseBoolToVisibilityConverter",new XAttribute(X+"Key","HiddenIf")),entries);
            var root=new XElement(Ui+"Window",new XAttribute(XNamespace.Xmlns+"x",X.NamespaceName),new XAttribute(XNamespace.Xmlns+"common",common.NamespaceName),new XElement(Ui+"Window.Resources",resources),page);
            _window=(Window)XamlReader.Parse(root.ToString());
            ThemeManager.Apply(_window.Resources,theme=="custom-light"?"graphite-mint":theme);
            if(theme=="custom-light")foreach(var pair in new Dictionary<string,string>{["AppBackground"]="#FFFFFF",["CardBackground"]="#FAFAFA",["TextPrimary"]="#151515",["TextSecondary"]="#454545",["TextMuted"]="#555555",["SelectionBackground"]="#DDE8EE",["SelectionForeground"]="#151515"})_window.Resources[pair.Key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            Model=new(_root,true,Dispatcher.CurrentDispatcher,new NoMonitor(),publicDirectory:Public,serverDirectory:new SearchServers(),previewProvider:Capture);
            Model.Navigate.Execute("JoinGame");_window.DataContext=Model;View=(ScrollViewer)_window.Content;View.SetResourceReference(Control.ForegroundProperty,"TextPrimary");
        }
        public async ValueTask DisposeAsync()
        {
            _window.Content=null;_window.Close();View.DataContext=null;await Model.DisposeAsync();
            string full=Path.GetFullPath(_root);if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(full).StartsWith("zenith-join-layout-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected fixture cleanup path");
            if(Directory.Exists(full))Directory.Delete(full,true);
        }
    }
    private sealed class NoMonitor:IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)=>throw new InvalidOperationException("No fixture initialization or sampling expected");
        public Task<bool> FocusAsync(SessionObservation value,CancellationToken ct)=>throw new InvalidOperationException("No window operation expected");
        public Task<bool> RequestCloseAsync(SessionObservation value,CancellationToken ct)=>throw new InvalidOperationException("No window operation expected");
    }
    private sealed class NoCapture:IClientPreviewProvider
    {
        public int Calls;
        public Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets,long generation,CancellationToken ct){Calls++;throw new InvalidOperationException("No capture expected");}
        public Task ReleaseAsync(CancellationToken ct)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static Button FindButton(DependencyObject root,string label)=>Visual(root).OfType<Button>().Single(button=>Equals(button.Content,label));
    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Visual(child))yield return nested;}}
    private static void Flush()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    private static void Layout(FrameworkElement view,double width,double height){view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));Flush();view.UpdateLayout();}
    private static void ScrollTo(ScrollViewer view,FrameworkElement element,double width,double height)
    {var content=(FrameworkElement)view.Content;double top=element.TransformToAncestor(content).Transform(new Point()).Y;view.ScrollToVerticalOffset(Math.Max(0,top-50));Layout(view,width,height);}
    private static void Reachable(FrameworkElement root,FrameworkElement item)
    {var bounds=item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));Assert.True(item.ActualWidth>0&&item.ActualHeight>0);Assert.InRange(bounds.Left,0,root.ActualWidth);Assert.InRange(bounds.Right,0,root.ActualWidth+.5);Assert.InRange(bounds.Top,0,root.ActualHeight);Assert.InRange(bounds.Bottom,0,root.ActualHeight+.5);}
}
