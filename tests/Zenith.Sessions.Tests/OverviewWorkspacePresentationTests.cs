using System.Collections.ObjectModel;
using System.Windows;
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
public sealed class OverviewWorkspacePresentationTests
{
    private static readonly XNamespace Ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation",X="http://schemas.microsoft.com/winfx/2006/xaml";
    public static IEnumerable<object[]> Cases()
    {foreach(string theme in new[]{"graphite-mint","midnight","amber-night","ocean-blue","violet-dusk","custom-light"}) foreach(double width in new[]{728d,1088d}) yield return new object[]{theme,width};}
    [Theory]
    [MemberData(nameof(Cases))]
    public Task ActualOverviewKeepsCountsAndViewOnlyNavigationAboveItsDetailedResourceText(string theme,double width)=>OnSta(()=>
    {
        var model=new Model();using var fixture=new Fixture(model,theme);var view=fixture.View;Layout(view,width,480);
        var counts=Named<Grid>(view,"OverviewWorkspaceCounts");Reachable(view,counts);
        Assert.Equal("10000",BoundText(counts,"Overview.SavedProfiles").Text);Assert.Equal("1",BoundText(counts,"Overview.ObservedProcesses").Text);
        foreach(var button in Visual(Named<WrapPanel>(view,"OverviewNavigation")).OfType<Button>()) {Assert.True(button.ActualHeight>=36);Reachable(view,button);}
        var details=Named<Expander>(view,"OverviewDetails");Assert.False(details.IsExpanded);
        var charts=Visual(view).OfType<ResourceSparkline>().ToArray();Assert.Equal(3,charts.Length);
        Assert.Same(model.Telemetry.PhysicalPoints,charts[0].Points);Assert.Same(model.Telemetry.CommitPoints,charts[1].Points);Assert.Same(model.Telemetry.CpuPoints,charts[2].Points);
        Assert.All(charts,chart=>Assert.Equal(40,chart.Height));Assert.Equal(0,model.Changes);
        var image=new RenderTargetBitmap((int)width,480,96,96,PixelFormats.Pbgra32);image.Render(view);
        Assert.True(image.PixelWidth>=728);Assert.Equal(480,image.PixelHeight);
        return Task.CompletedTask;
    });
    [Fact]
    public Task UnknownHeadlinesRenderDashesAndPreserveTheActualNoDataExplanation()=>OnSta(()=>
    {
        var model=new Model {Overview=OverviewWorkspaceProjection.Create(new(),DateTimeOffset.UtcNow)};
        using var fixture=new Fixture(model,"midnight");Layout(fixture.View,728,480);var counts=Named<Grid>(fixture.View,"OverviewWorkspaceCounts");
        foreach(string binding in new[]{"Overview.SavedProfiles","Overview.ObservedProcesses","Overview.QueuedRequests"}) Assert.Equal("—",BoundText(counts,binding).Text);
        Assert.Contains("unavailable",BoundText(counts,"Overview.LibraryCaption").Text);Assert.Contains("unknown",BoundText(counts,"Overview.ProcessCaption").Text);
        return Task.CompletedTask;
    });
    [Fact]
    public Task DetailsRemainReachableByScrollingAndNeverReplaceHistoricalChartReferences()=>OnSta(()=>
    {
        var model=new Model();using var fixture=new Fixture(model,"custom-light");var view=fixture.View;Layout(view,728,480);
        var charts=Visual(view).OfType<ResourceSparkline>().ToArray();var references=charts.Select(chart=>chart.Points).ToArray();
        var details=Named<Expander>(view,"OverviewDetails");details.IsExpanded=true;Layout(view,728,480);
        var capacity=BoundText(view,"Capacity");capacity.BringIntoView();Flush();view.UpdateLayout();Reachable(view,capacity);
        Assert.Equal(model.Capacity,capacity.Text);Assert.Contains(Visual(details).OfType<TextBlock>(),text=>text.Text==model.PressureNotice);
        var gpu=Visual(details).OfType<Expander>().Single();gpu.IsExpanded=true;Layout(view,728,480);
        var gpuStatus=BoundText(view,"GpuTelemetry.Status");gpuStatus.BringIntoView();Flush();view.UpdateLayout();Reachable(view,gpuStatus);
        Assert.Equal(model.GpuTelemetry.Status,gpuStatus.Text);Assert.Contains("not been sampled",gpuStatus.Text);Assert.DoesNotContain("0%",gpuStatus.Text);
        for(int i=0;i<charts.Length;i++) Assert.Same(references[i],charts[i].Points);
        Assert.Equal(0,model.Changes);Assert.Same(model.Events,Named<ListBox>(view,"OverviewRecentActivity").ItemsSource);
        return Task.CompletedTask;
    });
    [Fact]
    public Task NavigationBindsExistingRoutesAndOpenQueueDoesNotResumeAnything()=>OnSta(()=>
    {
        var model=new Model();using var fixture=new Fixture(model,"graphite-mint");Layout(fixture.View,728,480);
        var buttons=Visual(Named<WrapPanel>(fixture.View,"OverviewNavigation")).OfType<Button>().ToArray();
        var accounts=buttons.Single(button=>Equals(button.Content,"Accounts"));Assert.Same(model.Navigate,accounts.Command);Assert.Equal("Accounts",accounts.CommandParameter);
        Assert.Same(model.ShowWindowsOverview,buttons.Single(button=>Equals(button.Content,"Windows")).Command);
        var automation=buttons.Single(button=>Equals(button.Content,"Automation"));Assert.Same(model.Navigate,automation.Command);Assert.Equal("Automation",automation.CommandParameter);
        var join=buttons.Single(button=>Equals(button.Content,"Join a game"));Assert.Same(model.Navigate,join.Command);Assert.Equal("JoinGame",join.CommandParameter);
        var queue=buttons.Single(button=>Equals(button.Content,"Open queue"));Assert.Same(model.OpenOverviewQueue,queue.Command);
        queue.Command!.Execute(null);Assert.True(model.QueueExpanded);Assert.Equal(1,model.Changes);
        return Task.CompletedTask;
    });
    private sealed class Model
    {
        public Model()
        {
            var input=OverviewWorkspaceProjectionTests.Input();var now=input.Desktop!.Resources.ObservedUtc;
            Overview=OverviewWorkspaceProjection.Create(input,now);var history=new TelemetryHistory();history.Observe(input.Desktop,now);
            Telemetry=new(history);Telemetry.Update(null,now);OpenOverviewQueue=new(_=>{QueueExpanded=true;Changes++;});
        }
        public OverviewWorkspaceData Overview {get;set;}
        public bool IsOverview=>true;
        public bool IsReady=>true;
        public string ActivePerformanceName=>"Balanced";
        public string ActivePerformanceSummary=>"Balanced · manager observation policy, not a game FPS claim.";
        public string Capacity=>"Synthetic admission detail preserves physical and commit guards, reservations and the independent provider gate.";
        public string PressureNotice=>"Synthetic pressure observation; no clients are launched.";
        public TelemetryDisplay Telemetry {get;}
        public GpuTelemetryDisplay GpuTelemetry {get;}=new();
        public ObservableCollection<string> Events {get;}=["Synthetic bounded activity."];
        public bool QueueExpanded;public int Changes;
        public Command Navigate {get;}=new(_=>{});
        public Command ShowWindowsOverview {get;}=new(_=>{});
        public Command OpenOverviewQueue {get;}
        public Command Refresh {get;}=new(_=>{});
    }
    private sealed class Fixture:IDisposable
    {
        private readonly Window _window;
        public ScrollViewer View {get;}
        public Fixture(Model model,string theme)
        {
            _=Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml",UriKind.Relative));
            using var stream=typeof(OverviewWorkspacePresentationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
            var source=XDocument.Load(stream);var page=new XElement(source.Descendants(Ui+"ScrollViewer").Single(element=>(string?)element.Attribute(X+"Name")=="OverviewPage"));
            string[] keys=["HistoryChart","Muted","Panel","SectionTitle"];
            var entries=source.Root!.Element(Ui+"Window.Resources")!.Elements().Where(element=>keys.Contains((string?)element.Attribute(X+"Key"))).Select(element=>new XElement(element));
            var resources=new XElement(Ui+"ResourceDictionary",new XElement(Ui+"ResourceDictionary.MergedDictionaries",new XElement(Ui+"ResourceDictionary",new XAttribute("Source","/Zenith.Sessions.App;component/Themes/SessionsControls.xaml"))),new XElement(Ui+"BooleanToVisibilityConverter",new XAttribute(X+"Key","VisibleIf")),entries);
            var root=new XElement(Ui+"Window",new XAttribute(XNamespace.Xmlns+"x",X.NamespaceName),new XAttribute(XNamespace.Xmlns+"local","clr-namespace:Zenith.Sessions.App;assembly=Zenith.Sessions.App"),new XElement(Ui+"Window.Resources",resources),page);
            // Production BAML resolves its own assembly implicitly. The extracted loose
            // XAML must qualify both element XNames and copied prefix declarations.
            XNamespace productionApp="clr-namespace:Zenith.Sessions.App",fixtureApp="clr-namespace:Zenith.Sessions.App;assembly=Zenith.Sessions.App";
            foreach(var element in root.DescendantsAndSelf())
            {
                if(element.Name.Namespace==productionApp) element.Name=fixtureApp+element.Name.LocalName;
                foreach(var attribute in element.Attributes().ToArray())
                {
                    if(attribute.IsNamespaceDeclaration && attribute.Value==productionApp.NamespaceName) attribute.Value=fixtureApp.NamespaceName;
                    else if(attribute.Name.Namespace==productionApp) {element.SetAttributeValue(fixtureApp+attribute.Name.LocalName,attribute.Value);attribute.Remove();}
                }
            }
            _window=(Window)XamlReader.Parse(root.ToString());ThemeManager.Apply(_window.Resources,theme=="custom-light"?"graphite-mint":theme);
            if(theme=="custom-light") foreach(var pair in new Dictionary<string,string>{["AppBackground"]="#FFFFFF",["CardBackground"]="#FAFAFA",["TextPrimary"]="#151515",["TextSecondary"]="#454545",["TextMuted"]="#555555",["SelectionBackground"]="#DDE8EE",["SelectionForeground"]="#151515"}) _window.Resources[pair.Key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            View=(ScrollViewer)_window.Content;_window.DataContext=model;View.SetResourceReference(Control.BackgroundProperty,"AppBackground");
        }
        public void Dispose(){_window.Content=null;_window.Close();View.DataContext=null;}
    }
    private static T Named<T>(DependencyObject root,string name) where T:FrameworkElement=>Visual(root).OfType<T>().Single(element=>element.Name==name);
    private static TextBlock BoundText(DependencyObject root,string path)=>Visual(root).OfType<TextBlock>().Single(text=>BindingOperations.GetBinding(text,TextBlock.TextProperty)?.Path?.Path==path);
    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Visual(child))yield return nested;}}
    private static void Layout(FrameworkElement view,double width,double height){view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));Flush();view.UpdateLayout();}
    private static void Flush()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    private static void Reachable(FrameworkElement root,FrameworkElement item)
    {var bounds=item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));Assert.True(item.ActualWidth>0 && item.ActualHeight>0);Assert.InRange(bounds.Left,0,root.ActualWidth);Assert.InRange(bounds.Right,0,root.ActualWidth+.5);Assert.InRange(bounds.Top,0,root.ActualHeight);Assert.InRange(bounds.Bottom,0,root.ActualHeight+.5);}
    internal static Task OnSta(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async()=>{try{await action();done.TrySetResult();}catch(Exception error){done.TrySetException(error);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
}
