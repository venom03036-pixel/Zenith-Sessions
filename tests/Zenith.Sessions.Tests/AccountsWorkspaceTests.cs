using System.Collections.ObjectModel;
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
using Xunit.Abstractions;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

[Collection(WpfProductionResourceCollection.Name)]
public sealed class AccountsWorkspaceTests
{
    private readonly ITestOutputHelper _output;
    public AccountsWorkspaceTests(ITestOutputHelper output)=>_output=output;
    private static readonly XNamespace Ui="http://schemas.microsoft.com/winfx/2006/xaml/presentation", X="http://schemas.microsoft.com/winfx/2006/xaml";
    public static IEnumerable<object[]> LayoutCases()
    {
        foreach(string theme in new[]{"graphite-mint","midnight","amber-night","ocean-blue","violet-dusk","custom-light"})
            foreach(bool dense in new[]{false,true})
                foreach(double width in new[]{728d,1088d}) yield return new object[]{theme,dense,width};
    }
    [Theory]
    [MemberData(nameof(LayoutCases))]
    public Task ProductionAccountsKeepToolbarPagingHeadshotsAndUsernamesReachable(string theme,bool dense,double width)=>OnSta(()=>
    {
        var model=new WorkspaceModel {Dense=dense,Narrow=width<900};
        using var fixture=new ProductionWorkspace(model,theme);var view=fixture.View;Layout(view,width,480);
        var toolbar=Named<WrapPanel>(view,"AccountToolbar");Assert.True(toolbar.ActualHeight>=36);
        foreach(var child in toolbar.Children.OfType<FrameworkElement>().Where(child=>child.Visibility==Visibility.Visible)) Reachable(view,child);
        var add=Visual(view).OfType<Button>().First(button=>Equals(button.Content,"+ Add account"));Reachable(view,add);Assert.True(add.ActualHeight>=36);
        var list=Named<ListBox>(view,"AccountList");Assert.True(VirtualizingPanel.GetIsVirtualizing(list));Assert.True(ScrollViewer.GetCanContentScroll(list));
        Assert.Same(model.Accounts,list.ItemsSource);Assert.Same(model.SelectedAccount,list.SelectedItem);Assert.InRange(list.ActualHeight,120,480);
        Assert.InRange(Visual(list).OfType<ListBoxItem>().Count(),1,30); // 100 metadata rows must remain virtualized.
        var first=Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        DiagnoseUsername(first);
        Assert.Contains(Visual(first).OfType<TextBlock>(),ShowsUsername);
        var headshot=Assert.Single(Visual(first).OfType<Image>());Assert.Same(model.Accounts[0].Avatar,headshot.Source);Assert.True(headshot.ActualWidth>=28);
        var placeholder=Assert.Single(Visual(first).OfType<System.Windows.Shapes.Path>().Where(path=>path.Name.EndsWith("AvatarPlaceholder",StringComparison.Ordinal)));
        Assert.Equal(Visibility.Collapsed,placeholder.Visibility);
        if(model.IsAccountInspectorVisible) Assert.Equal(Visibility.Collapsed,Named<System.Windows.Shapes.Path>(view,"InspectorAvatarPlaceholder").Visibility);
        var power=Assert.Single(Visual(first).OfType<Button>().Where(button=>Equals(button.Content,model.Accounts[0].PowerLabel)));
        Assert.Same(model.AccountPower,power.Command);Assert.Same(model.Accounts[0],power.CommandParameter);Assert.True(power.ActualHeight>=36);Reachable(view,power);
        foreach(string action in new[]{"Previous","Next"}) Reachable(view,Visual(view).OfType<Button>().Single(button=>Equals(button.Content,action)));
        var target=new RenderTargetBitmap((int)width,480,96,96,PixelFormats.Pbgra32);target.Render(view);
        var point=headshot.TransformToAncestor(view).Transform(new Point(headshot.ActualWidth/2,headshot.ActualHeight/2));
        byte[] pixel=new byte[4];target.CopyPixels(new Int32Rect((int)point.X,(int)point.Y,1,1),pixel,4,0);
        Assert.Equal(new byte[]{30,160,210,255},pixel); // Actual production Image draws the frozen fixture; no network.
        Assert.Equal("saved_player",model.Accounts[0].Profile.Username);Assert.Equal("Saved link",model.Accounts[0].Identity);
        return Task.CompletedTask;
    });

