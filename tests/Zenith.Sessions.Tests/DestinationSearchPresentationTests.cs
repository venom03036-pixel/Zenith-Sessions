using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;
using MacroSuite.App.Themes;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

[Collection(WpfProductionResourceCollection.Name)]
public sealed class DestinationSearchPresentationTests
{
    private static readonly XNamespace Ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation",X="http://schemas.microsoft.com/winfx/2006/xaml";
    public static IEnumerable<object[]> Cases()
    {foreach(string theme in new[]{"graphite-mint","midnight","amber-night","ocean-blue","violet-dusk","custom-light"})foreach(double width in new[]{728d,1088d})yield return new object[]{theme,width};}

    [Theory]
    [MemberData(nameof(Cases))]
    public Task SearchResultsRenderAsATwoColumnCardGrid(string theme,double width)=>SearchTest.OnUi(async()=>
    {
        await using var model=new SearchTest();model.Directory.SearchResponse=q=>SearchTest.Ok(model.Directory.Page(q,"next") with
        {Items=Enumerable.Range(1,50).Select(i=>new PublicRobloxGameSearchResult(i,1000+i,"Synthetic public experience "+i)).ToArray()});
        using var fixture=new Fixture(model,theme);model.Browser.UsePreferredPlace(42);model.Browser.LinkInput="Fisch";
        await model.Search.SearchOrUseAsync();await model.Search.ArtOperation;Layout(fixture.View,width,620);
        var input=Visual(fixture.View).OfType<TextBox>().Single();Assert.Equal("Fisch",input.Text);Assert.Equal(4096,input.MaxLength);
        Assert.Contains("Game name",AutomationProperties.GetName(input));
        var grid=Grid(fixture.View);Assert.Same(model.Search.VisibleResults,grid.ItemsSource);Assert.Equal(12,grid.Items.Count);
        Assert.DoesNotContain(Visual(fixture.View),element=>element is ListBox);
        Assert.Equal(2,Visual(grid).OfType<UniformGrid>().Single().Columns);
        var cards=Visual(grid).OfType<Button>().ToArray();Assert.Equal(12,cards.Length);
        Assert.Same(model.Search.UseCard,cards[0].Command);Assert.Same(model.Search.VisibleResults[0],cards[0].CommandParameter);
        Assert.Contains("Public listing",AutomationProperties.GetName(cards[0]));Reachable(fixture.View,cards[0]);Assert.True(cards[0].ActualWidth>=200);
        Assert.All(model.Search.VisibleResults,row=>Assert.True(row.HasArt)); // The synthetic directory supplied every picture.
        var text=Visual(grid).OfType<TextBlock>().Where(block=>block.Visibility==Visibility.Visible).Select(block=>block.Text).ToArray();
        Assert.Contains("Synthetic public experience 1",text);Assert.Contains("Creator unavailable",text);Assert.Contains("Player count unavailable",text);
        Assert.DoesNotContain(text,value=>value.EndsWith("% liked",StringComparison.Ordinal));
        var sort=Visual(fixture.View).OfType<ComboBox>().Single();Assert.Same(model.Search.SortOptions,sort.ItemsSource);Assert.Equal("Relevance",sort.SelectedItem);
        model.Search.SelectedSort="Rating";Flush();Assert.Equal("Rating",sort.SelectedItem);
        var showMore=Button(fixture.View,"Show more");var more=Button(fixture.View,"More results");var search=Button(fixture.View,"Search / Use");
        Assert.Same(model.Search.SearchOrUse,search.Command);Assert.Same(model.Search.ShowMore,showMore.Command);Assert.Same(model.Search.Next,more.Command);
        Assert.True(search.IsEnabled);Assert.True(showMore.IsEnabled);Assert.True(more.IsEnabled);
        Assert.DoesNotContain(Visual(fixture.View).OfType<Button>(),button=>Equals(button.Content,"Use selected experience"));
        foreach(var button in new[]{search,showMore,more,Button(fixture.View,"Cancel / discard draft")})
        {Assert.True(button.ActualHeight>=36);ScrollTo(fixture.View,button,width,620);Reachable(fixture.View,button);}
        Assert.Contains(Visual(fixture.View).OfType<TextBlock>(),block=>block.Text.StartsWith("Current request:",StringComparison.Ordinal)&&block.Text.Contains("42"));
        Assert.Equal(0,model.Directory.GameRequests);Assert.Equal(42,model.Browser.Destination!.PlaceId);
        fixture.View.ScrollToTop();Layout(fixture.View,width,620);
        var rendered=new RenderTargetBitmap((int)width,620,96,96,PixelFormats.Pbgra32);rendered.Render(fixture.View);Assert.Equal(620,rendered.PixelHeight);
    });

