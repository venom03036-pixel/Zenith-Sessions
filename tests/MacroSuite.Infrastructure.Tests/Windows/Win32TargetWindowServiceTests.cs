using System.Windows.Interop;
using System.Windows.Threading;
using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Windows;

public sealed class Win32TargetWindowServiceTests
{
    [Fact]
    public Task ObserverAsyncDisposal_DrainsWorkerWithoutBlockingItsRequiredUiContinuation()
        => WpfTestApplication.RunAsync(async () =>
        {
            var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcher = Dispatcher.CurrentDispatcher;
            var service = new Win32TargetWindowService(worker.Task,
                () => dispatcher.BeginInvoke(new Action(() => worker.TrySetResult()), DispatcherPriority.Background));
            var disposal = service.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Same(disposal, service.DisposeAsync().AsTask());
            await disposal.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(worker.Task.IsCompletedSuccessfully);
            service.Dispose();
        });

    [Fact]
    public Task OwnWindowMetadata_DoesNotSendTextMessagesToTheBlockedUiThread()
        => WpfTestApplication.RunAsync(async () =>
        {
            // A nonactivating 1px offscreen source, not an application window or global hook.
            using var source = new HwndSource(new HwndSourceParameters("Zenith metadata fixture")
            { WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000000, PositionX = -32000, PositionY = -32000, Width = 1, Height = 1 });
            int textRequests = 0;
            source.AddHook((nint window, int message, nint wParam, nint lParam, ref bool handled) =>
            { if (message is 0x000D or 0x000E) Interlocked.Increment(ref textRequests); return 0; });
            await using var service = new Win32TargetWindowService(Task.CompletedTask, () => { });
            nint handle = source.Handle;
            var read = Task.Run(() => service.InspectWindowMetadata(handle));
            // Intentionally block this private test UI. A same-process GetWindowText call
            // would wait for this thread and fail the bounded completion assertion.
            Assert.True(read.Wait(TimeSpan.FromMilliseconds(500)));
            Assert.NotNull(read.Result); Assert.True(read.Result.IsOwnWindow);
            Assert.Equal(0, textRequests);
        });
}