    public static IEnumerable<object[]> SelectedRowCases()
    {
        foreach(string palette in new[]{"high contrast black","high contrast white","graphite-mint","light"})
            foreach(bool dense in new[]{false,true})
                foreach(bool compact in new[]{true,false}) yield return new object[]{palette,dense,compact};
    }
    // Under Windows High Contrast the selection colour is the Highlight colour and the primary, secondary and muted text colours are
    // all the window text colour, about 1.4:1 on it. Every label drawn straight on the selected row (name, @username and group,
    // sign-in and launch state, dense group) and the account-actions glyph must use the selection text colour instead.
    [Theory]
    [MemberData(nameof(SelectedRowCases))]
    public Task TheSelectedAccountRowDrawsEveryLabelAndItsActionsGlyphReadablyOnTheSelectionColour(string palette,bool dense,bool compact)=>OnSta(()=>
    {
        var model=new WorkspaceModel {Dense=dense,CompactLayout=compact};
        using var fixture=new ProductionWorkspace(model,palette);var view=fixture.View;Layout(view,1088,480);
        Color selection=Assert.IsType<SolidColorBrush>(fixture.Resources["SelectionBackground"]).Color;
        var list=Named<ListBox>(view,"AccountList");var selected=Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.True(selected.IsSelected);var image=RenderedInk.Render(view);
        Assert.Equal(selection,RenderedInk.At(image,view,Assert.IsType<Border>(selected.Template.FindName("Row",selected)),new Point(4,4)));
        // The sign-in chip, the buttons and the check box draw their own fill, so only text with nothing but the row behind it is read here.
        var labels=Visual(selected).OfType<TextBlock>().Where(text=>Words(text).Length>0 && text.ActualWidth>0 && Shown(text,selected) && !OnItsOwnFill(text,selected)).ToArray();
        Assert.Equal(dense && compact?2:3,labels.Length);
        foreach(var label in labels)
        {
            RenderedInk.AssertReadable(image,view,label,selection,4.5,$"{palette}: '{Words(label)}' on the selected row");
            // In the app, the implicit TextBlock style in Application resources gives TextPrimary to any TextBlock whose colour would only
            // be inherited from the row. This fixture has no Application, so inheritance would pass here and fail there: the selected
            // colour must be set on each label by its own template or style.
            var source=DependencyPropertyHelper.GetValueSource(label,TextBlock.ForegroundProperty).BaseValueSource;
            Assert.True(source is not (BaseValueSource.Inherited or BaseValueSource.Default or BaseValueSource.ImplicitStyleReference),$"'{Words(label)}' only inherits its colour ({source}).");
        }
        if(!dense)
        {
            var actions=Visual(selected).OfType<Button>().Single(button=>AutomationProperties.GetName(button)=="Account actions");
            RenderedInk.AssertReadable(image,view,Visual(actions).OfType<System.Windows.Shapes.Path>().Single(),selection,3,$"{palette}: the account actions glyph on the selected row");
        }
        return Task.CompletedTask;
    });
    private static IEnumerable<DependencyObject> Between(DependencyObject element,DependencyObject ancestor)
    {for(var parent=VisualTreeHelper.GetParent(element);parent is not null && !ReferenceEquals(parent,ancestor);parent=VisualTreeHelper.GetParent(parent)) yield return parent;}
    private static bool Shown(UIElement element,DependencyObject row)=>element.Visibility==Visibility.Visible && Between(element,row).OfType<UIElement>().All(parent=>parent.Visibility==Visibility.Visible);
    // A TextBlock built from Runs (the @username line, the compact status line) keeps its words in the Runs, not in Text.
    private static string Words(TextBlock text)=>text.Text.Length>0?text.Text:string.Concat(text.Inlines.OfType<System.Windows.Documents.Run>().Select(run=>run.Text));
    // Buttons, the check box and the chips are all content controls inside the row's own ListBoxItem.
    private static bool OnItsOwnFill(DependencyObject element,DependencyObject row)=>Between(element,row).Any(parent=>parent is ContentControl);

