using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

[Collection(WpfProductionResourceCollection.Name)]
public sealed class GroupPresentationTests
{
    [Theory]
    [InlineData("graphite-mint")]
    [InlineData("midnight")]
    [InlineData("amber-night")]
    [InlineData("ocean-blue")]
    [InlineData("violet-dusk")]
    public Task ThemeSwatchFollowsThemeChangesAndCustomColorNeverChangesItsTextForeground(string theme) => OnSta(() =>
    {
        var resources = Resources(theme); var swatch = new GroupColorSwatch { Resources = resources };
        Assert.Equal(ColorOf(resources["AccentPrimary"]), ColorOf(swatch.Background)); Assert.Equal(ColorOf(resources["ControlBorder"]), ColorOf(swatch.BorderBrush));
        swatch.ColorHex = "#aabbcc"; Assert.Equal(Color.FromRgb(0xAA, 0xBB, 0xCC), ColorOf(swatch.Background)); Assert.True(swatch.Background.IsFrozen);
        ThemeManager.Apply(resources, theme == "ocean-blue" ? "amber-night" : "ocean-blue"); Pump();
        Assert.Equal(Color.FromRgb(0xAA, 0xBB, 0xCC), ColorOf(swatch.Background));
        swatch.ColorHex = null; Pump(); Assert.Equal(ColorOf(resources["AccentPrimary"]), ColorOf(swatch.Background));
        swatch.ColorHex = "#unfinished"; Assert.Equal(ColorOf(resources["AccentPrimary"]), ColorOf(swatch.Background));
        Assert.Throws<ArgumentException>(() => GroupPresentation.ColorInput("#unfinished"));
        Assert.Equal(9, GroupPresentation.Colors.Count); Assert.Equal("Theme accent", GroupPresentation.Colors[0].Name); Assert.Null(GroupPresentation.Colors[0].ColorHex);
        return Task.CompletedTask;
    });

    [Fact]
    public Task EditableCatalogPickerShowsNamesAndColorTextAndRetainsAnOutOfPageTypedName() => OnSta(() =>
    {
        var definitions = new[] { Group("Raid", null), Group("Family", "#112233", 1) }; var resources = Resources();
        var picker = GroupPresentation.EditablePicker(definitions, "Outside current page", "Primary group"); picker.Resources = resources; Layout(picker, 360, 40);
        Assert.Equal("Outside current page", Editor(picker).Text); Assert.Equal("Primary group", AutomationProperties.GetName(Editor(picker)));
        picker.SelectedIndex = 1; Pump(); Assert.Equal("Family", picker.Text); Assert.Equal("Family", Editor(picker).Text);
        Assert.DoesNotContain("AccountGroupDefinition", picker.Text); picker.Text = "Typed new group"; Pump(); Assert.Equal("Typed new group", Editor(picker).Text);
        // Let WPF prepare the template through the same presenter used for items;
        // directly calling LoadContent on an unprepared factory template is invalid.
        var row = new ContentPresenter { ContentTemplate = picker.ItemTemplate, Content = picker.Items[0], Resources = resources }; Layout(row, 360, 36);
        Assert.Contains(Visual(row).OfType<TextBlock>(), text => text.Text == "Raid"); Assert.Contains(Visual(row).OfType<TextBlock>(), text => text.Text == "Theme accent");
        Assert.Equal(ColorOf(resources["AccentPrimary"]), ColorOf(Assert.Single(Visual(row).OfType<GroupColorSwatch>()).Background));
        Assert.Equal(240, picker.MaxDropDownHeight); Assert.False(picker.IsDropDownOpen);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ProfileCatalogPickerSavesOnlyTheChosenNameAndKeepsItsFixedFooterAndOtherMetadata() => OnSta(() =>
    {
        var original = (AccountProfile.Create("Kept alias") with { Group = "Outside current page", AdditionalGroups = "Keep\nOther", RobloxUserId = 123, Notes = "Retained notes", Priority = 31 }).Validated();
        AccountProfile? submitted = null;
        var dialog = new ProfileDialog(original, draft => { submitted = draft; return Task.FromException(new IOException("Synthetic keep-open failure.")); }, [Group("Raid", "#112233")]);
        try
        {
            dialog.Resources.MergedDictionaries.Add(Resources()); var root = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content); Layout(root, 480, 360);
            var picker = Assert.Single(Logical(root).OfType<ComboBox>()); Assert.Equal(original.Group, Editor(picker).Text);
            picker.Text = "Raid"; Pump(); var save = Action(root, "Save profile"); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(root, 480, 360);
            Assert.Equal(original with { Group = "Raid" }, submitted); Assert.Null(dialog.DialogResult);
            Reachable(root, save); Reachable(root, Action(root, "Cancel")); Assert.Equal("Raid", Editor(picker).Text);
        }
        finally { dialog.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task BulkMembershipCatalogPickerPassesAPlainNameAndDoesNotExposeDefinitionSerialization() => OnSta(() =>
    {
        string? selected = null;
        var dialog = new AccountGroupDialog(2, ["Legacy suggestion"], name => { selected = name; return Task.FromException(new ArgumentException("Synthetic keep-open failure.")); }, [Group("Raid", "#112233")]);
        try
        {
            dialog.Resources.MergedDictionaries.Add(Resources()); var root = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content); Layout(root, 480, 330);
            var picker = Assert.Single(Logical(root).OfType<ComboBox>()); picker.SelectedIndex = 0; Pump();
            Assert.Equal("Raid", Editor(picker).Text); Action(root, "Add to group").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Raid", selected); Assert.Null(dialog.DialogResult); Assert.DoesNotContain("ColorHex", selected!);
        }
        finally { dialog.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PickerRejectsAmbiguousOrUnboundedSuggestionSnapshots(bool duplicate) => OnSta(() =>
    {
        var one = Group("First", null);
        IReadOnlyList<AccountGroupDefinition> groups = duplicate ? new[] { one, one with { Name = "Different name" } }
            : Enumerable.Range(0, 1001).Select(index => Group("Group " + index, null, index)).ToArray();
        Assert.Throws<ArgumentException>(() => GroupPresentation.EditablePicker(groups, "First", "Test group"));
        return Task.CompletedTask;
    });

    private static AccountGroupDefinition Group(string name, string? color, long order = 0) => new(Guid.NewGuid(), name, color, order, 1, DateTimeOffset.UtcNow);
    private static ResourceDictionary Resources(string theme = "graphite-mint")
    {
        var resources = Assert.IsType<ResourceDictionary>(Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml", UriKind.Relative)));
        ThemeManager.Apply(resources, theme); return resources;
    }
    private static Color ColorOf(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static TextBox Editor(ComboBox picker) => Assert.IsType<TextBox>(picker.Template.FindName("PART_EditableTextBox", picker));
    private static Button Action(DependencyObject root, string text) => Logical(root).OfType<Button>().Single(button => Equals(button.Content, text));
    private static void Layout(FrameworkElement element, double width, double height)
    { element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); Pump(); element.UpdateLayout(); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var nested in Logical(child)) yield return nested;
    }
    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var nested in Visual(VisualTreeHelper.GetChild(root, index))) yield return nested;
    }
    private static void Reachable(FrameworkElement root, FrameworkElement element)
    {
        Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Width > 0 && bounds.Height > 0); Assert.InRange(bounds.Left, 0, root.ActualWidth); Assert.InRange(bounds.Right, 1, root.ActualWidth + .5); Assert.InRange(bounds.Bottom, 1, root.ActualHeight + .5);
    }
    private static Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
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
