using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

// The manager's keyboard shortcuts come from one table: MainWindow builds its key bindings from it and the Guide lists it,
// so the list a user reads cannot drift from the keys that work.
public sealed class ShellShortcutsTests
{
    private static readonly XNamespace Ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation",X="http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void PanicComesFirstAndEverySidebarItemHasExactlyOneShortcutInSidebarOrder()
    {
        var rail=Source().Descendants(Ui+"Border").Single(e=>(string?)e.Attribute(X+"Name")=="NavigationRail").Descendants(Ui+"Button")
            .Select(e=>(Command:(string?)e.Attribute("Command"),Parameter:(string?)e.Attribute("CommandParameter"),Name:(string?)e.Attribute("AutomationProperties.Name"))).ToArray();
        var all=ShellShortcuts.All;
        Assert.NotEmpty(all);
        // Rule 11: Panic is the first shortcut, on F12 with no modifier.
        Assert.Equal((Key.F12,ModifierKeys.None,ShellShortcutAction.Panic,"F12"),(all[0].Key,all[0].Modifiers,all[0].Action,all[0].Gesture));
        // Ctrl+N opens the Nth sidebar item through the same command (and lock and shutdown guard) its button uses.
        var pages=all.Where(s=>s.Action is ShellShortcutAction.Navigate or ShellShortcutAction.ShowWindowsOverview).ToArray();
        Assert.Equal(rail.Select(r=>(r.Command,r.Parameter)),pages.Select(XamlTarget));
        Assert.Equal(rail.Select(r=>ProductionWindowStrings.Resolve(r.Name)),pages.Select(p=>p.Label));
        Assert.Equal(Enumerable.Range(1,rail.Length).Select(n=>"Ctrl+"+n),pages.Select(p=>p.Gesture));
        Assert.Equal("Ctrl+F",Assert.Single(all,s=>s.Action==ShellShortcutAction.FindOnPage).Gesture);
        // No two shortcuts share a key, and every one has a label for the Guide.
        Assert.Equal(all.Count,all.Select(s=>(s.Key,s.Modifiers)).Distinct().Count());
        Assert.Equal(all.Count,all.Select(s=>s.Gesture).Distinct().Count());
        Assert.All(all,s=>Assert.False(string.IsNullOrWhiteSpace(s.Label)));
        static (string?,string?) XamlTarget(ShellShortcut s)=>s.Action==ShellShortcutAction.ShowWindowsOverview?("{Binding ShowWindowsOverview}",null):("{Binding Navigate}",s.Page);
    }

    [Fact]
    public Task EveryShortcutBecomesOneKeyBindingOnItsCommandAndTheXamlKeepsNoSecondCopy()=>OverviewWorkspacePresentationTests.OnSta(()=>
    {
        // MainWindow builds its bindings from the table, so the XAML keeps no hand-written bindings that could disagree.
        var source=Source();
        Assert.Null(source.Root!.Element(Ui+"Window.InputBindings"));
        Assert.DoesNotContain(source.Descendants(),e=>e.Name.LocalName=="KeyBinding");
        Command panic=new(_=>{}),navigate=new(_=>{}),windows=new(_=>{}),find=new(_=>{});
        var bindings=ShellShortcuts.CreateBindings(panic,navigate,windows,find);
        Assert.Equal(ShellShortcuts.All.Count,bindings.Length);
        for(int i=0;i<bindings.Length;i++)
        {
            var row=ShellShortcuts.All[i];var gesture=Assert.IsType<KeyGesture>(bindings[i].Gesture);
            Assert.Equal((row.Key,row.Modifiers),(gesture.Key,gesture.Modifiers));
            Assert.Same(row.Action switch{ShellShortcutAction.Panic=>panic,ShellShortcutAction.Navigate=>navigate,ShellShortcutAction.ShowWindowsOverview=>windows,_=>find},bindings[i].Command);
            Assert.Equal(row.Page,bindings[i].CommandParameter);
        }
        // The packaged self-check accepts a window built this way.
        var window=SearchBoxHost(ShellShortcuts.SearchBoxes.Values);
        foreach(var binding in bindings)window.InputBindings.Add(binding);
        PackagedLayoutCheck.RequireShellShortcuts(window,panic,navigate,windows);
        return Task.CompletedTask;
    });