    [Fact]
    public Task OverflowUsesPlacementTargetContextAndPreservesAllExistingFileProfileHandlers()=>OnSta(()=>
    {
        var model=new WorkspaceModel();using var fixture=new ProductionWorkspace(model,"midnight");Layout(fixture.View,1088,480);
        var button=Named<Button>(fixture.View,"AccountLibraryTools");Assert.Equal("Account library tools",AutomationProperties.GetName(button));
        var menu=button.ContextMenu!;menu.PlacementTarget=button;
        BindingOperations.GetBindingExpression(menu,FrameworkElement.DataContextProperty)!.UpdateTarget();Flush();
        Assert.Same(model,menu.DataContext);
        var items=menu.Items.OfType<MenuItem>().ToArray();Assert.Equal(5,items.Length);
        foreach(var item in items) {Assert.Same(model,item.DataContext);Assert.True(item.IsEnabled);}
        model.PortableAllowed=false;Flush();
        Assert.All(new[]{items[0],items[1],items[4]},item=>Assert.True(item.IsEnabled));
        Assert.All(new[]{items[2],items[3]},item=>Assert.False(item.IsEnabled));
        model.Ready=false;Flush();Assert.All(items,item=>Assert.False(item.IsEnabled));
        var source=Document();var original=source.Descendants(Ui+"Button").Single(element=>(string?)element.Attribute(X+"Name")=="AccountLibraryTools");
        Assert.Equal(new[]{"ImportProfiles","ExportProfiles","ExportProtectedBackup","ImportProtectedBackup","AddProfile"},original.Descendants(Ui+"MenuItem").Select(item=>(string?)item.Attribute("Click")));
        Assert.All(original.Descendants(Ui+"MenuItem").Where(item=>((string?)item.Attribute("Click"))?.Contains("ProtectedBackup")==true),
            item=>Assert.Equal("{Binding CanUsePortableBackup}",(string?)item.Attribute("IsEnabled")));
        Assert.Contains(original.Descendants(Ui+"ContextMenu"),element=>(string?)element.Attribute("DataContext")=="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}");
        return Task.CompletedTask;
    });

    [Fact]
    public Task NarrowDetailsCollapseKeepsTheSameInspectorSelectionAndExpandedContent()=>OnSta(()=>
    {
        var model=new WorkspaceModel();using var fixture=new ProductionWorkspace(model,"graphite-mint");var view=fixture.View;Layout(view,1088,480);
        var inspector=Named<Border>(view,"AccountInspector");var scroll=Named<ScrollViewer>(view,"AccountInspectorScroll");var list=Named<ListBox>(view,"AccountList");
        var expanders=Visual(inspector).OfType<Expander>().ToArray();Assert.Equal(2,expanders.Length);foreach(var item in expanders) item.IsExpanded=true;
        Layout(view,1088,480);scroll.ScrollToBottom();Flush();Assert.True(scroll.ScrollableHeight>0);
        var selected=list.SelectedItem;var accounts=list.ItemsSource;
        model.Narrow=true;Layout(view,728,480);double closedWidth=list.ActualWidth;
        Assert.Equal(Visibility.Collapsed,inspector.Visibility);Assert.Same(selected,list.SelectedItem);Assert.Same(accounts,list.ItemsSource);
        var toggle=Visual(view).OfType<Button>().Single(button=>AutomationProperties.GetName(button)=="Toggle account details");
        Assert.Equal(Visibility.Visible,toggle.Visibility);Assert.Same(model.ToggleAccountInspector,toggle.Command);Reachable(view,toggle);
        toggle.Command!.Execute(null);Layout(view,728,480);
        Assert.Equal(Visibility.Visible,inspector.Visibility);Assert.Same(inspector,Named<Border>(view,"AccountInspector"));Assert.Same(scroll,Named<ScrollViewer>(view,"AccountInspectorScroll"));
        Assert.All(expanders,item=>Assert.True(item.IsExpanded));Assert.True(closedWidth-list.ActualWidth>=280);
        scroll.ScrollToBottom();Flush();view.UpdateLayout();
        Reachable(view,Visual(inspector).OfType<Button>().Single(button=>Equals(button.Content,"Remove profile")));
        Assert.Same(selected,list.SelectedItem);Assert.Equal(0,model.Executions);
        return Task.CompletedTask;
    });

