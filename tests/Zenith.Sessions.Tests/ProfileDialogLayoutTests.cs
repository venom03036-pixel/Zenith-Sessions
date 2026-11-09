using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

[Collection(WpfProductionResourceCollection.Name)]
public sealed class ProfileDialogLayoutTests
{
    [Theory]
    [InlineData(480, 360)]
    [InlineData(540, 520)]
    public Task ShortDialogKeepsItsHeadingAndActionsReachableWhileOnlyFieldsScroll(double width, double height) => OnSta(async () =>
    {
        int saves = 0;
        var dialog = new ProfileDialog(AccountProfile.Create("Local alias"), _ => { saves++; return Task.CompletedTask; });
        var root = Root(dialog); Layout(root, width, height);
        var fields = Fields(root); var save = Action(root, "Save profile"); var cancel = Action(root, "Cancel");
        var heading = Elements(root).OfType<TextBlock>().Single(item => item.Text == dialog.Title);
        Rect saveBefore = Bounds(root, save), cancelBefore = Bounds(root, cancel), headingBefore = Bounds(root, heading);
        Reachable(root, save); Reachable(root, cancel); Reachable(root, heading);
        Assert.True(fields.ScrollableHeight > 0, "The production form must really overflow this short viewport.");
        Assert.DoesNotContain(save, Elements(fields)); Assert.DoesNotContain(cancel, Elements(fields)); Assert.DoesNotContain(heading, Elements(fields));

        fields.ScrollToBottom(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); root.UpdateLayout();
        Assert.True(fields.VerticalOffset > 0);
        Assert.Equal(saveBefore, Bounds(root, save)); Assert.Equal(cancelBefore, Bounds(root, cancel)); Assert.Equal(headingBefore, Bounds(root, heading));
        Reachable(root, save); Reachable(root, cancel); Assert.Equal(0, saves);
        Assert.Null(dialog.DialogResult); Assert.True(save.IsDefault); Assert.True(cancel.IsCancel);
    });

    [Theory]
    [InlineData(480, 360)]
    [InlineData(420, 360)]
    public Task LongValidationErrorWrapsAndScrollsWithoutDisplacingSaveOrCancel(double width, double height) => OnSta(async () =>
    {
        string message = string.Join(" ", Enumerable.Repeat("A synthetic validation detail remains readable.", 100));
        int saves = 0;
        var dialog = new ProfileDialog(AccountProfile.Create("Kept draft"), _ =>
        { saves++; return Task.FromException(new ArgumentException(message)); });
        var root = Root(dialog); Layout(root, width, height);
        var save = Action(root, "Save profile"); var cancel = Action(root, "Cancel");
        Rect before = Bounds(root, save);
        Click(save); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout(root, width, height);

        var error = Error(root); var details = Elements(root).OfType<ScrollViewer>().Single(item => ReferenceEquals(item.Content, error));
        Assert.Equal(message, error.Text); Assert.Equal(TextWrapping.Wrap, error.TextWrapping); Assert.Equal(Visibility.Visible, details.Visibility);
        Assert.True(details.ActualHeight > 0); Assert.InRange(details.ActualHeight, 0, 96);
        Assert.True(details.ScrollableHeight > 0, "The complete error remains available in its bounded viewport.");
        Assert.Equal(before, Bounds(root, save)); Reachable(root, save); Reachable(root, cancel); Reachable(root, details);
        Assert.True(Fields(root).ActualHeight > 0); Assert.DoesNotContain(error, Elements(Fields(root)));
        details.ScrollToBottom(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); root.UpdateLayout();
        Assert.True(details.VerticalOffset > 0); Reachable(root, save); Reachable(root, cancel);
        Assert.Equal(1, saves); Assert.Equal("Kept draft", Field(root, "Display name").Text); Assert.Null(dialog.DialogResult);
    });

    [Fact]
    public Task InvalidDefaultSaveKeepsDraftAndPresentsAccessibleValidationWithoutCallingStorage() => OnSta(() =>
    {
        int saves = 0;
        var dialog = new ProfileDialog(AccountProfile.Create("Original"), _ => { saves++; return Task.CompletedTask; });
        var root = Root(dialog); Field(root, "Display name").Text = ""; Field(root, "Notes · included in metadata exports; never enter secrets").Text = "Keep my unfinished note";
        Layout(root, 480, 360); var save = Action(root, "Save profile");
        Assert.True(save.IsDefault); Click(save); Layout(root, 480, 360);

        Assert.Equal(0, saves); Assert.Null(dialog.DialogResult); Assert.Equal("", Field(root, "Display name").Text);
        Assert.Equal("Keep my unfinished note", Field(root, "Notes · included in metadata exports; never enter secrets").Text);
        Assert.Contains("Name", Error(root).Text); Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Error(root)));
        Reachable(root, save); Reachable(root, Action(root, "Cancel")); Assert.True(save.IsEnabled); Assert.True(Action(root, "Cancel").IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task PendingSaveStillGuardsClosingAndFailureRestoresActionsWithoutDiscardingEdits() => OnSta(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AccountProfile? submitted = null;
        var dialog = new ProfileDialog(AccountProfile.Create("Original"), draft => { submitted = draft; return release.Task; });
        var root = Root(dialog); var name = Field(root, "Display name"); name.Text = "Draft name";
        Layout(root, 480, 360); var save = Action(root, "Save profile"); var cancel = Action(root, "Cancel");
        Click(save);
        try
        {
            Assert.Equal("Draft name", submitted?.Label); Assert.Equal("Saving…", save.Content);
            Assert.False(save.IsEnabled); Assert.False(cancel.IsEnabled); Assert.True(ClosingIsCancelled(dialog));
            name.Text = "Latest unsaved edit";
        }
        finally
        {
            release.TrySetException(new IOException("Synthetic storage failure."));
            await Until(() => save.IsEnabled);
        }
        Layout(root, 480, 360); Assert.True(cancel.IsEnabled); Assert.Equal("Save profile", save.Content);
        Assert.False(ClosingIsCancelled(dialog)); Assert.Equal("Latest unsaved edit", name.Text); Assert.Null(dialog.DialogResult);
        Assert.Contains("Your edits remain here", Error(root).Text); Assert.DoesNotContain("Synthetic storage failure", Error(root).Text);
        Reachable(root, save); Reachable(root, cancel);
    });

