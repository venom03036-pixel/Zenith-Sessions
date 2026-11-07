using System.Reflection;
using System.Windows;
using MacroSuite.App;
using MacroSuite.App.ViewModels;
using MacroSuite.Infrastructure.Tests.ViewModels;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Windows;

/// <summary>Production StateChanged handler, with no HWND, hooks, input or owner profile.</summary>
public sealed class MainWindowRestoreStateTests
{

    [Fact]
    public Task OwnedRestoreStateChange_KeepsReadinessBudgetAndMiniHandoffOwnedByRestore()
        => WithWindow(async (window, vm) =>
        {
            using var cancellation = new CancellationTokenSource();
            Set(window, "_restoreFocusCancellation", cancellation);
            Set(window, "_windowStateBeforeMini", WindowState.Maximized);
            Set(window, "_restoringMainWindow", true);
            typeof(Window).GetMethod("OnStateChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object[] { EventArgs.Empty });
            Assert.False(cancellation.IsCancellationRequested);
            // No general StateChanged bookkeeping/visibility synchronization should run
            // during this internal transition; the original restore snapshot stays pinned.
            Assert.Equal(WindowState.Maximized, Get<WindowState>(window, "_windowStateBeforeMini"));
            Assert.Null(Get<object?>(window, "_hotkeyService"));
            await Task.CompletedTask;
        });

    [Fact]
    public Task NewUserMinimize_CancelsPendingReturn_EvenWhileRestoreIsInProgress()
        => WithWindow(async (window, vm) =>
        {
            using var cancellation = new CancellationTokenSource();
            Set(window, "_restoreFocusCancellation", cancellation);
            Set(window, "_restoringMainWindow", true);
            window.WindowState = WindowState.Minimized;
            typeof(Window).GetMethod("OnStateChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object[] { EventArgs.Empty });
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Null(Get<object?>(window, "_hotkeyService"));
            await Task.CompletedTask;
        });

    private static Task WithWindow(Func<MainWindow, MainViewModel, Task> body) => WpfTestApplication.RunAsync(async () =>
    {
        var resources = Application.Current.Resources;
        if (!resources.MergedDictionaries.Any(resource => resource.Source?.ToString().Contains("ModernControls.xaml") == true))
            resources.MergedDictionaries.Add(new() { Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/ModernControls.xaml") });
        using var fixture = new MainViewModelTests();
        var factory = typeof(MainViewModelTests).GetMethod("CreateMainViewModel", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var vm = (MainViewModel)factory.Invoke(fixture, new object?[] { null, null, null })!;
        var window = new MainWindow(vm);
        try { await body(window, vm); }
        finally
        {
            Set(window, "_restoreFocusCancellation", null); Set(window, "_restoringMainWindow", false);
            await Task.WhenAll(vm.EditorVM.ShutdownAsync(), vm.AutoClickerVM.ShutdownAsync(), vm.SettingsVM.Appearance.ShutdownAsync());
            vm.SettingsVM.DetachRuntimeSettings(); window.DataContext = null; window.Close();
        }
    });
    private static void Set(MainWindow window, string field, object? value) => typeof(MainWindow).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string field) => (T)typeof(MainWindow).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
}
