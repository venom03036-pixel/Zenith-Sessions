using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;
using MacroSuite.App.Themes;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

[Collection(WpfProductionResourceCollection.Name)]
public sealed class SidebarTemplateTests
{
    [Theory]
    [InlineData("pseudo")]
    [InlineData("pseudo-rtl")]
    public Task LongerAndRightToLeftNavigationKeepsLabelsInsideTheirControls(string language)
        =>PackagedLayoutCheck.RunOnStaAsync(()=>new object(),_=>
        {
            var (view,resources)=ProductionRail();ThemeManager.Apply(resources,"graphite-mint");
            var strings=new ShellStrings(ShellLanguages.Resolve(language,System.Globalization.CultureInfo.GetCultureInfo("en-US")));
            Zenith.Sessions.App.Localization.SetStrings(view,strings);view.FlowDirection=strings.FlowDirection;view.DataContext=new RailModel();
            view.Measure(new Size(184,500));view.Arrange(new Rect(0,0,184,500));view.UpdateLayout();
            var buttons=Descendants(view).OfType<Button>().ToArray();Assert.Equal(9,buttons.Length);
            foreach(var button in buttons)
            {
                string caption=AutomationProperties.GetName(button);Assert.StartsWith("[",caption);
                Assert.Equal(strings.FlowDirection,button.FlowDirection);
                var label=Assert.Single(Descendants(button).OfType<TextBlock>().Where(t=>t.Text==caption));
                var bounds=label.TransformToAncestor(button).TransformBounds(new Rect(label.RenderSize));
                Assert.True(bounds.Left>=0 && bounds.Right<=button.ActualWidth+0.5,$"{language}: {caption} falls outside its control: {bounds}, width {button.ActualWidth}");
                Assert.True(bounds.Top>=0 && bounds.Bottom<=button.ActualHeight+0.5,$"{language}: {caption} is vertically clipped.");
                var text=new FormattedText(label.Text,System.Globalization.CultureInfo.GetCultureInfo("en-US"),label.FlowDirection,
                    new Typeface(label.FontFamily,label.FontStyle,label.FontWeight,label.FontStretch),label.FontSize,Brushes.White,VisualTreeHelper.GetDpi(label).PixelsPerDip);
                if(label.TextWrapping==TextWrapping.NoWrap) Assert.True(text.Width<=label.ActualWidth+0.5,$"{language}: {caption} loses its full label: {text.Width} in {label.ActualWidth}.");
            }
            return Task.CompletedTask;
        },_=>{},CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData("graphite-mint")]
    [InlineData("midnight")]
    [InlineData("amber-night")]
    [InlineData("ocean-blue")]
    [InlineData("violet-dusk")]
    public Task ProductionNavigationLoadsWithRealThemeResourcesAndReachableLabeledControls(string theme)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                var (view,resources)=ProductionRail();ThemeManager.Apply(resources,theme);
                view.Measure(new Size(184,500));view.Arrange(new Rect(0,0,184,500));view.UpdateLayout();
                var buttons=Descendants(view).OfType<Button>().ToArray();
                Assert.Equal(new[]{"Overview","Accounts","Sessions","Windows","Automation","Diagnostics","Settings","Guide","News"},buttons.Select(AutomationProperties.GetName));
                foreach(var button in buttons)
                {
                    Assert.True(button.ActualHeight>=36);Assert.True(button.ActualWidth>=140);Assert.True(button.Focusable);
                    Assert.Contains(Descendants(button).OfType<TextBlock>(),t=>t.Text==AutomationProperties.GetName(button));
                    var icon=Assert.Single(Descendants(button).OfType<System.Windows.Shapes.Path>());
                    Assert.Equal(16,icon.Height);Assert.Equal(16,icon.Width);Assert.NotNull(icon.Data);
                    Assert.InRange(icon.ActualHeight,1,16);Assert.InRange(icon.ActualWidth,1,16);
                    var bounds=button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
                    Assert.InRange(bounds.Top,0,500);Assert.InRange(bounds.Bottom,0,500);
                    button.Tag=true;Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.DataBind);view.UpdateLayout();
                    var marker=(Border)button.Template.FindName("Rail",button);
                    Assert.Equal(((SolidColorBrush)resources["AccentPrimary"]).Color,((SolidColorBrush)marker.Background).Color);
                    var fill=(Border)button.Template.FindName("Fill",button);Assert.NotNull(fill.Background);
                    button.Tag=false;
                }
                done.TrySetResult();
            }
            catch(Exception ex) {done.TrySetException(ex);}
            finally {Dispatcher.CurrentDispatcher.InvokeShutdown();}
        }) {IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    // Windows High Contrast Black and White, where the selection colour is the Highlight colour and every other text colour is the
    // window text colour, plus a dark preset and Light. The current page must draw its name, icon and account count in the selection
    // text colour, not in the window text colour, which is about 1.4:1 on the Highlight colour in both High Contrast themes.
    [Theory]
    [InlineData("high contrast black")]
    [InlineData("high contrast white")]
    [InlineData("graphite-mint")]
    [InlineData("light")]
    public Task TheCurrentPageDrawsItsNameIconAndCountReadablyOnTheSelectionColour(string palette)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                var (view,resources)=ProductionRail();SessionsThemes.Apply(resources,SessionsThemesTests.Palette(palette));
                view.DataContext=new RailModel();view.Measure(new Size(184,500));view.Arrange(new Rect(0,0,184,500));
                Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.DataBind);view.UpdateLayout();
                Color selection=((SolidColorBrush)resources["SelectionBackground"]).Color;
                foreach(var button in Descendants(view).OfType<Button>().ToArray())
                {
                    string page=AutomationProperties.GetName(button);
                    button.Tag=true;Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.DataBind);view.UpdateLayout();
                    var fill=(Border)button.Template.FindName("Fill",button);var image=RenderedInk.Render(view);
                    Assert.Equal(selection,RenderedInk.At(image,view,fill,new Point(fill.ActualWidth/2,3)));
                    var texts=Descendants(button).OfType<TextBlock>().Where(text=>text.Text.Length>0).ToArray();
                    Assert.Equal(page=="Accounts"?2:1,texts.Length); // The Accounts page also shows the account count.
                    foreach(var text in texts) RenderedInk.AssertReadable(image,view,text,selection,4.5,$"{palette}: the current page's '{text.Text}'");
                    RenderedInk.AssertReadable(image,view,Descendants(button).OfType<System.Windows.Shapes.Path>().Single(),selection,3,$"{palette}: the current page's {page} icon");
                    button.Tag=false;
                }
                done.TrySetResult();
            }
            catch(Exception ex) {done.TrySetException(ex);}
            finally {Dispatcher.CurrentDispatcher.InvokeShutdown();}
        }) {IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    // The production navigation rail with its own styles, over the shared controls; the caller applies a palette.
    private static (Border View,ResourceDictionary Resources) ProductionRail()
    {
        using var input=typeof(SidebarTemplateTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document=XDocument.Load(input);XNamespace ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
        var rail=new XElement(document.Descendants(ui+"Border").Single(e=>(string?)e.Attribute(x+"Name")=="NavigationRail"));
        rail.Attribute(x+"Name")!.Remove();rail.Add(new XAttribute(x+"Key","Rail")); // A standalone resource has a key, not a window name scope.
        var dictionary=new XElement(ui+"ResourceDictionary",new XAttribute(XNamespace.Xmlns+"x",x.NamespaceName),
            new XAttribute(XNamespace.Xmlns+"local","clr-namespace:Zenith.Sessions.App;assembly=Zenith.Sessions.App"),
            new XElement(ui+"ResourceDictionary.MergedDictionaries",new XElement(ui+"ResourceDictionary",new XAttribute("Source","/Zenith.Sessions.App;component/Themes/ModernControls.xaml"))),
            document.Descendants(ui+"Style").Where(e=>(string?)e.Attribute(x+"Key") is "SessionNav" or "NavigationIcon" or "Muted").Select(e=>new XElement(e)),rail);
        var resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var view=(Border)resources["Rail"];view.Resources=resources;
        return (view,resources);
    }
    // The two values the rail shows from the shell; its commands and page flags are left unbound.
    private sealed class RailModel
    {
        public int AccountCount=>12;
        public string ProductLabel=>"Zenith Sessions · Personal";
    }
    [Fact]
    public void ControlDigitShortcutsFollowTheRailOrderAndGuideMatchesCurrentControls()
    {
        using var input=typeof(SidebarTemplateTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document=XDocument.Load(input);XNamespace ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
        var rail=document.Descendants(ui+"Border").Single(e=>(string?)e.Attribute(x+"Name")=="NavigationRail");
        var railTargets=rail.Descendants(ui+"Button").Select(Target).ToArray();
        // The window builds its key bindings from ShellShortcuts (R-shell-4), so the digit rows are read from that table.
        var digitShortcuts=ShellShortcuts.All.Where(s=>s.Modifiers==ModifierKeys.Control && s.Key is >=Key.D1 and <=Key.D9).OrderBy(s=>s.Key)
            .Select<ShellShortcut,(string?,string?)>(s=>s.Action==ShellShortcutAction.ShowWindowsOverview?("{Binding ShowWindowsOverview}",null):("{Binding Navigate}",s.Page)).ToArray();
        // Ctrl+N must open the Nth sidebar item through the same command (and lock/shutdown guard) the rail button uses.
        Assert.Equal(railTargets,digitShortcuts);
        Assert.Equal(("{Binding ShowWindowsOverview}",(string?)null),digitShortcuts[3]);
        char arrow=(char)0x2192;
        var texts=document.Descendants(ui+"TextBlock").Select(e=>(string?)e.Attribute("Text")).OfType<string>().ToArray();
        // The Guide lists that same table (ShellShortcutsTests renders it) instead of a hand-written key range.
        Assert.Contains(document.Descendants(ui+"ItemsControl"),e=>(string?)e.Attribute("ItemsSource")=="{x:Static local:ShellShortcuts.All}");
        Assert.DoesNotContain(texts,t=>t.Contains("seven sidebar sections"));
        // The Guide names only controls that exist: result cards replaced the Use selected experience button,
        // and bundle editing lives under Who is launching > Edit bundles.
        Assert.DoesNotContain(texts,t=>t.Contains("Use selected experience"));
        Assert.DoesNotContain(texts,t=>t.Contains($"Join a game {arrow} Account bundles"));
        Assert.Contains("Who is launching",texts);
        Assert.Contains(document.Descendants(ui+"Expander"),e=>(string?)e.Attribute("Header")=="Edit bundles");
        Assert.Contains(texts,t=>t.Contains($"Join a game {arrow} Who is launching {arrow} Edit bundles"));
        static (string? Command,string? Parameter) Target(XElement e)=>((string?)e.Attribute("Command"),(string?)e.Attribute("CommandParameter"));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var descendant in Descendants(child)) yield return descendant;}
    }
}
