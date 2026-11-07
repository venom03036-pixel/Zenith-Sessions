using System.Windows;
using System.Windows.Controls;
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
public sealed class NativeLaunchDiagnosticLayoutTests
{
    private static readonly XNamespace Ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation",X="http://schemas.microsoft.com/winfx/2006/xaml";
    [Theory]
    [InlineData("graphite-mint")]
    [InlineData("midnight")]
    [InlineData("amber-night")]
    [InlineData("ocean-blue")]
    [InlineData("violet-dusk")]
    [InlineData("custom-light")]
    public Task ProductionExpanderShowsFixedFailedStageAndKeepsItsOriginalTimeReachable(string theme)=>SearchTest.OnUi(()=>
    {
        var id=Guid.NewGuid();var source=NativeLaunchDiagnosticPresentationTests.Snapshot(id) with
        {TrustStatus=ClientExecutableTrustStatus.Rejected,TrustReason=ClientExecutableTrustReason.Unsigned,PreparationStatus=null,BrowserOutcome=null,
            ShapeStatus=null,DispatchStatus=null,PreparationObservedUtc=null,BrowserObservedUtc=null,DispatchObservedUtc=null};
        using var fixture=new Fixture(NativeLaunchDiagnosticPresentation.Project(id,source),theme);Layout(fixture.View,728,480);
        Assert.Equal("NativeLaunchStages",fixture.Expander.Name);Assert.False(fixture.Expander.IsExpanded);fixture.Expander.IsExpanded=true;Layout(fixture.View,728,480);
        Assert.Contains(Visual(fixture.Expander).OfType<TextBlock>(),text=>text.Text.Contains("signature was not established",StringComparison.Ordinal));
        Assert.Contains(Visual(fixture.Expander).OfType<TextBlock>(),text=>text.Text.Contains("not reached or not recorded",StringComparison.Ordinal));
        var times=Visual(fixture.Expander).OfType<TextBlock>().Single(text=>text.Text.Contains("17:59:51 UTC",StringComparison.Ordinal));
        double top=times.TransformToAncestor((FrameworkElement)fixture.View.Content).Transform(new Point()).Y;
        fixture.View.ScrollToVerticalOffset(Math.Max(0,top-30));Layout(fixture.View,728,480);Reachable(fixture.View,times);
        Assert.DoesNotContain(Visual(fixture.Expander).OfType<Button>(),button=>button.Command is not null);Assert.Null(fixture.Projection.Snapshot?.DispatchStatus);
        fixture.View.ScrollToTop();Layout(fixture.View,728,480);var image=new RenderTargetBitmap(728,480,96,96,PixelFormats.Pbgra32);image.Render(fixture.View);Assert.Equal(728,image.PixelWidth);
        return Task.CompletedTask;
    });
    [Fact]
    public Task MissingTransientHistoryRendersAnExplicitUnavailableExplanationRatherThanAnEmptySuccess()=>SearchTest.OnUi(()=>
    {
        using var fixture=new Fixture(NativeLaunchDiagnosticPresentation.Unavailable(Guid.NewGuid()),"midnight");fixture.Expander.IsExpanded=true;Layout(fixture.View,728,480);
        var text=Visual(fixture.Expander).OfType<TextBlock>().Select(value=>value.Text).ToArray();
        Assert.Contains(text,value=>value.Contains("unavailable for this request",StringComparison.Ordinal));Assert.Contains(text,value=>value.Contains("evicted",StringComparison.Ordinal));
        Assert.DoesNotContain(text,value=>value.Contains("start was reported",StringComparison.Ordinal));Assert.DoesNotContain(text,value=>value.Contains("publisher signature verified",StringComparison.Ordinal));
        Assert.False(fixture.Projection.AccountVerified);Assert.False(fixture.Projection.ArrivalVerified);Assert.False(fixture.Projection.InputAuthorized);return Task.CompletedTask;
    });
    private sealed class Model(NativeLaunchDiagnosticPresentation view)
    {public NativeLaunchDiagnosticPresentation SelectedNativeDiagnostics=>view;public bool HasLaunchEvidence=>true;}
    private sealed class Fixture:IDisposable
    {
        private readonly Window _window;
        public NativeLaunchDiagnosticPresentation Projection {get;}
        public ScrollViewer View {get;}
        public Expander Expander {get;}
        public Fixture(NativeLaunchDiagnosticPresentation view,string theme)
        {
            Projection=view;_=Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml",UriKind.Relative));
            using var stream=typeof(NativeLaunchDiagnosticLayoutTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
            var document=XDocument.Load(stream);var section=new XElement(document.Descendants(Ui+"Expander").Single(element=>(string?)element.Attribute(X+"Name")=="NativeLaunchStages"));
            var muted=new XElement(document.Root!.Element(Ui+"Window.Resources")!.Elements().Single(element=>(string?)element.Attribute(X+"Key")=="Muted"));
            var resources=new XElement(Ui+"ResourceDictionary",new XElement(Ui+"ResourceDictionary.MergedDictionaries",new XElement(Ui+"ResourceDictionary",new XAttribute("Source","/Zenith.Sessions.App;component/Themes/SessionsControls.xaml"))),new XElement(Ui+"BooleanToVisibilityConverter",new XAttribute(X+"Key","VisibleIf")),muted);
            var root=new XElement(Ui+"Window",new XAttribute(XNamespace.Xmlns+"x",X.NamespaceName),new XElement(Ui+"Window.Resources",resources),new XElement(Ui+"ScrollViewer",new XAttribute("VerticalScrollBarVisibility","Auto"),new XAttribute("HorizontalScrollBarVisibility","Disabled"),section));
            _window=(Window)XamlReader.Parse(root.ToString());ThemeManager.Apply(_window.Resources,theme=="custom-light"?"graphite-mint":theme);
            if(theme=="custom-light")foreach(var pair in new Dictionary<string,string>{["AppBackground"]="#FFFFFF",["CardBackground"]="#FAFAFA",["TextPrimary"]="#151515",["TextSecondary"]="#454545",["TextMuted"]="#555555",["SelectionBackground"]="#DDE8EE",["SelectionForeground"]="#151515"})_window.Resources[pair.Key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            _window.DataContext=new Model(view);View=(ScrollViewer)_window.Content;Expander=(Expander)View.Content;View.SetResourceReference(Control.ForegroundProperty,"TextPrimary");
        }
        public void Dispose(){_window.Content=null;_window.Close();View.DataContext=null;}
    }
    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {for(int index=0;index<VisualTreeHelper.GetChildrenCount(root);index++){var child=VisualTreeHelper.GetChild(root,index);yield return child;foreach(var nested in Visual(child))yield return nested;}}
    private static void Layout(FrameworkElement view,double width,double height){view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();}
    private static void Reachable(FrameworkElement root,FrameworkElement item)
    {var bounds=item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));Assert.True(item.ActualWidth>0&&item.ActualHeight>0);Assert.InRange(bounds.Left,0,root.ActualWidth);Assert.InRange(bounds.Right,0,root.ActualWidth+.5);Assert.InRange(bounds.Top,0,root.ActualHeight);Assert.InRange(bounds.Bottom,0,root.ActualHeight+.5);}
}
