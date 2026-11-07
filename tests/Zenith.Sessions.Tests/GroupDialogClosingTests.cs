using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ShellGroupLifecycleTests
{
    [Fact]
    public Task RenamedGroupRemainsVisibleInTheActualEditablePickerAfterSuggestionsReload() => OnUi(async () =>
    {
        var repository = new LifecycleRepository(_root); var auth = new ForbiddenAuthorization();
        await using var shell = await Ready(repository, auth);
        await shell.SaveAccountAsync(AccountProfile.Create("Synthetic picker") with { Group = "Raid" });
        var dialog = new AccountGroupManagerDialog(shell);
        dialog.Resources.MergedDictionaries.Add(Assert.IsType<ResourceDictionary>(Application.LoadComponent(
            new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml", UriKind.Relative))));
        ThemeManager.Apply(dialog.Resources, ThemeCatalog.DefaultId);
        var root = Assert.IsType<Grid>(dialog.Content);
        root.Measure(new Size(512, 530)); root.Arrange(new Rect(0, 0, 512, 530)); root.UpdateLayout();
        var model = Assert.IsType<AccountGroupManagerViewModel>(dialog.DataContext);
        model.DestinationName = "Renamed"; model.Review.Execute(null); await model.Review.Execution;
        Assert.True(model.CanApply); model.Apply.Execute(null); await model.Apply.Execution;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); root.UpdateLayout();
        Assert.Equal("Renamed", model.SourceName); Assert.Contains("Renamed", model.Groups);
        var fields = Descendants(root).OfType<ComboBox>().Single(item => item.IsEditable);
        Assert.Equal("Renamed", fields.Text);
        Assert.Equal("Renamed", Assert.IsType<TextBox>(fields.Template.FindName("PART_EditableTextBox", fields)).Text);
        Assert.Contains("Updated 1", model.Status); Assert.False(model.CanApply);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => closed.TrySetResult();
        typeof(Window).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [new CancelEventArgs()]);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    });
    private static IEnumerable<DependencyObject> Descendants(DependencyObject owner)
    {
        yield return owner;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(owner); i++)
            foreach (var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(owner, i))) yield return child;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task GroupDialogJoinsAndDefersCloseUntilTheCurrentClosingCallbackReturns(bool repeated) => OnUi(async () =>
    {
        var repository = new LifecycleRepository(_root); var auth = new ForbiddenAuthorization();
        await using var shell = await Ready(repository, auth);
        await shell.SaveAccountAsync(AccountProfile.Create("Synthetic group dialog") with { Group = "Raid" });
        var dialog = new AccountGroupManagerDialog(shell);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int closes = 0;
        dialog.Closed += (_, _) => { closes++; closed.TrySetResult(); };
        // Invoke the actual subscribed Closing callback without showing a native window.
        // Its synchronous-disposal path must return before a second Close is requested.
        var onClosing = typeof(Window).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = new CancelEventArgs(); onClosing.Invoke(dialog, [first]);
        Assert.True(first.Cancel); Assert.False(closed.Task.IsCompleted);
        if (repeated)
        {
            var second = new CancelEventArgs(); onClosing.Invoke(dialog, [second]);
            Assert.True(second.Cancel); Assert.False(closed.Task.IsCompleted);
        }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, closes); Assert.Equal(1, shell.AccountCount);
        Assert.Equal("Raid", (await repository.Inner.FindAsync(shell.SelectedAccount!.Profile.Id, default))!.Group);
    });
}
