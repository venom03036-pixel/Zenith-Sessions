using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FluentAssertions;
using MacroSuite.App.Themes;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendInteractionMessage(nint window, int message, nint wParam, nint lParam);
    [Fact]
    public Task WindowInteraction_HoldsReductionAcrossNativeMoveBurstThenRestores()
        => WpfTestApplication.RunAsync(async () =>
        {
            var window = new Window(); int changes = 0;
            using var guard = new WindowInteractionGuard(window, () => changes++);
            var handle = new WindowInteropHelper(window).EnsureHandle();
            void Send(int id) => SendInteractionMessage(handle, id, 0, 0);
            Send(0x0231);
            for (int i = 0; i < 1000; i++) Send(0x0231);
            await Task.Delay(260);
            guard.IsInteracting.Should().BeTrue(); changes.Should().Be(1);
            Send(0x0232); await Task.Delay(280);
            guard.IsInteracting.Should().BeFalse(); changes.Should().Be(2);
            window.Close();
        });

    [Fact]
    public Task WindowInteraction_ScrollThumbPersistsUntilReleasedAndDisposesSubscriptions()
        => WpfTestApplication.RunAsync(async () =>
        {
            var window = new Window(); int changes = 0;
            var guard = new WindowInteractionGuard(window, () => changes++);
            window.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            await Task.Delay(260); guard.IsInteracting.Should().BeTrue();
            window.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await Task.Delay(280); guard.IsInteracting.Should().BeFalse(); changes.Should().Be(2);
            guard.Dispose();
            window.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            changes.Should().Be(2); guard.IsInteracting.Should().BeFalse();
        });
}
