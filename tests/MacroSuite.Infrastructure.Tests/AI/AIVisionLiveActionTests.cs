using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Security;
using MacroSuite.Core.Steps;
using MacroSuite.Infrastructure.AIProviders;
using MacroSuite.Infrastructure.Capture;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.OpenCV;
using MacroSuite.Infrastructure.Security;
using MacroSuite.Infrastructure.Windows;
using MacroSuite.SDK.AI;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
using DrawingBrushes = System.Windows.Media.Brushes;
using Window = System.Windows.Window;

namespace MacroSuite.Infrastructure.Tests.AI;

[Collection("InteractiveDesktop")]
public sealed class AIVisionLiveActionTests
{
    private const string TargetWindowTitle = "Zenith AI Vision Target";
    private static readonly TimeSpan ForegroundWaitTimeout = TimeSpan.FromSeconds(2);
    private readonly ITestOutputHelper _output;

    public AIVisionLiveActionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AIVision_ScreenshotProvider_ClicksGreenTargetButton()
    {
        if (!CanRunLiveInput())
        {
            _output.WriteLine("Skipped: no interactive desktop session.");
            return;
        }

        var vault = new MemoryVault();
        vault.SetSecret("screenshot-vision", "local-test");
        var provider = new GreenButtonScreenshotProvider();
        if (!await RunLiveClickAsync(provider, vault, "screenshot-vision").ConfigureAwait(true))
            return;

        _output.WriteLine("AI Vision pipeline captured the target window and clicked the green button.");
    }

    [Fact]
    public async Task AIVision_CloudProvider_ClicksGreenTargetButton_WhenKeyConfigured()
    {
        if (!CanRunLiveInput())
        {
            _output.WriteLine("Skipped: no interactive desktop session.");
            return;
        }

        var vault = new MemoryVault();
        string? providerId = TryLoadCloudKey(vault);
        if (providerId is null)
        {
            _output.WriteLine("Skipped: no cloud vision API key in the local vault or environment.");
            return;
        }

        _output.WriteLine($"Using configured cloud vision provider '{providerId}'.");

        using var http = new ProviderHttpClientFactory();
        IAIVisionProvider provider = providerId switch
        {
            "anthropic" => new AnthropicVisionProvider(http),
            "gemini" => new GoogleGeminiVisionProvider(http),
            _ => new OpenAiVisionProvider(http)
        };

        await RunLiveClickAsync(provider, vault, providerId, provider.DefaultModel).ConfigureAwait(true);
    }

    /// <summary>Returns false when the test was skipped because the target window could not take the foreground.</summary>
    private async Task<bool> RunLiveClickAsync(
        IAIVisionProvider provider,
        ISecretVault vault,
        string providerId,
        string? modelId = null)
    {
        using var target = AiVisionTargetHost.Show();
        using var capture = new GdiBitBltCaptureService();
        using var player = new Win32InputPlayer();
        using var controller = new MacroController(player);

        var router = new AIVisionRouter(vault, [provider]);
        router.DefaultProviderId = providerId;
        if (!string.IsNullOrWhiteSpace(modelId))
            router.DefaultModelId = modelId;

        var license = LicenseInfo.CreateAdministrator("live-ai-vision-test", "local-test");
        var featureGate = new FeatureGate(new StaticLicenseService(license));

        var step = new AIStep(
            "Click the large bright green button labeled CLICK ME. Return a click at the center of that button. Coordinates are image-local pixels from the top-left of this screenshot.",
            target.WindowRect,
            modelId: modelId,
            providerId: providerId)
        {
            Timeout = TimeSpan.FromSeconds(90),
            // Input-producing AI actions must name the window they may act on (Rule 8).
            ExpectedWindow = TargetWindowTitle
        };

        var context = new MacroContext(capture, controller)
        {
            // Same foreground-title reader the app wires for playback, so the validator
            // checks the real foreground window against ExpectedWindow.
            ForegroundWindow = Win32ForegroundWindowInfo.Default,
            InputPlayer = player,
            VisionRouter = router,
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            ActionValidator = new AIActionValidator { MinConfidenceThreshold = 0.5 },
            FeatureGate = featureGate,
            ImageEncoder = new OpenCvImageEncoder(),
            AutonomousMode = true,
            ApprovalTimeout = TimeSpan.FromSeconds(5)
        };

        // The validator rejects input unless the target really is the foreground window.
        // Windows' foreground lock can refuse SetForegroundWindow during a long run, so retry
        // for a bounded time and skip (no input sent) rather than fail on machine state.
        if (!await target.TryTakeForegroundAsync(ForegroundWaitTimeout).ConfigureAwait(true))
        {
            _output.WriteLine("Skipped: the target window could not take the foreground.");
            return false;
        }

        await step.ExecuteAsync(context).ConfigureAwait(true);

        target.WaitForClick(TimeSpan.FromSeconds(3)).Should().BeTrue(
            "AI Vision should click the dedicated green target button");
        target.ClickCount.Should().BeGreaterThan(0);
        File.Exists(target.MarkerPath).Should().BeTrue();
        File.ReadAllText(target.MarkerPath).Should().Be("clicked");
        return true;
    }