    [Fact]
    public Task PackagedCheckRefusesAWindowWhoseShortcutsDriftedFromTheTable()=>OverviewWorkspacePresentationTests.OnSta(()=>
    {
        Command panic=new(_=>{}),navigate=new(_=>{}),windows=new(_=>{}),find=new(_=>{});
        const string Message="The packaged window's keyboard shortcuts do not match the shortcut table the Guide lists.";
        // A missing binding.
        var missing=SearchBoxHost(ShellShortcuts.SearchBoxes.Values);
        foreach(var binding in ShellShortcuts.CreateBindings(panic,navigate,windows,find).Skip(1))missing.InputBindings.Add(binding);
        Assert.Equal(Message,Assert.Throws<InvalidOperationException>(()=>PackagedLayoutCheck.RequireShellShortcuts(missing,panic,navigate,windows)).Message);
        // F12 bound to something other than Panic.
        var swapped=SearchBoxHost(ShellShortcuts.SearchBoxes.Values);
        foreach(var binding in ShellShortcuts.CreateBindings(navigate,navigate,windows,find))swapped.InputBindings.Add(binding);
        Assert.Equal(Message,Assert.Throws<InvalidOperationException>(()=>PackagedLayoutCheck.RequireShellShortcuts(swapped,panic,navigate,windows)).Message);
        // A renamed search box that Ctrl+F could no longer reach.
        var renamed=SearchBoxHost(ShellShortcuts.SearchBoxes.Values.Skip(1));
        foreach(var binding in ShellShortcuts.CreateBindings(panic,navigate,windows,find))renamed.InputBindings.Add(binding);
        Assert.Equal(Message,Assert.Throws<InvalidOperationException>(()=>PackagedLayoutCheck.RequireShellShortcuts(renamed,panic,navigate,windows)).Message);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("Accounts","AccountsSearchBox","IsAccounts","{Binding Search,")]
    [InlineData("JoinGame","DestinationInput","IsJoinGame","{Binding LinkInput,")]
    [InlineData("Diagnostics","DiagnosticsFilterBox","IsDiagnostics","{Binding DiagnosticFilter,")]
    [InlineData("Settings","SettingsSearchBox","IsSettings","{Binding SettingsSearchQuery,")]
    public void CtrlFGoesToTheSearchBoxOfThePageThatIsOpen(string tab,string name,string page,string text)
    {
        Assert.Equal(name,ShellShortcuts.SearchBoxFor(tab));
        var box=Assert.Single(Source().Descendants(Ui+"TextBox"),e=>(string?)e.Attribute(X+"Name")==name);
        Assert.StartsWith(text,(string?)box.Attribute("Text"));
        // The box is on the page that tab shows, and its tooltip names the shortcut.
        Assert.Contains(box.Ancestors(),e=>(string?)e.Attribute("Visibility")=="{Binding "+page+", Converter={StaticResource VisibleIf}}");
        Assert.EndsWith(" · Ctrl+F",(string?)box.Attribute("ToolTip"));
    }

    [Theory]
    [InlineData("Overview")]
    [InlineData("Sessions")]
    [InlineData("Windows")]
    [InlineData("Automation")]
    [InlineData("Guide")]
    [InlineData("")]
    public void CtrlFLeavesPagesWithoutASearchBoxAlone(string tab)=>Assert.Null(ShellShortcuts.SearchBoxFor(tab));

    [Fact]
    public void TheFindLabelNamesEveryPageCtrlFReaches()
    {
        Assert.Equal(["Accounts","JoinGame","Diagnostics","Settings"],ShellShortcuts.SearchBoxes.Keys);
        Assert.Equal("Go to the search box on Accounts, Join a game, Diagnostics or Settings",ShellShortcuts.All.Single(s=>s.Action==ShellShortcutAction.FindOnPage).Label);
    }

    [Fact]
    public Task GuideListsTheShortcutTableRowByRow()=>OverviewWorkspacePresentationTests.OnSta(()=>
    {
        var source=Source();
        var guide=source.Descendants(Ui+"ScrollViewer").Single(e=>(string?)e.Attribute("Visibility")=="{Binding IsGuide, Converter={StaticResource VisibleIf}}");
        var list=Assert.Single(guide.Descendants(Ui+"ItemsControl"),e=>(string?)e.Attribute("ItemsSource")=="{x:Static local:ShellShortcuts.All}");
        // No hand-written Guide sentence names a Ctrl+digit key that the table could later change.
        var texts=guide.Descendants(Ui+"TextBlock").Select(e=>(string?)e.Attribute("Text")).OfType<string>().ToArray();
        Assert.DoesNotContain(texts,t=>Regex.IsMatch(t,@"Ctrl\+[0-9]"));
        Assert.DoesNotContain(texts,t=>t.Contains("sidebar sections",StringComparison.Ordinal));
        // Render the shipped list: it shows each row's gesture and label, in table order.
        XNamespace local="clr-namespace:Zenith.Sessions.App;assembly=Zenith.Sessions.App";
        var styles=source.Root!.Element(Ui+"Window.Resources")!.Elements(Ui+"Style").Where(e=>(string?)e.Attribute(X+"Key") is "Muted" or "SectionTitle").Select(e=>new XElement(e));
        var root=new XElement(Ui+"Border",new XAttribute(XNamespace.Xmlns+"x",X.NamespaceName),new XAttribute(XNamespace.Xmlns+"local",local.NamespaceName),
            new XElement(Ui+"Border.Resources",styles),new XElement(list));
        var view=(Border)XamlReader.Parse(root.ToString());
        view.Measure(new Size(720,2000));view.Arrange(new Rect(0,0,720,2000));view.UpdateLayout();
        Assert.Equal(ShellShortcuts.All.SelectMany(s=>new[]{s.Gesture,s.Label}),Visual(view).OfType<TextBlock>().Select(t=>t.Text));
        return Task.CompletedTask;
    });

    private static XDocument Source(){using var stream=typeof(ShellShortcutsTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;return XDocument.Load(stream);}
    // A stand-in for the window's name scope: one TextBox per given name.
    private static StackPanel SearchBoxHost(IEnumerable<string> names)
    {
        var root=new XElement(Ui+"StackPanel",new XAttribute(XNamespace.Xmlns+"x",X.NamespaceName),names.Select(name=>new XElement(Ui+"TextBox",new XAttribute(X+"Name",name))));
        return (StackPanel)XamlReader.Parse(root.ToString());
    }
    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Visual(child))yield return nested;}}
}