    [Fact]
    public Task DenseAndNormalRowsRetainNeutralFallbackWhenPublicImageIsUnavailable()=>OnSta(()=>
    {
        var model=new WorkspaceModel();model.Accounts[0].ClearPublicPresentation();
        using var fixture=new ProductionWorkspace(model,"custom-light");var view=fixture.View;
        foreach(bool dense in new[]{false,true})
        {
            model.Dense=dense;Layout(view,1088,480);var list=Named<ListBox>(view,"AccountList");
            var first=Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));var image=Assert.Single(Visual(first).OfType<Image>());
            Assert.Null(image.Source);Assert.Equal(Visibility.Collapsed,image.Visibility);
            Assert.Contains(Visual(first).OfType<System.Windows.Shapes.Path>(),path=>path.ActualWidth>0 && path.Visibility==Visibility.Visible);
            Assert.Equal(Visibility.Visible,Named<System.Windows.Shapes.Path>(fixture.View,"InspectorAvatarPlaceholder").Visibility);
            Assert.Contains(Visual(first).OfType<TextBlock>(),ShowsUsername);
        }
        Assert.Equal("Saved link",model.Accounts[0].Identity);Assert.Equal("No verified client",model.Accounts[0].LaunchStatus);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SetupCancellationIsReachableWithoutASelectedAccountAndInspectorRemainsInspectable()=>OnSta(()=>
    {
        var model=new WorkspaceModel {Narrow=true,IsAuthorizing=true};model.SelectedAccount=null;
        using var fixture=new ProductionWorkspace(model,"midnight");Layout(fixture.View,728,480);
        var toolbar=Named<WrapPanel>(fixture.View,"AccountToolbar");var cancel=Visual(toolbar).OfType<Button>().Single(button=>Equals(button.Content,"Cancel account setup"));
        Assert.Same(model.CancelAuthorization,cancel.Command);Reachable(fixture.View,cancel);cancel.Command!.Execute(null);Assert.Equal(1,model.Executions);
        model.ToggleAccountInspector.Execute(null);Layout(fixture.View,728,480);
        var inspector=Named<Border>(fixture.View,"AccountInspector");Assert.Contains(Visual(inspector).OfType<TextBlock>(),text=>text.Text=="Select an account");
        Assert.Contains(Visual(inspector).OfType<Button>(),button=>Equals(button.Content,"Cancel account setup") && button.Visibility==Visibility.Visible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AccountsBreakpointAndToggleDoNotChangeActualShellSelectionOrSettingsDraft()=>OnSta(async()=>
    {
        string root=Path.Combine(Path.GetTempPath(),"zenith-accounts-layout-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using var shell=new ShellViewModel(root,true,Dispatcher.CurrentDispatcher,new NoDesktop(),new NoDirectory(),new NoServers(),previewProvider:new NoPreview());
            var selected=new AccountRow(AccountProfile.Create("Kept local alias"));shell.Accounts.Add(selected);shell.SelectedAccount=selected;shell.SpareRam="4.25";
            Assert.True(shell.IsAccountInspectorVisible);shell.SetAccountsViewportWidth(728);Assert.False(shell.IsAccountInspectorVisible);
            shell.ToggleAccountInspector.Execute(null);Assert.True(shell.IsAccountInspectorVisible);shell.SetAccountsViewportWidth(1088);Assert.True(shell.IsAccountInspectorVisible);
            shell.SetAccountsViewportWidth(728);Assert.True(shell.IsAccountInspectorVisible);shell.ToggleAccountInspector.Execute(null);Assert.False(shell.IsAccountInspectorVisible);
            shell.SetAccountsViewportWidth(double.NaN);Assert.False(shell.IsAccountInspectorVisible);
            Assert.Same(selected,shell.SelectedAccount);Assert.Same(selected,Assert.Single(shell.Accounts));Assert.Equal("4.25",shell.SpareRam);
            Assert.False(shell.IsReady); // Layout did not initialize services or issue any fake work.
        }
        finally
        {
            string full=Path.GetFullPath(root);
            if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-accounts-layout-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
            if(Directory.Exists(full)) Directory.Delete(full,true);
        }
    });

    private sealed class ProductionWorkspace:IDisposable
    {
        public Grid View {get;}
        public ResourceDictionary Resources=>_ancestor.Resources;
        private readonly Window _ancestor;
        public ProductionWorkspace(WorkspaceModel model,string theme)
        {
            _=Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml",UriKind.Relative));
            var document=Document();var region=new XElement(document.Descendants(Ui+"Grid").Single(element=>(string?)element.Attribute(X+"Name")=="AccountsWorkspace"));
            foreach(var attr in region.DescendantsAndSelf().Attributes().Where(attr=>attr.Name.LocalName is "Click" or "MouseDoubleClick").ToArray()) attr.Remove();
            string[] keys=["SavedGroupFilterTemplate","SavedGroupFilterCombo","SectionTitle","Muted","Panel","AccountItem","AccountRow","DenseAccountRow"];
            var entries=document.Root!.Element(Ui+"Window.Resources")!.Elements().Where(element=>keys.Contains((string?)element.Attribute(X+"Key"))).Select(element=>new XElement(element)).ToArray();
            foreach(var attr in entries.SelectMany(element=>element.DescendantsAndSelf().Attributes("Click")).ToArray()) attr.Remove();
            var dict=new XElement(Ui+"ResourceDictionary",new XAttribute(XNamespace.Xmlns+"x",X.NamespaceName),new XAttribute(XNamespace.Xmlns+"common","clr-namespace:MacroSuite.App.Common;assembly=Zenith.Sessions.App"),
                new XElement(Ui+"ResourceDictionary.MergedDictionaries",new XElement(Ui+"ResourceDictionary",new XAttribute("Source","/Zenith.Sessions.App;component/Themes/SessionsControls.xaml"))),
                new XElement(Ui+"BooleanToVisibilityConverter",new XAttribute(X+"Key","VisibleIf")),new XElement(XName.Get("InverseBoolToVisibilityConverter","clr-namespace:MacroSuite.App.Common;assembly=Zenith.Sessions.App"),new XAttribute(X+"Key","HiddenIf")),entries);
            // An ordinary Window supplies the production name scope and ancestor bindings.
            // A named visual subtree cannot be registered under ResourceDictionary scope.
            var window=new XElement(Ui+"Window",new XAttribute(XNamespace.Xmlns+"x",X.NamespaceName),
                new XElement(Ui+"Window.Resources",dict),region);
            _ancestor=(Window)XamlReader.Parse(window.ToString());var resources=_ancestor.Resources;
            // Light and High Contrast are the Sessions palettes, applied as the app applies them.
            if(theme=="light" || theme.StartsWith("high contrast ",StringComparison.Ordinal)) SessionsThemes.Apply(resources,SessionsThemesTests.Palette(theme));
            else ThemeManager.Apply(resources,theme=="custom-light"?"graphite-mint":theme);
            if(theme=="custom-light") foreach(var pair in new Dictionary<string,string>{["AppBackground"]="#FFFFFF",["CardBackground"]="#FAFAFA",["SurfaceBackground"]="#EEEEEE",["TextPrimary"]="#151515",["TextSecondary"]="#454545",["TextMuted"]="#555555",["SelectionBackground"]="#DDE8EE",["SelectionForeground"]="#151515"}) resources[pair.Key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            View=(Grid)_ancestor.Content;_ancestor.DataContext=model;
            View.SetResourceReference(Panel.BackgroundProperty,"AppBackground"); // Window is never shown.
        }
        public void Dispose(){_ancestor.Content=null;_ancestor.Close();View.DataContext=null;}
    }
    private sealed class WorkspaceModel:Observable
    {
        private bool _dense,_narrow,_open,_ready=true;
        private AccountRow? _selected;
        public WorkspaceModel()
        {
            byte[] pixels=new byte[150*150*4];for(int p=0;p<pixels.Length;p+=4){pixels[p]=30;pixels[p+1]=160;pixels[p+2]=210;pixels[p+3]=255;}
            var bitmap=BitmapSource.Create(150,150,96,96,PixelFormats.Bgra32,null,pixels,600);bitmap.Freeze();
            for(int i=0;i<100;i++)
            {
                var profile=AccountProfile.Create(new string('L',100)) with {Username="saved_player",Group=new string('G',60),Notes="Synthetic notes only.",PreferredPlaceId=42};var row=new AccountRow(profile);
                var now=DateTimeOffset.UtcNow;row.SetCachedAuthorization(new(profile.Id,"metadata",RobloxSessionIdentity.Provider,RobloxSessionIdentity.Issuer,"123",123,"saved_player",1,AuthorizationHealth.Linked,now.AddHours(1),now));
                row.SetPublicPresentation(new(123,"saved_player","Public display name",bitmap,PublicDirectoryState.Success,now,1));Accounts.Add(row);
            }
            _selected=Accounts[0];AccountPower=new(_=>Executions++);CancelAuthorization=new(_=>Executions++);ToggleAccountInspector=new(_=>{_open=!_open;Raise(nameof(IsAccountInspectorVisible));Raise(nameof(AccountInspectorToggleLabel));});
        }
        public ObservableCollection<AccountRow> Accounts {get;}=[];
        public AccountRow? SelectedAccount {get=>_selected;set {Set(ref _selected,value);Raise(nameof(HasSelection));}}
        public bool HasSelection=>SelectedAccount is not null;
        public bool IsAccounts=>true;
        public bool CompactLayout {get;init;}=true;
        public bool Dense {get=>_dense;set=>Set(ref _dense,value);}
        public bool Narrow {get=>_narrow;set {Set(ref _narrow,value);Raise(nameof(IsAccountInspectorNarrow));Raise(nameof(IsAccountInspectorVisible));}}
        public bool IsAccountInspectorNarrow=>Narrow;
        public bool IsAccountInspectorVisible=>!Narrow||_open;
        public string AccountInspectorToggleLabel=>_open?"Hide details":"Details";
        public bool Ready {get=>_ready;set {Set(ref _ready,value);Raise(nameof(IsReady));Raise(nameof(CanUsePortableBackup));}}
        public bool IsReady=>Ready;
        private bool _portableAllowed=true;
        public bool PortableAllowed {get=>_portableAllowed;set {Set(ref _portableAllowed,value);Raise(nameof(CanUsePortableBackup));}}
        public bool CanUsePortableBackup=>Ready&&PortableAllowed;
        public bool IsAuthorizing {get;set;}
        public bool CanAddRobloxAccount=>true;
        public bool QueueReady=>false;
        public string Search {get;set;}="";
        public GroupFilter[] Groups {get;}=[GroupFilter.All,new("Saved group","Saved group")];
        public GroupFilter Group {get;set;}=GroupFilter.All;
        public AccountSortChoice[] SortChoices {get;}=[new("Name",AccountOrder.Name)];
        public AccountSortChoice SortChoice {get;set;}=new("Name",AccountOrder.Name);
        public string GroupChoicesNotice=>"Bounded group choices.";
        public string EmptyAccountsText=>"No accounts match.";
        public string PageLabel=>"1–100 of 200 saved profiles";
        public bool CanPrevious=>false;
        public bool CanNext=>true;
        public int Executions;
        public Command AccountPower {get;}
        public Command CancelAuthorization {get;}
        public Command ToggleAccountInspector {get;}
        public Command AddRobloxAccount {get;}=new(_=>{});
        public Command PreviousPage {get;}=new(_=>{});
        public Command NextPage {get;}=new(_=>{});
        public Command QueueSelected {get;}=new(_=>{});
        public Command UseSelectedPreferredPlace {get;}=new(_=>{});
        public Command LinkAccount {get;}=new(_=>{});
        public Command RefreshAuthorization {get;}=new(_=>{});
        public Command ToggleFavorite {get;}=new(_=>{});
        public Command SetSelectedAsMain {get;}=new(_=>{});
        public Command RefreshPublicAccount {get;}=new(_=>{});
    }
    [Fact]
    public void LockOverlayDoesNotPromiseReconnectWhileReconnectIsUnavailable()
    {
        var reconnect=Assert.Single(ProviderCapabilities.Current,capability=>capability.Name=="Named-account launch & reconnect");
        Assert.Equal(CapabilityState.Unavailable,reconnect.State);
        var overlay=Assert.Single(Document().Descendants(Ui+"TextBlock"),element=>((string?)element.Attribute("Text"))?.StartsWith("Account details are hidden",StringComparison.Ordinal)==true);
        Assert.Contains(overlay.Ancestors(Ui+"Border"),border=>((string?)border.Attribute("Visibility"))?.Contains("IsLocked",StringComparison.Ordinal)==true);
        string text=(string)overlay.Attribute("Text")!;
        Assert.DoesNotContain("reconnect",text,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("automation requires an explicit restart",text,StringComparison.Ordinal);
    }
    private static XDocument Document(){using var input=typeof(AccountsWorkspaceTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;return XDocument.Load(input);}
    private static T Named<T>(DependencyObject root,string name) where T:FrameworkElement=>Visual(root).OfType<T>().Single(element=>element.Name==name);
    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Visual(child))yield return nested;}}
    private static void Layout(FrameworkElement view,double width,double height){view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));Flush();view.UpdateLayout();}
    private static void Flush()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    private void Reachable(FrameworkElement root,FrameworkElement item)
    {
        var bounds=item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));
        if(bounds.Left<0 || bounds.Right>root.ActualWidth+.5 || bounds.Top<0 || bounds.Bottom>root.ActualHeight+.5)
            _output.WriteLine($"Bounds: {item.GetType().Name} {item.Name} {(item as ContentControl)?.Content}, {bounds}; root {root.RenderSize}; height {item.Height}/{item.ActualHeight}; margin {item.Margin}.");
        Assert.True(item.ActualWidth>0 && item.ActualHeight>0);Assert.InRange(bounds.Left,0,root.ActualWidth);Assert.InRange(bounds.Right,0,root.ActualWidth+.5);Assert.InRange(bounds.Top,0,root.ActualHeight);Assert.InRange(bounds.Bottom,0,root.ActualHeight+.5);
    }
    private void DiagnoseUsername(DependencyObject item)
    {
        var texts=Visual(item).OfType<TextBlock>().ToArray();
        if(texts.Any(ShowsUsername)) return;
        foreach(var text in texts)
        {
            _output.WriteLine($"Text {text.Name}: '{text.Text}', {text.Visibility}, size {text.RenderSize}, data {text.DataContext?.GetType().Name}, binding {text.GetBindingExpression(TextBlock.TextProperty)?.Status}.");
            foreach(var run in text.Inlines.OfType<System.Windows.Documents.Run>())
                _output.WriteLine($" Run '{run.Text}', data {run.DataContext?.GetType().Name}, binding {BindingOperations.GetBindingExpression(run,System.Windows.Documents.Run.TextProperty)?.Status}.");
        }
    }
    private static bool ShowsUsername(TextBlock text)=>text.Visibility==Visibility.Visible && text.ActualWidth>0 && text.ActualHeight>0
        && (text.Text.Contains("@saved_player") || text.Inlines.OfType<System.Windows.Documents.Run>().Any(run=>run.Text.Contains("@saved_player")));
    private static Task OnSta(Func<Task> work)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async()=>{try{await work();done.TrySetResult();}catch(Exception error){done.TrySetException(error);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private sealed class NoDesktop:IDesktopMonitor
    {public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)=>throw new InvalidOperationException("No sampling expected.");public Task<bool> FocusAsync(SessionObservation row,CancellationToken ct)=>throw new InvalidOperationException("No focus expected.");public Task<bool> RequestCloseAsync(SessionObservation row,CancellationToken ct)=>throw new InvalidOperationException("No close expected.");}
    private sealed class NoDirectory:IPublicRobloxDirectory
    {public Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long id,CancellationToken ct)=>throw new InvalidOperationException("No network expected.");public Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long id,CancellationToken ct)=>throw new InvalidOperationException("No network expected.");public Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar,CancellationToken ct)=>throw new InvalidOperationException("No network expected.");public void ClearMemoryCache(){}}
    private sealed class NoServers:IRobloxPublicServerDirectory
    {public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query,CancellationToken ct)=>throw new InvalidOperationException("No network expected.");}
    private sealed class NoPreview:IClientPreviewProvider
    {public Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets,long generation,CancellationToken ct)=>throw new InvalidOperationException("No capture expected.");public Task ReleaseAsync(CancellationToken ct)=>Task.CompletedTask;public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
}
