using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Theory]
    [InlineData("quick")]
    [InlineData("review")]
    [InlineData("account")]
    public Task ExplicitLaunchClickStartsTheSavedAccountFromTheInitiallyPausedQueue(string route) => OnUi(async () =>
    {
        var child = new NativeEvidenceChild { PreparedMode = NativeLaunchMode.DirectTicket };
        await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor(), mode: NativeLaunchMode.DirectTicket);
        Assert.Equal("Resume queue", shell.QueuePauseLabel);
        shell.SelectedAccount!.IsChecked = true;
        var profile = shell.SelectedAccount.Profile;
        var command = route == "account" ? shell.AccountPower : LaunchButtonCommand(shell, route);
        command.Execute(route == "account" ? shell.SelectedAccount : null);
        await command.Execution;
        var evidence = await NativeEvidenceStartedAsync(shell);
        Assert.Equal(1, child.Starts);
        Assert.Equal(profile.Id, evidence.Operation.Intent.ProfileId);
        Assert.Equal(42, evidence.Operation.Intent.PlaceId);
        Assert.True(child.Entered.Task.Result.RequiresVerifiedExecutable);
        Assert.False(evidence.View!.ArrivalVerified);
        Assert.False(shell.IsNativeLaunchVerified);
    });

    private static AsyncCommand LaunchButtonCommand(ShellViewModel shell, string route)
    {
        var source = XDocument.Load(LaunchUiSource());
        var button = source.Descendants().Single(element => element.Name.LocalName == "Button" &&
            (route == "quick" ? (string?)element.Attribute("Content") == "{Binding QuickJoinLabel}"
                : (string?)element.Attribute("Content") is "Queue checked accounts" or "Launch checked accounts"));
        string name = Regex.Match((string)button.Attribute("Command")!, @"DataContext\.([A-Za-z]+)").Groups[1].Value;
        return Assert.IsType<AsyncCommand>(typeof(ShellViewModel).GetProperty(name)!.GetValue(shell));
    }

    [Theory]
    [InlineData("quick")]
    [InlineData("review")]
    [InlineData("account")]
    public Task PanicDuringTheLaunchGesturePreventsQueuingAndStarting(string route) => OnUi(async () =>
    {
        var child = new NativeEvidenceChild { PreparedMode = NativeLaunchMode.DirectTicket };
        await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor(), mode: NativeLaunchMode.DirectTicket);
        shell.SelectedAccount!.IsChecked = true;
        var command = route == "account" ? shell.AccountPower : LaunchButtonCommand(shell, route);
        command.CanExecuteChanged += (_, _) => { if (!command.CanExecute(null)) shell.Panic.Execute(null); };
        command.Execute(route == "account" ? shell.SelectedAccount : null); await command.Execution;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Empty(shell.Launches); Assert.Equal(0, child.Starts); Assert.Equal(0, child.Preparations);
        Assert.Equal("Resume queue", shell.QueuePauseLabel);
    });
    [Theory]
    [InlineData("quick")]
    [InlineData("review")]
    [InlineData("account")]
    public Task PauseDuringTheLaunchGestureWinsOverTheEarlierClick(string route) => OnUi(async () =>
    {
        var child = new NativeEvidenceChild { PreparedMode = NativeLaunchMode.DirectTicket };
        await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor(), mode: NativeLaunchMode.DirectTicket);
        shell.ToggleQueuePause.Execute(null); await shell.ToggleQueuePause.Execution;
        Assert.Equal("Pause queue", shell.QueuePauseLabel);
        shell.SelectedAccount!.IsChecked = true;
        var command = route == "account" ? shell.AccountPower : LaunchButtonCommand(shell, route);
        command.CanExecuteChanged += (_, _) => { if (!command.CanExecute(null)) shell.ToggleQueuePause.Execute(null); };
        command.Execute(route == "account" ? shell.SelectedAccount : null); await command.Execution;
        await shell.ToggleQueuePause.Execution;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Empty(shell.Launches); Assert.Equal(0, child.Starts); Assert.Equal(0, child.Preparations);
        Assert.Equal("Resume queue", shell.QueuePauseLabel);
    });

    internal static string LaunchUiSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string file = Path.Combine(directory.FullName, "src", "Zenith.Sessions.App", "MainWindow.xaml");
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException("Production launch page source is required.");
    }
}