    [Fact]
    public Task CardsShowPictureCreatorPlayingAndRating()=>SearchTest.OnUi(async()=>
    {
        await using var model=new SearchTest(art:false);
        model.Directory.SearchResponse=q=>SearchTest.Ok(new PublicRobloxGameSearchPage(q,[new(1,11,"Fisch",7,"Fischling",85_200,false,true,920,80),new(2,12,"Plain")],null,model.Clock.Utc));
        using var fixture=new Fixture(model,"graphite-mint");model.Browser.LinkInput="Fisch";await model.Search.SearchOrUseAsync();Layout(fixture.View,1088,620);
        var cards=Visual(Grid(fixture.View)).OfType<Button>().ToArray();Assert.Equal(2,cards.Length);
        Assert.Equal(new[]{"Fisch","by Fischling","85.2K playing","92% liked"},Text(cards[0]));
        Assert.Equal(new[]{"Plain","Creator unavailable","Player count unavailable"},Text(cards[1]));
        Assert.Equal(Visibility.Visible,Glyph(cards[0],"Verified creator").Visibility);Assert.Equal(Visibility.Collapsed,Glyph(cards[1],"Verified creator").Visibility);
        var row=model.Search.VisibleResults[0];var brush=Visual(cards[0]).OfType<Border>().Select(border=>border.Background).OfType<ImageBrush>().Single();
        Assert.Null(brush.ImageSource);Assert.Equal(Stretch.UniformToFill,brush.Stretch);
        row.Art=PublicAccountPresentation.DecodeCardPng(PublicAccountPresentationTests.Png(width:384,height:216),"image/png",default);Flush();
        Assert.NotNull(row.Art);Assert.Same(row.Art,brush.ImageSource);
        Assert.Equal(Visibility.Collapsed,Glyph(cards[0],"Current requested destination").Visibility);
        await model.Search.UseCardAsync(row);Flush();
        Assert.True(row.IsCurrent);Assert.Equal(Visibility.Visible,Glyph(cards[0],"Current requested destination").Visibility);
        Assert.Equal(Visibility.Collapsed,Glyph(cards[1],"Current requested destination").Visibility);
        static string[] Text(Button card)=>Visual(card).OfType<TextBlock>().Where(block=>block.Visibility==Visibility.Visible).Select(block=>block.Text).ToArray();
    });

    [Fact]
    public Task SponsoredCardsSaySoAndOthersStayUnmarked()=>SearchTest.OnUi(async()=>
    {
        await using var model=new SearchTest(art:false);
        model.Directory.SearchResponse=q=>SearchTest.Ok(new PublicRobloxGameSearchPage(q,
            [new(1,11,"Promoted",Sponsored:true),new(2,12,"Organic",Sponsored:false),new(3,13,"Unknown")],null,model.Clock.Utc));
        using var fixture=new Fixture(model,"graphite-mint");model.Browser.LinkInput="Fisch";await model.Search.SearchOrUseAsync();Layout(fixture.View,1088,620);
        var cards=Visual(Grid(fixture.View)).OfType<Button>().ToArray();Assert.Equal(3,cards.Length);
        Assert.Contains("Sponsored",Text(cards[0]));Assert.StartsWith("Promoted. Sponsored.",AutomationProperties.GetName(cards[0]),StringComparison.Ordinal);
        foreach(var card in cards[1..])
        {Assert.DoesNotContain("Sponsored",Text(card));Assert.DoesNotContain("Sponsored",AutomationProperties.GetName(card),StringComparison.Ordinal);}
        static string[] Text(Button card)=>Visual(card).OfType<TextBlock>().Where(block=>block.Visibility==Visibility.Visible).Select(block=>block.Text).ToArray();
    });

    [Fact]
    public Task NarrowPanelsUseOneColumn()=>SearchTest.OnUi(async()=>
    {
        await using var model=new SearchTest(art:false);using var fixture=new Fixture(model,"graphite-mint");
        model.Browser.LinkInput="Fisch";await model.Search.SearchOrUseAsync();
        Layout(fixture.View,520,620);Assert.Equal(1,Visual(Grid(fixture.View)).OfType<UniformGrid>().Single().Columns);
        Layout(fixture.View,728,620);Assert.Equal(2,Visual(Grid(fixture.View)).OfType<UniformGrid>().Single().Columns);
    });

    [Fact]
    public Task EmptyContinuationRemainsReachableAndAChosenCardKeepsTheGrid()=>SearchTest.OnUi(async()=>
    {
        await using var model=new SearchTest(art:false);
        model.Directory.SearchResponse=q=>SearchTest.Ok(model.Directory.Page(q,"next") with {Items=[]});
        using var fixture=new Fixture(model,"graphite-mint");model.Browser.LinkInput="Fisch";
        await model.Search.SearchOrUseAsync();Layout(fixture.View,728,620);
        Assert.Empty(model.Search.Results);
        var more=Button(fixture.View,"More results");
        Assert.Equal(Visibility.Visible,more.Visibility);Assert.True(more.IsEnabled);Reachable(fixture.View,more);
        Assert.Equal(Visibility.Collapsed,Button(fixture.View,"Show more").Visibility);Assert.Equal(Visibility.Collapsed,Grid(fixture.View).Visibility);

        model.Directory.SearchResponse=q=>SearchTest.Ok(model.Directory.Page(q,null));
        await model.Search.SearchOrUseAsync();var card=model.Search.VisibleResults[0];
        await model.Search.UseCardAsync(card);Layout(fixture.View,728,620);
        Assert.Equal(1,model.Directory.GameRequests);Assert.True(card.IsCurrent);
        Assert.Equal(2,Visual(Grid(fixture.View)).OfType<Button>().Count()); // The grid stays so the chosen card can show its mark.
        foreach(string label in new[]{"Show more","More results"})Assert.Equal(Visibility.Collapsed,Button(fixture.View,label).Visibility);
        var cancel=Button(fixture.View,"Cancel / discard draft");Assert.Equal(Visibility.Visible,cancel.Visibility);Assert.True(cancel.IsEnabled);
        Assert.Contains(Visual(fixture.View).OfType<TextBlock>(),block=>block.Text.StartsWith("Current request:",StringComparison.Ordinal)&&block.Text.Contains("101"));
        Assert.Contains("selected",model.Search.Status,StringComparison.OrdinalIgnoreCase);
    });

