using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;
using MacroSuite.App.Themes;
using Xunit;

namespace Zenith.Sessions.Tests;

[Collection(WpfProductionResourceCollection.Name)]
public sealed class SidebarTemplateTests
{
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
                using var input=typeof(SidebarTemplateTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
                var document=XDocument.Load(input);XNamespace ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
                var rail=new XElement(document.Descendants(ui+"Border").Single(e=>(string?)e.Attribute(x+"Name")=="NavigationRail"));
                rail.Attribute(x+"Name")!.Remove(); // A standalone resource has a key, not a window name scope.
                rail.Add(new XAttribute(x+"Key","Rail"));
                var dictionary=new XElement(ui+"ResourceDictionary",new XAttribute(XNamespace.Xmlns+"x",x.NamespaceName),
                    new XElement(ui+"ResourceDictionary.MergedDictionaries",new XElement(ui+"ResourceDictionary",new XAttribute("Source","/Zenith.Sessions.App;component/Themes/ModernControls.xaml"))),
                    document.Descendants(ui+"Style").Where(e=>(string?)e.Attribute(x+"Key") is "SessionNav" or "NavigationIcon" or "Muted").Select(e=>new XElement(e)),rail);
                var resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());ThemeManager.Apply(resources,theme);
                var view=(Border)resources["Rail"];view.Resources=resources;
                view.Measure(new Size(184,500));view.Arrange(new Rect(0,0,184,500));view.UpdateLayout();
                var buttons=Descendants(view).OfType<Button>().ToArray();
                Assert.Equal(new[]{"Overview","Accounts","Sessions","Windows","Automation","Diagnostics","Settings","Guide"},buttons.Select(AutomationProperties.GetName));
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
    [Fact]
    public void ControlDigitShortcutsFollowTheRailOrderAndGuideMatchesCurrentControls()
    {
        using var input=typeof(SidebarTemplateTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document=XDocument.Load(input);XNamespace ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
        var rail=document.Descendants(ui+"Border").Single(e=>(string?)e.Attribute(x+"Name")=="NavigationRail");
        var railTargets=rail.Descendants(ui+"Button").Select(Target).ToArray();
        var digitShortcuts=document.Root!.Element(ui+"Window.InputBindings")!.Elements(ui+"KeyBinding")
            .Where(e=>(string?)e.Attribute("Modifiers")=="Control" && (string?)e.Attribute("Key") is {Length:2} key && key[0]=='D' && key[1] is >='1' and <='9')
            .OrderBy(e=>(string)e.Attribute("Key")!,StringComparer.Ordinal).ToArray();
        // Ctrl+N must open the Nth sidebar item through the same command (and lock/shutdown guard) the rail button uses.
        Assert.Equal(railTargets,digitShortcuts.Select(Target));
        Assert.Equal(("{Binding ShowWindowsOverview}",(string?)null),Target(digitShortcuts[3]));
        char dash=(char)0x2013,arrow=(char)0x2192;
        var texts=document.Descendants(ui+"TextBlock").Select(e=>(string?)e.Attribute("Text")).OfType<string>().ToArray();
        Assert.Contains(texts,t=>t.Contains($"Ctrl+1{dash}{railTargets.Length} switches the "));
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
