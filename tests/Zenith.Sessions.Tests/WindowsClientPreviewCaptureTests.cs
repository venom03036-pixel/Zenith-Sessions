using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using CaptureSession = Windows.Graphics.Capture.GraphicsCaptureSession;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Real Windows Graphics Capture of borderless WPF windows this test owns and paints with four known colours.
/// The provider's executable allow-list names this test host instead of Roblox; every PID/start/HWND/visibility
/// check still runs. No Roblox client, account, network or file output is involved.
/// </summary>
public sealed class WindowsClientPreviewCaptureTests
{
    private static readonly byte[] Red = [0, 0, 255, 255], Green = [0, 255, 0, 255], Blue = [255, 0, 0, 255], Yellow = [0, 255, 255, 255];

    [Fact]
    public async Task OwnedWindowIsDownscaledOnTheGpuSoOnlyASmallLevelIsReadBackAndColoursSurvive()
    {
        if (!CaptureSession.IsSupported()) return; // Same rule as the MacroSuite capture tests: no capture stack, nothing to observe.
        await using var windows = await OwnedWindows.StartAsync();
        var large = await windows.ShowAsync(960, 540);
        await using var provider = new WindowsClientPreviewProvider([System.IO.Path.GetFileName(Environment.ProcessPath)!]);

        var frame = await CaptureAsync(provider, windows, large);
        PreviewSurfaceSize source = new(frame.SourceWidth, frame.SourceHeight);
        var read = provider.LastReadbackSize;
        Assert.Equal(ClientPreviewBufferReader.LevelSize(source, ClientPreviewBufferReader.ReadbackLevel(source)), read);
        // A 960x540 DIP window is at least 960x540 pixels, so at least one 2x GPU reduction applies before readback.
        Assert.True(4L * read.Width * read.Height <= (long)source.Width * source.Height,
            $"Readback {read.Width}x{read.Height} is not at most a quarter of the {source.Width}x{source.Height} source.");
        AssertQuadrants(frame, source);
        // The provider owns the cached GPU textures and releases them with its device.
        Assert.InRange(provider.ReadbackCachedBytes, 1, ClientPreviewBufferReader.MaximumReadbackBytes);
        await provider.ReleaseAsync(default);
        Assert.Equal(0, provider.ReadbackCachedBytes);
    }

    [Fact]
    public async Task SmallOwnedWindowNeedsNoGpuReductionAndIsStillReadExactly()
    {
        if (!CaptureSession.IsSupported()) return;
        await using var windows = await OwnedWindows.StartAsync();
        var small = await windows.ShowAsync(300, 150);
        await using var provider = new WindowsClientPreviewProvider([System.IO.Path.GetFileName(Environment.ProcessPath)!]);

        var frame = await CaptureAsync(provider, windows, small);
        PreviewSurfaceSize source = new(frame.SourceWidth, frame.SourceHeight);
        Assert.Equal(0, ClientPreviewBufferReader.ReadbackLevel(source));
        Assert.Equal(source, provider.LastReadbackSize);
        AssertQuadrants(frame, source);
    }

    [Fact]
    public async Task TheProductionProviderStillRejectsAWindowThatIsNotARobloxClient()
    {
        await using var windows = await OwnedWindows.StartAsync();
        var owned = await windows.ShowAsync(300, 150);
        await using var provider = new WindowsClientPreviewProvider();
        var result = Assert.Single(await provider.CaptureAsync([Target(owned.Handle)], 1, default));
        Assert.Equal(ClientPreviewStatus.InvalidClient, result.Status);
        Assert.Null(result.Frame);
    }

    private static async Task<ClientPreviewFrame> CaptureAsync(WindowsClientPreviewProvider provider, OwnedWindows windows, OwnedWindow window)
    {
        var target = Target(window.Handle);
        var limit = Stopwatch.StartNew(); var seen = new List<ClientPreviewStatus>();
        while (limit.Elapsed < TimeSpan.FromSeconds(20))
        {
            var result = Assert.Single(await provider.CaptureAsync([target], 1, default));
            if (result.Status == ClientPreviewStatus.Ready)
            {
                var frame = result.Frame!;
                Assert.Equal(target, frame.Target);
                Assert.Equal(ClientPreviewBufferReader.OutputSize(new(frame.SourceWidth, frame.SourceHeight)), (frame.Width, frame.Height));
                Assert.Equal(frame.Width * 4, frame.Stride); Assert.Equal(frame.Stride * frame.Height, frame.Pixels.Length);
                return frame;
            }
            seen.Add(result.Status);
            Assert.True(result.Status is ClientPreviewStatus.NoFrame or ClientPreviewStatus.Deferred, "Unexpected preview status " + result.Status);
            // WGC delivers a frame only when the window changes, and the provider accepts one batch per second.
            await windows.TickAsync(window);
            await Task.Delay(ClientPreviewLimits.MinimumBatchInterval + TimeSpan.FromMilliseconds(100));
        }
        throw new TimeoutException("No preview frame arrived. Statuses: " + string.Join(", ", seen));
    }