    private sealed class Model(SearchTest model)
    {public ServerBrowserViewModel ServerBrowser=>model.Browser;public DestinationSearchViewModel DestinationSearch=>model.Search;}
    private sealed class Fixture:IDisposable
    {
        private readonly Window _window;
        public ScrollViewer View {get;}
        public Fixture(SearchTest model,string theme)
        {
            _=Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml",UriKind.Relative));
            using var stream=typeof(DestinationSearchPresentationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
            var source=XDocument.Load(stream);
            var input=source.Descendants(Ui+"TextBox").Single(element=>(string?)element.Attribute(X+"Name")=="DestinationInput");
            var results=source.Descendants(Ui+"Border").Single(element=>(string?)element.Attribute(X+"Name")=="DestinationSearchPanel");
            // The production page scrolls; the card grid grows with Show more, so the fixture scrolls too.
            var content=new XElement(Ui+"ScrollViewer",new XAttribute("VerticalScrollBarVisibility","Auto"),new XAttribute("HorizontalScrollBarVisibility","Disabled"),
                new XElement(Ui+"StackPanel",new XAttribute("DataContext","{Binding ServerBrowser}"),new XElement(input.Parent!),new XElement(results)));
            var entries=source.Root!.Element(Ui+"Window.Resources")!.Elements().Where(element=>(string?)element.Attribute(X+"Key") is "Muted" or "AccountItem").Select(element=>new XElement(element));
            var resources=new XElement(Ui+"ResourceDictionary",new XElement(Ui+"ResourceDictionary.MergedDictionaries",new XElement(Ui+"ResourceDictionary",new XAttribute("Source","/Zenith.Sessions.App;component/Themes/SessionsControls.xaml"))),new XElement(Ui+"BooleanToVisibilityConverter",new XAttribute(X+"Key","VisibleIf")),entries);
            var root=new XElement(Ui+"Window",new XAttribute(XNamespace.Xmlns+"x",X.NamespaceName),new XElement(Ui+"Window.Resources",resources),content);
            _window=(Window)XamlReader.Parse(root.ToString());ThemeManager.Apply(_window.Resources,theme=="custom-light"?"graphite-mint":theme);
            if(theme=="custom-light")foreach(var pair in new Dictionary<string,string>{["AppBackground"]="#FFFFFF",["CardBackground"]="#FAFAFA",["TextPrimary"]="#151515",["TextSecondary"]="#454545",["TextMuted"]="#555555",["SelectionBackground"]="#DDE8EE",["SelectionForeground"]="#151515"})_window.Resources[pair.Key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            View=(ScrollViewer)_window.Content;_window.DataContext=new Model(model);View.SetResourceReference(Control.ForegroundProperty,"TextPrimary");
        }
        public void Dispose(){_window.Content=null;_window.Close();View.DataContext=null;}
    }
    private static ItemsControl Grid(DependencyObject root)=>Visual(root).OfType<ItemsControl>().Single(control=>control.Name=="DestinationSearchResults");
    private static Path Glyph(DependencyObject root,string toolTip)=>Visual(root).OfType<Path>().Single(path=>Equals(path.ToolTip,toolTip));
    private static Button Button(DependencyObject root,string text)=>Visual(root).OfType<Button>().Single(button=>Equals(button.Content,text));
    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Visual(child))yield return nested;}}
    private static void Flush()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    private static void Layout(FrameworkElement view,double width,double height){view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));Flush();view.UpdateLayout();}
    private static void ScrollTo(ScrollViewer view,FrameworkElement element,double width,double height)
    {var content=(FrameworkElement)view.Content;double top=element.TransformToAncestor(content).Transform(new Point()).Y;view.ScrollToVerticalOffset(Math.Max(0,top-50));Layout(view,width,height);}
    private static void Reachable(FrameworkElement root,FrameworkElement item)
    {var bounds=item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));Assert.True(item.ActualWidth>0&&item.ActualHeight>0);Assert.InRange(bounds.Left,0,root.ActualWidth);Assert.InRange(bounds.Right,0,root.ActualWidth+.5);Assert.InRange(bounds.Top,0,root.ActualHeight);Assert.InRange(bounds.Bottom,0,root.ActualHeight+.5);}
}