    private static bool CanRunLiveInput()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("ZENITH_SKIP_LIVE_INPUT"),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Environment.UserInteractive || !OperatingSystem.IsWindows())
            return false;

        int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return width > 0 && height > 0;
    }

    private static string? TryLoadCloudKey(MemoryVault dest)
    {
        try
        {
            var userVault = new DpapiSecretVault();
            foreach (string id in new[] { "openai", "gemini", "anthropic" })
            {
                if (!userVault.HasSecret(id))
                    continue;

                string? secret = userVault.GetSecret(id);
                if (string.IsNullOrWhiteSpace(secret))
                    continue;

                dest.SetSecret(id, secret);
                return id;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Could not read the local AI vault: {ex.Message}");
        }

        if (TryEnv("OPENAI_API_KEY", "openai", dest))
            return "openai";
        if (TryEnv("GEMINI_API_KEY", "gemini", dest) || TryEnv("GOOGLE_API_KEY", "gemini", dest))
            return "gemini";
        if (TryEnv("ANTHROPIC_API_KEY", "anthropic", dest))
            return "anthropic";

        return null;
    }

    private static bool TryEnv(string envName, string providerId, MemoryVault dest)
    {
        string? value = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        dest.SetSecret(providerId, value);
        return true;
    }

    private sealed class MemoryVault : ISecretVault
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

        public void SetSecret(string keyName, string secretValue) => _secrets[keyName] = secretValue;
        public string? GetSecret(string keyName) => _secrets.TryGetValue(keyName, out var v) ? v : null;
        public bool HasSecret(string keyName) => _secrets.ContainsKey(keyName);
        public bool RemoveSecret(string keyName) => _secrets.Remove(keyName);
        public IReadOnlyList<string> ListSecretKeys() => _secrets.Keys.ToList();
    }

    private sealed class StaticLicenseService : ILicenseService
    {
        private readonly LicenseInfo _license;
        public event EventHandler<LicenseChangedEventArgs>? LicenseChanged
        {
            add { }
            remove { }
        }

        public StaticLicenseService(LicenseInfo license) => _license = license;
        public LicenseInfo GetCurrentLicense() => _license;
        public Task<LicenseValidationResult> ValidateAsync(CancellationToken ct = default) =>
            Task.FromResult(LicenseValidationResult.Success(_license));
        public bool IsFeatureLicensed(FeatureId feature) => _license.IsFeatureLicensed(feature);
    }

    /// <summary>
    /// Local vision stand-in that inspects the captured JPEG and clicks the green button blob.
    /// Used when no cloud API key is configured so the full AI pipeline still performs a real click.
    /// </summary>
    private sealed class GreenButtonScreenshotProvider : IAIVisionProvider
    {
        public string ProviderId => "screenshot-vision";
        public string DisplayName => "Screenshot Vision";
        public IReadOnlyList<string> SupportedModels => ["local-green-blob"];
        public string DefaultModel => "local-green-blob";

        public Task<AIResponse> AnalyzeAsync(AIRequest request, string apiKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var (x, y, confidence) = LocateGreenButton(request.ImageBytes, request.ImageWidth, request.ImageHeight);
            return Task.FromResult(new AIResponse
            {
                Action = new AIAction
                {
                    ActionType = "click",
                    X = x,
                    Y = y,
                    Confidence = confidence,
                    Reasoning = "Located the bright green CLICK ME button in the captured screenshot."
                },
                InputTokens = 1,
                OutputTokens = 1,
                EstimatedCostUsd = 0m
            });
        }

        public Task<ApiKeyValidationResult> ValidateApiKeyAsync(
            string apiKey,
            string? modelId = null,
            CancellationToken ct = default)
            => Task.FromResult(ApiKeyValidationResult.Success(ProviderId, modelId ?? DefaultModel, TimeSpan.Zero));

        private static (int X, int Y, double Confidence) LocateGreenButton(
            byte[]? imageBytes,
            int fallbackWidth,
            int fallbackHeight)
        {
            int centerX = Math.Max(1, fallbackWidth / 2);
            int centerY = Math.Max(1, fallbackHeight / 2);
            if (imageBytes is null || imageBytes.Length == 0)
                return (centerX, centerY, 0.6);

            using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            if (mat.Empty())
                return (centerX, centerY, 0.6);

            long sumX = 0;
            long sumY = 0;
            int count = 0;
            for (int y = 0; y < mat.Height; y++)
            {
                for (int x = 0; x < mat.Width; x++)
                {
                    var pixel = mat.At<Vec3b>(y, x);
                    byte b = pixel.Item0;
                    byte g = pixel.Item1;
                    byte r = pixel.Item2;
                    if (g >= 140 && g >= r + 30 && g >= b + 30)
                    {
                        sumX += x;
                        sumY += y;
                        count++;
                    }
                }
            }

            if (count == 0)
                return (mat.Width / 2, mat.Height / 2, 0.6);

            return ((int)(sumX / count), (int)(sumY / count), 0.98);
        }
    }

    private sealed class AiVisionTargetHost : IDisposable
    {
        private readonly Thread _uiThread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _clicked = new(false);
        private Dispatcher? _dispatcher;
        private Window? _window;
        private IntPtr _hwnd;
        private Exception? _startupError;
        private int _clickCount;

        public Rectangle WindowRect { get; private set; }
        public string MarkerPath { get; }
        public int ClickCount => Volatile.Read(ref _clickCount);

        private AiVisionTargetHost(string markerPath)
        {
            MarkerPath = markerPath;
            _uiThread = new Thread(UiThreadProc)
            {
                IsBackground = true,
                Name = "Zenith.AIVisionLiveTarget"
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("AI Vision target window did not appear.");
            if (_startupError != null)
                throw new InvalidOperationException("AI Vision target window failed to start.", _startupError);
        }

        public static AiVisionTargetHost Show()
        {
            string marker = Path.Combine(Path.GetTempPath(), $"zenith-ai-vision-{Guid.NewGuid():N}.txt");
            var host = new AiVisionTargetHost(marker);
            Thread.Sleep(350);
            return host;
        }

        public bool WaitForClick(TimeSpan timeout) => _clicked.Wait(timeout);

        /// <summary>
        /// Waits up to <paramref name="timeout"/> for this window to be the real foreground
        /// window, re-requesting SetForegroundWindow and Activate on its UI thread between checks.
        /// </summary>
        public Task<bool> TryTakeForegroundAsync(TimeSpan timeout) =>
            LiveForegroundWait.TryAcquireAsync(
                () => Win32WindowActivation.IsForeground(_hwnd),
                RequestForeground,
                timeout,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);

        private void RequestForeground()
        {
            Dispatcher? dispatcher = _dispatcher;
            Window? window = _window;
            if (dispatcher is null || window is null || dispatcher.HasShutdownStarted)
                return;

            // Focus is thread-owned: ask the window's own UI thread, bounded so a stuck
            // dispatcher cannot hold the test past its foreground deadline.
            dispatcher.Invoke(
                () =>
                {
                    SetForegroundWindow(_hwnd);
                    window.Activate();
                },
                DispatcherPriority.Send,
                CancellationToken.None,
                TimeSpan.FromSeconds(1));
        }

        public void Dispose()
        {
            try
            {
                _dispatcher?.BeginInvoke(new Action(() => _window?.Close()));
            }
            catch
            {
                // Window may already be gone.
            }

            _uiThread.Join(TimeSpan.FromSeconds(5));
            _ready.Dispose();
            _clicked.Dispose();
            try
            {
                if (File.Exists(MarkerPath))
                    File.Delete(MarkerPath);
            }
            catch
            {
                // Temp cleanup is best-effort.
            }
        }

        private void UiThreadProc()
        {
            try
            {
                var button = new Button
                {
                    Content = "CLICK ME",
                    FontSize = 36,
                    FontWeight = FontWeights.Bold,
                    Background = DrawingBrushes.Lime,
                    Foreground = DrawingBrushes.Black,
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(24)
                };
                button.Click += (_, _) =>
                {
                    Interlocked.Increment(ref _clickCount);
                    File.WriteAllText(MarkerPath, "clicked");
                    _clicked.Set();
                };

                var window = new Window
                {
                    Title = TargetWindowTitle,
                    Width = 520,
                    Height = 300,
                    Left = 80,
                    Top = 80,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    WindowStyle = WindowStyle.ToolWindow,
                    ResizeMode = ResizeMode.NoResize,
                    Topmost = true,
                    ShowInTaskbar = false,
                    Background = DrawingBrushes.DimGray,
                    Content = button
                };

                window.Loaded += (_, _) =>
                {
                    try
                    {
                        var hwnd = new WindowInteropHelper(window).Handle;
                        NativeMethods.GetWindowRect(hwnd, out var rect);
                        WindowRect = new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height);
                        _hwnd = hwnd;
                        ShowWindow(hwnd, 9);
                        SetForegroundWindow(hwnd);
                        window.Activate();
                        _dispatcher = Dispatcher.CurrentDispatcher;
                        _window = window;
                        _ready.Set();
                    }
                    catch (Exception ex)
                    {
                        _startupError = ex;
                        _ready.Set();
                    }
                };

                window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                window.Show();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                _startupError = ex;
                _ready.Set();
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}

[Collection("InteractiveDesktop")]
public sealed class LowLevelHookServiceLifecycleTests
{
    [Fact]
    public async Task StartAsync_CancelledToken_StopsRecordingAndDisposesRegistration()
    {
        if (!Environment.UserInteractive || !OperatingSystem.IsWindows())
            return;

        using var hooks = new LowLevelHookService();
        var channel = Channel.CreateUnbounded<InputEvent>();
        using var cts = new CancellationTokenSource();

        await hooks.StartAsync(channel.Writer, cts.Token);
        hooks.IsRecording.Should().BeTrue();

        cts.Cancel();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (hooks.IsRecording && sw.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(20);

        hooks.IsRecording.Should().BeFalse();
        await hooks.StopAsync();
    }
}
