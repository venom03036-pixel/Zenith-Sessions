using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

[Collection(WpfProductionResourceCollection.Name)]
public sealed class AccountTemplateTests
{
    [Theory]
    [InlineData("AccountRow")]
    [InlineData("DenseAccountRow")]
    public Task ActualProductionRowTemplatesAcceptReadOnlyAccountProperties(string key)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(() =>
        {
            try
            {
                // Exercise the actual production binding/geometry markup without constructing
                // the native window, registering keys, creating tray icons or touching user data.
                using var input=typeof(AccountTemplateTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
                var document=XDocument.Load(input);XNamespace ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
                var template=new XElement(document.Descendants(ui+"DataTemplate").Single(e=>(string?)e.Attribute(x+"Key")==key));
                foreach(var eventAttribute in template.Descendants().Attributes("Click").ToArray()) eventAttribute.Remove();
                // Native event handlers and unrelated window resources are deliberately absent; the shared control styles are merged.
                _=Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml",UriKind.Relative));
                var dictionary=new XElement(ui+"ResourceDictionary",new XAttribute(XNamespace.Xmlns+"x",x.NamespaceName),
                    new XElement(ui+"ResourceDictionary.MergedDictionaries",new XElement(ui+"ResourceDictionary",new XAttribute("Source","/Zenith.Sessions.App;component/Themes/SessionsControls.xaml"))),
                    new XElement(ui+"BooleanToVisibilityConverter",new XAttribute(x+"Key","VisibleIf")),
                    new XElement(ui+"Style",new XAttribute(x+"Key","GhostButton"),new XAttribute("TargetType","Button")),template);
                var resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());
                var row=(FrameworkElement)((DataTemplate)resources[key]).LoadContent();
                var profile=AccountProfile.Create(new string('L',100)) with {Username=new string('u',20),Group=new string('G',60)};
                row.DataContext=new AccountRow(profile);row.Measure(new Size(680,100));row.Arrange(new Rect(0,0,680,row.DesiredSize.Height));
                Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.DataBind);row.UpdateLayout();
                Assert.Contains(Descendants(row).OfType<TextBlock>(),t=>t.Text==profile.Label);
                if(key=="AccountRow") Assert.Contains(Descendants(row).OfType<TextBlock>(),t=>t.Text.Contains("@"+profile.Username));
                row.DataContext=null;done.TrySetResult();
            }
            catch(Exception ex) {done.TrySetException(ex);}
            finally {Dispatcher.CurrentDispatcher.InvokeShutdown();}
        }) {IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    [Theory]
    [InlineData("AccountRow")]
    [InlineData("DenseAccountRow")]
    public Task ProductionRowActionsRemainReachableInACompactViewport(string key)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                using var input = typeof(AccountTemplateTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
                var document = XDocument.Load(input);
                XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var template = new XElement(document.Descendants(ui + "DataTemplate").Single(e => (string?)e.Attribute(x + "Key") == key));
                foreach (var attribute in template.Descendants().Attributes("Click").ToArray()) attribute.Remove();
                _ = Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml", UriKind.Relative));
                var dictionary = new XElement(ui + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                    new XElement(ui + "ResourceDictionary.MergedDictionaries", new XElement(ui + "ResourceDictionary", new XAttribute("Source", "/Zenith.Sessions.App;component/Themes/SessionsControls.xaml"))),
                    new XElement(ui + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "VisibleIf")),
                    new XElement(ui + "Style", new XAttribute(x + "Key", "GhostButton"), new XAttribute("TargetType", "Button")), template);
                var resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
                var presenter = new ContentPresenter { ContentTemplate = (DataTemplate)resources[key],
                    Content = new AccountRow(AccountProfile.Create(new string('L', 100)) with { Username = new string('u', 20), Group = new string('G', 60) }) };
                window = new Window { Width = 400, Height = 110, DataContext = new { CompactLayout = true }, Content = presenter };
                // The unshown Window supplies the ancestor binding, but a hidden Window
                // skips child layout. Use the actual template presenter: LoadContent alone
                // does not install DataTemplate triggers on its returned standalone tree.
                presenter.Measure(new Size(400, 110)); presenter.Arrange(new Rect(0, 0, 400, presenter.DesiredSize.Height));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind); presenter.UpdateLayout();
                var row = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetChild(presenter, 0));
                var buttons = Descendants(row).OfType<Button>().ToArray();
                var power = Assert.Single(buttons.Where(b => Equals(b.Content, "Sign in")));
                Assert.InRange(power.ActualWidth, 70, 100);
                foreach (var button in buttons)
                {
                    var bounds = button.TransformToAncestor(row).TransformBounds(new Rect(button.RenderSize));
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.InRange(bounds.Left, 0, row.ActualWidth);
                    Assert.InRange(bounds.Right, 1, row.ActualWidth + .5);
                    Assert.InRange(bounds.Bottom, 1, row.ActualHeight + .5);
                }
                if (key == "AccountRow")
                {
                    var content = Assert.IsType<Grid>(row);
                    Assert.Equal(0, content.ColumnDefinitions[3].ActualWidth);
                    Assert.Contains(Descendants(row).OfType<TextBlock>(), t => t.Visibility == Visibility.Visible && t.Text.Contains("Not linked"));
                }
                else
                {
                    Assert.Equal(0, Assert.IsType<Grid>(row).ColumnDefinitions[2].ActualWidth);
                    var label = Assert.Single(Descendants(row).OfType<TextBlock>().Where(t => t.Name == "DenseAccountName"));
                    Assert.InRange(label.ActualWidth, 140, 400);
                }
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { window?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);yield return child;
            foreach(var nested in Descendants(child)) yield return nested;
        }
    }
}