    private static void AssertQuadrants(ClientPreviewFrame frame, PreviewSurfaceSize source)
    {
        Assert.True(source.Width > 0 && source.Height > 0);
        At(frame, 0.25, 0.25, Red); At(frame, 0.75, 0.25, Green); At(frame, 0.25, 0.75, Blue); At(frame, 0.75, 0.75, Yellow);
    }

    private static void At(ClientPreviewFrame frame, double x, double y, byte[] expected)
    {
        var pixels = frame.Pixels.Span;
        int px = (int)(x * frame.Width), py = (int)(y * frame.Height), offset = py * frame.Stride + px * 4;
        for (int channel = 0; channel < 4; channel++)
            Assert.True(Math.Abs(pixels[offset + channel] - expected[channel]) <= 8,
                $"Pixel ({px},{py}) channel {channel} was {pixels[offset + channel]}, expected {expected[channel]} in a {frame.Width}x{frame.Height} preview.");
    }

    private static ClientPreviewTarget Target(nint window)
    {
        using var self = Process.GetCurrentProcess();
        long start = ProcessInstanceIdentity.StartTicks(self.SafeHandle) ?? throw new InvalidOperationException("The test process start time is unavailable.");
        return new(new SessionProcessKey(null, Environment.ProcessId, start), window);
    }

    private sealed record OwnedWindow(Window Window, Border Ticker, nint Handle);

    /// <summary>One STA dispatcher thread that owns every test window and closes them before it stops.</summary>
    private sealed class OwnedWindows : IAsyncDisposable
    {
        private readonly List<Window> _windows = [];
        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;
        private int _left = 40;
        private OwnedWindows(Thread thread, Dispatcher dispatcher) { _thread = thread; _dispatcher = dispatcher; }

        public static async Task<OwnedWindows> StartAsync()
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => { ready.TrySetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); })
            { IsBackground = true, Name = "Owned preview capture windows" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            return new(thread, await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        public async Task<OwnedWindow> ShowAsync(double width, double height)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var owned = await _dispatcher.InvokeAsync(() =>
            {
                var grid = new Grid();
                grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new());
                grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
                Fill(grid, 0, 0, 255, 0, 0); Fill(grid, 0, 1, 0, 255, 0); Fill(grid, 1, 0, 0, 0, 255); Fill(grid, 1, 1, 255, 255, 0);
                // A 4 DIP corner square changes before each retry so the compositor produces a new frame.
                var ticker = new Border { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Black };
                grid.Children.Add(ticker);
                var window = new Window
                {
                    Title = "Zenith preview capture test", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false, ShowActivated = false, Left = _left, Top = 40, Width = width, Height = height, Content = grid
                };
                _left += 40;
                window.ContentRendered += (_, _) => rendered.TrySetResult();
                window.Show(); _windows.Add(window);
                return new OwnedWindow(window, ticker, new WindowInteropHelper(window).Handle);
            });
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(250);
            return owned;
        }

        public Task TickAsync(OwnedWindow window) => _dispatcher.InvokeAsync(() =>
            window.Ticker.Background = window.Ticker.Background == Brushes.Black ? Brushes.White : Brushes.Black).Task;

        private static void Fill(Grid grid, int row, int column, byte r, byte g, byte b)
        {
            var cell = new Border { Background = new SolidColorBrush(Color.FromRgb(r, g, b)) };
            Grid.SetRow(cell, row); Grid.SetColumn(cell, column); grid.Children.Add(cell);
        }

        public async ValueTask DisposeAsync()
        {
            await _dispatcher.InvokeAsync(() => { foreach (var window in _windows) window.Close(); _windows.Clear(); });
            _dispatcher.InvokeShutdown();
            if (!_thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("The owned window thread did not stop.");
        }
    }
}