    [Fact]
    public Task FixedFooterPreservesOptionalMetadataAndExistingProfileFields() => OnSta(() =>
    {
        var original = (AccountProfile.Create("Kept alias") with
        { Revision = 7, Username = "old_name", RobloxUserId = 123, PreferredPlaceId = 42, Group = "Family", AdditionalGroups = "Raid", Notes = "Kept notes", IsFavorite = true, Priority = 31 }).Validated();
        AccountProfile? submitted = null;
        var dialog = new ProfileDialog(original, draft => { submitted = draft; return Task.FromException(new IOException("Synthetic save failure.")); });
        var root = Root(dialog); Field(root, "Display name").Text = "Renamed alias";
        Field(root, "Username · optional").Text = ""; Field(root, "Preferred Roblox place ID · optional").Text = "";
        Layout(root, 480, 360); Click(Action(root, "Save profile"));
        Assert.Equal(original with { Label = "Renamed alias", Username = "", PreferredPlaceId = null }, submitted);
        Assert.Null(dialog.DialogResult); Assert.Equal("Renamed alias", Field(root, "Display name").Text);
        Assert.Equal("", Field(root, "Preferred Roblox place ID · optional").Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ExtraClientArgumentsAreEditedPerAccountValidatedByTheirRuleAndSavedInTheirCanonicalForm() => OnSta(() =>
    {
        const string label = "Extra client arguments · optional; replaces the default for this account";
        var original = (AccountProfile.Create("Kept alias") with { Revision = 3, ExtraClientArguments = "--kept" }).Validated();
        AccountProfile? submitted = null;
        // The save fails on purpose: a dialog that is not shown cannot set DialogResult, and the submitted draft is what matters here.
        var dialog = new ProfileDialog(original, draft => { submitted = draft; return Task.FromException(new IOException("Synthetic save failure.")); });
        var root = Root(dialog); var field = Field(root, label);
        Assert.Equal("--kept", field.Text); Assert.Equal(ExtraClientArgumentPolicy.MaximumCharacters, field.MaxLength);
        Assert.Contains(field, Elements(Fields(root)));
        field.Text = "--own %TEMP%"; Layout(root, 540, 730); Click(Action(root, "Save profile"));
        Assert.Null(submitted); Assert.Equal("Percent signs are not allowed in extra client arguments.", Error(root).Text);
        field.Text = "  --own   --value=1 "; Click(Action(root, "Save profile"));
        Assert.Equal(original with { ExtraClientArguments = "--own --value=1" }, submitted);
        return Task.CompletedTask;
    });

    [Fact]
    public Task InitialAndMinimumDimensionsFitTheAvailableWpfWorkArea() => OnSta(() =>
    {
        var dialog = new ProfileDialog(AccountProfile.Create("Local alias"), _ => throw new InvalidOperationException("No save expected."));
        var work = SystemParameters.WorkArea;
        Assert.InRange(dialog.Width, 1, Math.Max(1, work.Width)); Assert.InRange(dialog.Height, 1, Math.Max(1, work.Height));
        Assert.InRange(dialog.MinWidth, 1, dialog.Width); Assert.InRange(dialog.MinHeight, 1, dialog.Height);
        Assert.True(dialog.MinHeight <= 420); Assert.Equal(ResizeMode.CanResize, dialog.ResizeMode);
        Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation); Assert.False(dialog.IsVisible);
        return Task.CompletedTask;
    });

    private static FrameworkElement Root(ProfileDialog dialog) => Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
    private static ScrollViewer Fields(FrameworkElement root) => Elements(root).OfType<ScrollViewer>().Single(item => AutomationProperties.GetName(item) == "Account profile fields");
    private static TextBox Field(FrameworkElement root, string name) => Elements(root).OfType<TextBox>().Single(item => AutomationProperties.GetName(item) == name);
    private static TextBlock Error(FrameworkElement root) => Elements(root).OfType<TextBlock>().Single(item => AutomationProperties.GetName(item) == "Profile validation error");
    private static Button Action(FrameworkElement root, string content) => Elements(root).OfType<Button>().Single(item => Equals(item.Content, content));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Elements(child)) yield return descendant;
    }
    private static void Layout(FrameworkElement root, double width, double height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static Rect Bounds(FrameworkElement root, FrameworkElement element) => new(element.TranslatePoint(new Point(), root), element.RenderSize);
    private static void Reachable(FrameworkElement root, FrameworkElement element)
    {
        Rect bounds = Bounds(root, element);
        Assert.True(bounds.Width > 0 && bounds.Height > 0, "The control must have an actual layout area.");
        Assert.True(bounds.Left >= -.01 && bounds.Top >= -.01 && bounds.Right <= root.RenderSize.Width + .01 && bounds.Bottom <= root.RenderSize.Height + .01,
            "The full control must remain inside the arranged dialog content.");
    }
    private static bool ClosingIsCancelled(ProfileDialog dialog)
    {
        var args = new CancelEventArgs();
        typeof(Window).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [args]);
        return args.Cancel;
    }
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Synthetic dialog save owner did not complete.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(1);
        }
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
