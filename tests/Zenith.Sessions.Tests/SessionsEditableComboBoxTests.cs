using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

[Collection(WpfProductionResourceCollection.Name)]
public sealed class SessionsEditableComboBoxTests
{
    [Theory]
    [InlineData("graphite-mint")]
    [InlineData("midnight")]
    [InlineData("amber-night")]
    [InlineData("ocean-blue")]
    [InlineData("violet-dusk")]
    public Task NamedEditablePartActuallyRendersItsTextWithEveryProductionTheme(string theme) => OnSta(() =>
    {
        var resources = Resources(theme); var combo = Combo(resources); combo.Text = "Bubble Gum";
        Layout(combo); var field = Editable(combo); field.ApplyTemplate();
        Assert.Equal("Bubble Gum", field.Text); Assert.Equal(Visibility.Visible, field.Visibility);
        Assert.InRange(field.ActualWidth, 200, 360); Assert.True(field.ActualHeight > 0);
        Assert.IsType<ScrollViewer>(field.Template.FindName("PART_ContentHost", field));
        Assert.Equal(((SolidColorBrush)resources["TextPrimary"]).Color, ((SolidColorBrush)field.Foreground).Color);
        Assert.Equal(((SolidColorBrush)resources["TextPrimary"]).Color, ((SolidColorBrush)field.CaretBrush).Color);
        Assert.Equal(((SolidColorBrush)resources["SurfaceBackground"]).Color, ((SolidColorBrush)Chrome(combo).Background).Color);

        // Rendering only the transparent, unframed editor means nontransparent pixels
        // must come from its actual text, rather than the arrow or surrounding chrome.
        var image = new RenderTargetBitmap((int)Math.Ceiling(field.ActualWidth), (int)Math.Ceiling(field.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(field); byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        int drawn = 0; for (int index = 3; index < pixels.Length; index += 4) if (pixels[index] != 0) drawn++;
        Assert.True(drawn > 20, "The editable part must render text, not merely expose ComboBox.Text to automation.");
        Assert.False(Popup(combo).IsOpen); return Task.CompletedTask;
    });

    [Fact]
    public Task SelectionTypingAndModelChangesShareTheRealEditableTextWithoutReplacingTheBinding() => OnSta(() =>
    {
        var combo = Combo(Resources()); var draft = new Draft { Name = "Bubble Gum" };
        combo.SetBinding(ComboBox.TextProperty, new Binding(nameof(Draft.Name))
        { Source = draft, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Layout(combo); var field = Editable(combo); Assert.Equal("Bubble Gum", field.Text);
        combo.SelectedItem = "Card Clash"; Pump(); Assert.Equal("Card Clash", field.Text); Assert.Equal("Card Clash", draft.Name);
        field.SelectAll(); field.SelectedText = "Typed group"; Pump();
        Assert.Equal("Typed group", field.Text); Assert.Equal("Typed group", combo.Text); Assert.Equal("Typed group", draft.Name);
        Assert.True(BindingOperations.IsDataBound(combo, ComboBox.TextProperty));
        draft.Name = "Updated outside the list"; Pump(); Assert.Equal(draft.Name, field.Text);
        Assert.Equal(Visibility.Collapsed, ((ContentPresenter)combo.Template.FindName("SelectionPresenter", combo)).Visibility);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ReadOnlyAndDisabledStatesPreserveTextAndKeepOneAccessibleEditingTabStop() => OnSta(() =>
    {
        var combo = Combo(Resources()); combo.Text = "Bubble Gum";
        AutomationProperties.SetName(combo, "Existing group name"); Layout(combo);
        var field = Editable(combo); var toggle = (ToggleButton)combo.Template.FindName("DropDownToggle", combo);
        Assert.Equal("Existing group name", AutomationProperties.GetName(field));
        Assert.True(field.Focusable); Assert.True(field.IsTabStop); Assert.False(combo.IsTabStop); Assert.False(toggle.Focusable); Assert.False(toggle.IsTabStop);
        Assert.NotNull(combo.FocusVisualStyle); Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(combo));
        Assert.Equal(220, ((Border)Popup(combo).Child).MaxHeight); Assert.False(Popup(combo).IsOpen);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(combo)); Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(combo));
        Assert.IsType<VirtualizingStackPanel>(combo.ItemsPanel.LoadContent());
        combo.IsReadOnly = true; Pump(); Assert.True(field.IsReadOnly); Assert.Equal("Bubble Gum", field.Text);
        combo.IsEnabled = false; Pump(); Assert.False(field.IsEnabled); Assert.False(toggle.IsEnabled); Assert.Equal(.45, Chrome(combo).Opacity);
        combo.IsEnabled = true; combo.IsReadOnly = false; Pump();
        Assert.True(field.IsEnabled); Assert.False(field.IsReadOnly); Assert.Equal(1, Chrome(combo).Opacity); Assert.Equal("Bubble Gum", field.Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ActualAddToGroupDialogUsesTheCompanionStyleAndShowsItsEditableValue() => OnSta(() =>
    {
        var resources = Resources();
        var dialog = new AccountGroupDialog(2, ["Bubble Gum", "Card Clash"], _ => throw new InvalidOperationException("No save expected."));
        try
        {
            dialog.Resources.MergedDictionaries.Add(resources);
            var root = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
            var combo = Assert.Single(Logical(root).OfType<ComboBox>()); combo.Text = "Bubble Gum";
            root.Measure(new Size(480, 330)); root.Arrange(new Rect(0, 0, 480, 330)); Pump(); root.UpdateLayout();
            Assert.Same(resources["SessionsEditableComboBox"], combo.Style); Assert.Equal("Bubble Gum", Editable(combo).Text);
            Assert.Equal("Account group name", AutomationProperties.GetName(Editable(combo)));
            Assert.Equal(new[] { "Bubble Gum", "Card Clash" }, combo.Items.Cast<string>()); Assert.False(Popup(combo).IsOpen);
            Assert.False(dialog.IsVisible);
        }
        finally { dialog.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task CompanionStyleRetainsTheSharedBaseWithoutReplacingTheHostsImplicitTemplate() => OnSta(() =>
    {
        var resources = Resources(); var companion = Assert.IsType<Style>(resources["SessionsEditableComboBox"]);
        var shared = Assert.IsType<Style>(resources[typeof(ComboBox)]); Assert.Same(shared, companion.BasedOn);
        var host = new ComboBox { Resources = resources, Style = shared, IsEditable = false, ItemsSource = new[] { "Bubble Gum" }, SelectedIndex = 0 };
        Layout(host); Assert.Null(host.Template.FindName("PART_EditableTextBox", host)); Assert.NotNull(host.Template.FindName("ContentSite", host));
        var sessions = Combo(resources); Layout(sessions); Assert.NotSame(host.Template, sessions.Template); Assert.NotNull(Editable(sessions));
        return Task.CompletedTask;
    });

    private static ResourceDictionary Resources(string theme = "graphite-mint")
    {
        // LoadComponent initializes WPF's application resource/pack-URI plumbing even
        // when this fixture is the first WPF resource consumer in the test process.
        // It loads the compiled dictionary without constructing a global Application.
        var resources = Assert.IsType<ResourceDictionary>(Application.LoadComponent(
            new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml", UriKind.Relative)));
        ThemeManager.Apply(resources, theme); return resources;
    }
    private static ComboBox Combo(ResourceDictionary resources) => new()
    {
        Resources = resources, Style = (Style)resources["SessionsEditableComboBox"], IsEditable = true,
        ItemsSource = new[] { "Bubble Gum", "Card Clash" }, MaxDropDownHeight = 220
    };
    private static TextBox Editable(ComboBox combo) => Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
    private static Border Chrome(ComboBox combo) => Assert.IsType<Border>(combo.Template.FindName("Chrome", combo));
    private static Popup Popup(ComboBox combo) => Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
    private static void Layout(ComboBox combo)
    { combo.ApplyTemplate(); combo.Measure(new Size(360, 40)); combo.Arrange(new Rect(0, 0, 360, 40)); Pump(); combo.UpdateLayout(); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Logical(child)) yield return descendant;
    }
    private sealed class Draft : INotifyPropertyChanged
    {
        private string _name = "";
        public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private static Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
