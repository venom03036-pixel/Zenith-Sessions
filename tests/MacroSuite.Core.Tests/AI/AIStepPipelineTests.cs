using System.Buffers;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Audit;
using MacroSuite.Core.Capture;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Security;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Usage;
using MacroSuite.SDK.AI;
using Xunit;

namespace MacroSuite.Core.Tests.AI;

public class AIStepPipelineTests
{
    private sealed class SimpleMemoryOwner<T> : IMemoryOwner<T>
    {
        public Memory<T> Memory { get; }
        public SimpleMemoryOwner(int size) => Memory = new T[size];
        public void Dispose() { }
    }

    private sealed class MockCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            int width = Math.Max(1, region.Width);
            int height = Math.Max(1, region.Height);
            int stride = width * 4;
            var owner = new SimpleMemoryOwner<byte>(height * stride);
            return Task.FromResult(new CaptureFrame(
                width,
                height,
                stride,
                FramePixelFormat.Bgra32,
                CaptureSource.Gdi,
                owner,
                originX: region.X,
                originY: region.Y));
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) =>
            CaptureRegionAsync(new Rectangle(0, 0, 1920, 1080), ct);

        public void Dispose() { }
    }

    private sealed class MockInputPlayer : IInputPlayer
    {
        public List<InputEvent> PlayedEvents { get; } = new();
        public bool IsPlaying => false;

        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            PlayedEvents.AddRange(events);
            return Task.CompletedTask;
        }

        public void ReleaseAllHeldKeys() { }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { }
    }

    private sealed class MockAuditLogger : IAuditLogger
    {
        public AuditLoggerHealth Health => AuditLoggerHealth.Running;
        public List<AuditEntry> Entries { get; } = new();

        public void Enqueue(AuditEntry entry) => Entries.Add(entry);
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    private sealed class MockUsageRepository : IUsageRepository
    {
        public List<UsageRecord> Records { get; } = new();

        public Task RecordAsync(UsageRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<decimal> GetTotalEstimatedCostAsync(DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult(Records.Sum(r => r.EstimatedCostUsd));

        public Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UsageRecord>>(Records.TakeLast(count).ToList());

        public Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default)
        {
            var slice = Records.Where(r => r.TimestampUtc >= since).ToList();
            return Task.FromResult(new UsageTotals(
                slice.Sum(r => r.EstimatedCostUsd),
                slice.Sum(r => r.InputTokens),
                slice.Sum(r => r.OutputTokens),
                slice.Count));
        }

        public void Dispose() { }
    }

    private sealed class MockSecretVault : ISecretVault
    {
        private readonly Dictionary<string, string> _secrets = new();
        public void SetSecret(string keyName, string secretValue) => _secrets[keyName] = secretValue;
        public string? GetSecret(string keyName) => _secrets.TryGetValue(keyName, out var v) ? v : null;
        public bool HasSecret(string keyName) => _secrets.ContainsKey(keyName);
        public bool RemoveSecret(string keyName) => _secrets.Remove(keyName);
        public IReadOnlyList<string> ListSecretKeys() => _secrets.Keys.ToList();
    }

    private sealed class MockVisionProvider : IAIVisionProvider
    {
        public string ProviderId => "mock_ai";
        public string DisplayName => "Mock AI";
        public IReadOnlyList<string> SupportedModels => ["mock-model"];
        public string DefaultModel => "mock-model";

        public AIRequest? LastRequest { get; private set; }
        public AIAction ReturnAction { get; set; } = new() { ActionType = "click", X = 200, Y = 200, Confidence = 0.95 };
        public decimal EstimatedCostUsd { get; set; } = 0.0015m;
        public int AnalyzeCalls { get; private set; }

        public VisionEncodePlan? EncodePlan { get; set; }

        public Task<AIResponse> AnalyzeAsync(AIRequest request, string apiKey, CancellationToken ct = default)
        {
            AnalyzeCalls++;
            LastRequest = request;
            return Task.FromResult(new AIResponse
            {
                Action = ReturnAction,
                InputTokens = 120,
                OutputTokens = 15,
                EstimatedCostUsd = EstimatedCostUsd
            });
        }

        public VisionEncodePlan PlanEncode(int cropWidth, int cropHeight, string? modelId)
            => EncodePlan ?? VisionEncodePlanner.Fit720p(cropWidth, cropHeight);

        public Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey, string? modelId = null, CancellationToken ct = default) =>
            Task.FromResult(ApiKeyValidationResult.Success(ProviderId, modelId ?? DefaultModel, TimeSpan.FromMilliseconds(25)));
    }

    [Fact]
    public async Task ExecuteAsync_UnlicensedAIVision_ThrowsFeatureNotLicensedException()
    {
        var license = LicenseInfo.CreateFree();
        var featureGate = new FeatureGate(new MockLicenseService(license)); // Free tier does not include AIVision

        var capture = new MockCaptureService();
        var context = new MacroContext(capture)
        {
            FeatureGate = featureGate
        };

        var step = new AIStep("Click login");
        var act = async () => await step.ExecuteAsync(context);

        await act.Should().ThrowAsync<FeatureNotLicensedException>()
            .Where(e => e.Feature == FeatureId.AIVision);
    }

    [Fact]
    public async Task ExecuteAsync_UnlicensedAutonomousMode_ForcesInteractiveApprovalAndDoesNotDispatch()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Pro,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.UsageTracking]
        };
        var vault = new MockSecretVault();
        vault.SetSecret("mock_ai", "test_key");
        var inputPlayer = new MockInputPlayer();
        var auditLogger = new MockAuditLogger();
        var context = new MacroContext(new MockCaptureService())
        {
            FeatureGate = new FeatureGate(new MockLicenseService(license)),
            InputPlayer = inputPlayer,
            AuditLogger = auditLogger,
            VisionRouter = new AIVisionRouter(vault, [new MockVisionProvider()]) { DefaultProviderId = "mock_ai" },
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            AutonomousMode = true,
            ForegroundWindow = new StubForegroundWindow { Title = "Roblox" }
        };

        var act = async () => await new AIStep("Click OK", providerId: "mock_ai") { ExpectedWindow = "Roblox" }
            .ExecuteAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not approved*");
        inputPlayer.PlayedEvents.Should().BeEmpty();
        auditLogger.Entries.Should().Contain(e => e.ApprovalResult != null && e.ApprovalResult.Contains("interactive"));
    }

    [Fact]
    public async Task ExecuteAsync_FullSuccessfulPipeline_ExecutesAndAudits()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Enterprise,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.UsageTracking, FeatureId.AutonomousMode]
        };
        var featureGate = new FeatureGate(new MockLicenseService(license));

        var capture = new MockCaptureService();
        var inputPlayer = new MockInputPlayer();
        var auditLogger = new MockAuditLogger();
        var usageRepo = new MockUsageRepository();
        var usageTracker = new UsageTracker(usageRepo, featureGate);

        var vault = new MockSecretVault();
        vault.SetSecret("mock_ai", "test_key");
        var provider = new MockVisionProvider();
        var router = new AIVisionRouter(vault, [provider]) { DefaultProviderId = "mock_ai" };

        var context = new MacroContext(capture)
        {
            FeatureGate = featureGate,
            InputPlayer = inputPlayer,
            AuditLogger = auditLogger,
            UsageTracker = usageTracker,
            VisionRouter = router,
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            AutonomousMode = true,
            ForegroundWindow = new StubForegroundWindow { Title = "Roblox" }
        };

        var step = new AIStep("Click submit", targetRegion: new Rectangle(0, 0, 500, 500), providerId: "mock_ai")
        {
            ExpectedWindow = "Roblox"
        };

        await step.ExecuteAsync(context);

        // Verify input simulation
        inputPlayer.PlayedEvents.Should().NotBeEmpty();
        inputPlayer.PlayedEvents.Any(e => e.Type == InputEventType.MouseDown && e.X == 200 && e.Y == 200).Should().BeTrue();

        // Verify audit log
        auditLogger.Entries.Should().HaveCount(1);
        auditLogger.Entries[0].ApprovalResult.Should().Contain("Approved");
        auditLogger.Entries[0].ApprovalResult.Should().Contain("automatic");
        auditLogger.Entries[0].ExecutedAction.Should().Contain("200");

        // Verify usage record
        usageRepo.Records.Should().HaveCount(1);
        usageRepo.Records[0].EstimatedCostUsd.Should().Be(0.0015m);
        usageRepo.Records[0].Feature.Should().Be(FeatureId.AIVision);
    }

    [Fact]
    public async Task ExecuteAsync_RegionCapture_MapsImageCoordinatesToVirtualDesktop()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Enterprise,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.AutonomousMode]
        };
        var provider = new MockVisionProvider
        {
            ReturnAction = new AIAction
            {
                ActionType = "click",
                X = 20,
                Y = 30,
                Confidence = 0.95
            }
        };
        var vault = new MockSecretVault();
        vault.SetSecret(provider.ProviderId, "test_key");
        var inputPlayer = new MockInputPlayer();
        var context = new MacroContext(new MockCaptureService())
        {
            FeatureGate = new FeatureGate(new MockLicenseService(license)),
            InputPlayer = inputPlayer,
            VisionRouter = new AIVisionRouter(vault, [provider]) { DefaultProviderId = provider.ProviderId },
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            AutonomousMode = true,
            ForegroundWindow = new StubForegroundWindow { Title = "Roblox" }
        };
        var step = new AIStep(
            "Click the target",
            targetRegion: new Rectangle(-300, 200, 500, 400),
            providerId: provider.ProviderId)
        {
            ExpectedWindow = "Roblox"
        };

        await step.ExecuteAsync(context);

        inputPlayer.PlayedEvents.Should().Contain(e =>
            e.Type == InputEventType.MouseDown && e.X == -280 && e.Y == 230);
    }

    [Fact]
    public async Task ExecuteAsync_TextAction_DispatchesUnicodeEvents()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Enterprise,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.AutonomousMode]
        };
        var provider = new MockVisionProvider
        {
            ReturnAction = new AIAction
            {
                ActionType = "text",
                Text = "Hi\n",
                Confidence = 0.95
            }
        };
        var vault = new MockSecretVault();
        vault.SetSecret(provider.ProviderId, "test_key");
        var inputPlayer = new MockInputPlayer();
        var context = new MacroContext(new MockCaptureService())
        {
            FeatureGate = new FeatureGate(new MockLicenseService(license)),
            InputPlayer = inputPlayer,
            VisionRouter = new AIVisionRouter(vault, [provider]) { DefaultProviderId = provider.ProviderId },
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            AutonomousMode = true,
            ForegroundWindow = new StubForegroundWindow { Title = "Roblox" }
        };

        await new AIStep("Type greeting", providerId: provider.ProviderId) { ExpectedWindow = "Roblox" }
            .ExecuteAsync(context);

        inputPlayer.PlayedEvents.Should().Contain(e => e.Type == InputEventType.Unicode && e.Data == 'H');
        inputPlayer.PlayedEvents.Should().Contain(e => e.Type == InputEventType.Unicode && e.Data == 'i');
        inputPlayer.PlayedEvents.Should().Contain(e => e.Type == InputEventType.KeyDown && e.Data == 0x0D);
    }

    [Fact]
    public async Task ExecuteAsync_ExpectedWindowMismatch_RejectsBeforeDispatch()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Enterprise,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.AutonomousMode]
        };
        var vault = new MockSecretVault();
        vault.SetSecret("mock_ai", "test_key");
        var inputPlayer = new MockInputPlayer();
        var context = new MacroContext(new MockCaptureService())
        {
            FeatureGate = new FeatureGate(new MockLicenseService(license)),
            InputPlayer = inputPlayer,
            VisionRouter = new AIVisionRouter(vault, [new MockVisionProvider()]) { DefaultProviderId = "mock_ai" },
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            AutonomousMode = true,
            ForegroundWindow = new StubForegroundWindow { Title = "Calculator" }
        };

        var step = new AIStep("Click submit", providerId: "mock_ai")
        {
            ExpectedWindow = "Roblox"
        };

        var act = async () => await step.ExecuteAsync(context);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*safety validator*");
        inputPlayer.PlayedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ExpectedWindowMatch_Dispatches()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Enterprise,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.AutonomousMode]
        };
        var vault = new MockSecretVault();
        vault.SetSecret("mock_ai", "test_key");
        var inputPlayer = new MockInputPlayer();
        var context = new MacroContext(new MockCaptureService())
        {
            FeatureGate = new FeatureGate(new MockLicenseService(license)),
            InputPlayer = inputPlayer,
            VisionRouter = new AIVisionRouter(vault, [new MockVisionProvider()]) { DefaultProviderId = "mock_ai" },
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            AutonomousMode = true,
            ForegroundWindow = new StubForegroundWindow { Title = "Roblox" }
        };

        await new AIStep("Click submit", providerId: "mock_ai") { ExpectedWindow = "Roblox" }
            .ExecuteAsync(context);

        inputPlayer.PlayedEvents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ApprovalTimedOut_ThrowsInsteadOfSilentSuccess()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Enterprise,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.UsageTracking, FeatureId.AutonomousMode]
        };
        var vault = new MockSecretVault();
        vault.SetSecret("mock_ai", "test_key");
        var provider = new MockVisionProvider();
        var usageRepo = new MockUsageRepository();
        var featureGate = new FeatureGate(new MockLicenseService(license));
        var context = new MacroContext(new MockCaptureService())
        {
            FeatureGate = featureGate,
            InputPlayer = new MockInputPlayer(),
            VisionRouter = new AIVisionRouter(vault, [provider]) { DefaultProviderId = "mock_ai" },
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoReject),
            AutonomousMode = true,
            UsageTracker = new UsageTracker(usageRepo, featureGate),
            ForegroundWindow = new StubForegroundWindow { Title = "Roblox" }
        };

        var act = async () => await new AIStep("Click submit", providerId: "mock_ai") { ExpectedWindow = "Roblox" }.ExecuteAsync(context);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not approved*");

        usageRepo.Records.Should().ContainSingle();
        usageRepo.Records[0].InputTokens.Should().Be(120);
        usageRepo.Records[0].OutputTokens.Should().Be(15);
    }

    [Fact]
    public async Task ExecuteAsync_DailyBurstPreflight_DoesNotCallProvider()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Enterprise,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.UsageTracking, FeatureId.AutonomousMode]
        };
        var featureGate = new FeatureGate(new MockLicenseService(license));
        var usageRepo = new MockUsageRepository();
        var usageTracker = new UsageTracker(usageRepo, featureGate, dailySpendCapUsd: 0.01m);
        var provider = new MockVisionProvider();
        var vault = new MockSecretVault();
        vault.SetSecret("mock_ai", "test_key");
        var inputPlayer = new MockInputPlayer();
        var context = new MacroContext(new MockCaptureService())
        {
            FeatureGate = featureGate,
            InputPlayer = inputPlayer,
            UsageTracker = usageTracker,
            VisionRouter = new AIVisionRouter(vault, [provider]) { DefaultProviderId = "mock_ai" },
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            AutonomousMode = true,
            ForegroundWindow = new StubForegroundWindow { Title = "Roblox" }
        };

        var act = () => new AIStep("Click submit", providerId: "mock_ai") { ExpectedWindow = "Roblox" }.ExecuteAsync(context);
        await act.Should().ThrowAsync<SpendCapExceededException>().WithMessage("*Daily burst*");
        provider.AnalyzeCalls.Should().Be(0);
        inputPlayer.PlayedEvents.Should().BeEmpty();
        usageRepo.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ActualOvershoot_RecordsUsageButDoesNotDispatch()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Enterprise,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.UsageTracking, FeatureId.AutonomousMode]
        };
        var featureGate = new FeatureGate(new MockLicenseService(license));
        var usageRepo = new MockUsageRepository();
        await usageRepo.RecordAsync(new UsageRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            EstimatedCostUsd = 0.90m,
            Feature = FeatureId.AIVision
        });
        var usageTracker = new UsageTracker(usageRepo, featureGate, dailySpendCapUsd: 1.00m);
        var provider = new MockVisionProvider { EstimatedCostUsd = 0.15m };
        var vault = new MockSecretVault();
        vault.SetSecret("mock_ai", "test_key");
        var inputPlayer = new MockInputPlayer();
        var context = new MacroContext(new MockCaptureService())
        {
            FeatureGate = featureGate,
            InputPlayer = inputPlayer,
            UsageTracker = usageTracker,
            VisionRouter = new AIVisionRouter(vault, [provider]) { DefaultProviderId = "mock_ai" },
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            AutonomousMode = true,
            ForegroundWindow = new StubForegroundWindow { Title = "Roblox" }
        };

        var act = () => new AIStep("Click submit", providerId: "mock_ai") { ExpectedWindow = "Roblox" }.ExecuteAsync(context);
        await act.Should().ThrowAsync<SpendCapExceededException>().WithMessage("*Daily burst*");
        provider.AnalyzeCalls.Should().Be(1);
        inputPlayer.PlayedEvents.Should().BeEmpty();
        usageRepo.Records.Should().Contain(r => r.EstimatedCostUsd == 0.15m);
    }

    [Fact]
    public async Task ExecuteAsync_FullHdCapture_DownscalesTo720pAndMapsClicks()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Enterprise,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.AutonomousMode]
        };
        var provider = new MockVisionProvider
        {
            ReturnAction = new AIAction
            {
                ActionType = "click",
                X = 640,
                Y = 360,
                Confidence = 0.95
            }
        };
        var vault = new MockSecretVault();
        vault.SetSecret(provider.ProviderId, "test_key");
        var inputPlayer = new MockInputPlayer();
        var context = new MacroContext(new MockCaptureService())
        {
            FeatureGate = new FeatureGate(new MockLicenseService(license)),
            InputPlayer = inputPlayer,
            VisionRouter = new AIVisionRouter(vault, [provider]) { DefaultProviderId = provider.ProviderId },
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            AutonomousMode = true,
            ForegroundWindow = new StubForegroundWindow { Title = "Roblox" }
        };
        var step = new AIStep("Click the center", providerId: provider.ProviderId)
        {
            ExpectedWindow = "Roblox"
        };

        await step.ExecuteAsync(context);

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.ImageWidth.Should().Be(ImageDownscale.VisionMaxWidth);
        provider.LastRequest.ImageHeight.Should().Be(ImageDownscale.VisionMaxHeight);
        provider.LastRequest.ImageBytes.Should().NotBeNull();
        provider.LastRequest.ImageBytes!.Length.Should().BeLessThan(1920 * 1080 * 3);

        inputPlayer.PlayedEvents.Should().Contain(e =>
            e.Type == InputEventType.MouseDown && e.X == 960 && e.Y == 540);
    }

    [Fact]
    public async Task ExecuteAsync_GeminiNormalizedSwap_MapsToNative()
    {
        var license = new LicenseInfo
        {
            Type = LicenseType.Enterprise,
            LicensedFeatures = [FeatureId.AIVision, FeatureId.AutonomousMode]
        };
        var provider = new MockVisionProvider
        {
            EncodePlan = VisionEncodePlanner.Gemini(1920, 1080),
            ReturnAction = new AIAction
            {
                ActionType = "click",
                X = 0,
                Y = 1000,
                Confidence = 0.95
            }
        };
        var vault = new MockSecretVault();
        vault.SetSecret(provider.ProviderId, "test_key");
        var inputPlayer = new MockInputPlayer();
        var context = new MacroContext(new MockCaptureService())
        {
            FeatureGate = new FeatureGate(new MockLicenseService(license)),
            InputPlayer = inputPlayer,
            VisionRouter = new AIVisionRouter(vault, [provider]) { DefaultProviderId = provider.ProviderId },
            ApprovalGate = new ApprovalGate(ApprovalPolicy.AutoApprove),
            AutonomousMode = true,
            ForegroundWindow = new StubForegroundWindow { Title = "Roblox" }
        };
        var step = new AIStep("Click", providerId: provider.ProviderId)
        {
            ExpectedWindow = "Roblox"
        };

        await step.ExecuteAsync(context);

        provider.LastRequest!.ImageWidth.Should().Be(1920);
        provider.LastRequest.ImageHeight.Should().Be(1080);
        inputPlayer.PlayedEvents.Should().Contain(e =>
            e.Type == InputEventType.MouseDown && e.X == 1919 && e.Y == 0);
    }

    private sealed class StubForegroundWindow : IForegroundWindowInfo
    {
        public string? Title { get; set; }
        public string? GetForegroundWindowTitle() => Title;
    }

    private sealed class MockLicenseService : ILicenseService
    {
        private readonly LicenseInfo _license;
        public MockLicenseService(LicenseInfo license) => _license = license;
        public event EventHandler<LicenseChangedEventArgs>? LicenseChanged { add { } remove { } }
        public LicenseInfo GetCurrentLicense() => _license;
        public bool IsFeatureLicensed(FeatureId feature) => _license.IsFeatureLicensed(feature);
        public Task<LicenseValidationResult> ValidateAsync(CancellationToken ct = default) =>
            Task.FromResult(LicenseValidationResult.Success(_license));
    }
}
