using System.IO;
using System.Net.Http;
using System.Xml.Linq;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.App.Themes;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Security;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Vision;
using MacroSuite.Infrastructure.AIProviders;
using MacroSuite.SDK.AI;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public class SettingsViewModelTests
{
    [Fact]
    public void ScanBudget_PersistsExternalChangesAndDetachesItsRuntimeSubscription()
    {
        var paths = IsolatedPaths(); var vault = new InMemorySecretVault(); var budget = new ImageScanBudget();
        var vm = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths, imageScanBudget: budget);
        vm.ImageScanBudgetEnabled.Should().BeTrue();
        budget.Enabled = false;
        vm.ImageScanBudgetEnabled.Should().BeFalse();
        var reloadedBudget = new ImageScanBudget();
        var reloaded = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths, imageScanBudget: reloadedBudget);
        reloaded.ImageScanBudgetEnabled.Should().BeFalse(); reloadedBudget.Enabled.Should().BeFalse();
        vm.DetachRuntimeSettings(); reloaded.DetachRuntimeSettings();
        string config = Path.Combine(paths.ConfigDirectory, "settings.json"); string before = File.ReadAllText(config);
        budget.Enabled = true; File.ReadAllText(config).Should().Be(before);
    }

    [Fact]
    public void ThemeChoice_PersistsAndReloadsWhileKeepingOtherPreferences()
    {
        var paths = IsolatedPaths();
        var vault = new InMemorySecretVault();
        var vm = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);
        vm.SelectedTheme.Id.Should().Be(ThemeCatalog.DefaultId);
        vm.PanicHotkey = "Ctrl+Shift+L";
        vm.LibraryPaneWidth = 275;
        vm.TrySaveSpendCaps(7m, 70m).Should().BeTrue();
        var changes = new List<string>();
        vm.ThemeChanged += changes.Add;
        vm.SelectedTheme = ThemeCatalog.Resolve("amber-night");
        vm.SelectedTheme = ThemeCatalog.Resolve("amber-night");

        changes.Should().Equal("amber-night");
        var reloaded = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);
        reloaded.SelectedTheme.Id.Should().Be("amber-night");
        reloaded.PanicHotkey.Should().Be("Ctrl+Shift+L");
        reloaded.LibraryPaneWidth.Should().Be(275);
        reloaded.DailySpendCapUsd.Should().Be(7m);
        reloaded.MonthlySpendCapUsd.Should().Be(70m);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"themeId\":\"unknown-theme\",\"libraryPaneWidth\":280}")]
    [InlineData("{\"themeId\":null,\"libraryPaneWidth\":280}")]
    [InlineData("{\"themeId\":42,\"libraryPaneWidth\":280}")]
    public void MissingOrInvalidTheme_UsesGraphiteWithoutDiscardingOtherSettings(string json)
    {
        var paths = IsolatedPaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "settings.json"), json);
        var vault = new InMemorySecretVault();
        var vm = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);
        vm.SelectedTheme.Id.Should().Be(ThemeCatalog.DefaultId);
        if (json.Contains("libraryPaneWidth", StringComparison.Ordinal))
            vm.LibraryPaneWidth.Should().Be(280);
    }

    [Fact]
    public void AutoClickerShortcut_PersistsCombosAndExplicitlyUnboundState()
    {
        var paths = IsolatedPaths();
        var vault = new InMemorySecretVault();
        var vm = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);
        vm.AutoClickerHotkey = "Ctrl+Shift+L";
        var reloaded = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);
        reloaded.AutoClickerHotkey.Should().Be("Ctrl+Shift+L");
        reloaded.AutoClickerHotkey = "";
        new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths).AutoClickerHotkey.Should().BeEmpty();
    }

    [Fact]
    public void LibraryPaneWidth_PersistsAndClampsWithoutAcceptingNonFiniteValues()
    {
        var paths = IsolatedPaths();
        var vault = new InMemorySecretVault();
        var vm = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);
        vm.LibraryPaneWidth.Should().Be(240);
        vm.LibraryPaneWidth = 270;
        var reloaded = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);
        reloaded.LibraryPaneWidth.Should().Be(270);
        reloaded.LibraryPaneWidth = 900;
        reloaded.LibraryPaneWidth.Should().Be(360);
        reloaded.LibraryPaneWidth = 100;
        reloaded.LibraryPaneWidth.Should().Be(220);
        reloaded.LibraryPaneWidth = double.NaN;
        reloaded.LibraryPaneWidth.Should().Be(220);
    }

    [Fact]
    public void RawMouseRecording_DefaultsOff_AndPersistsIndependentlyOfPlaybackMode()
    {
        var paths = IsolatedPaths();
        var vault = new InMemorySecretVault();
        var first = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);
        first.UseRawMouseRecording.Should().BeFalse();
        first.CameraModeIndex = 1;
        first.UseRawMouseRecording = true;

        var reloaded = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);

        reloaded.UseRawMouseRecording.Should().BeTrue();
        reloaded.CameraModeIndex.Should().Be(1);
        reloaded.UseRawMouseRecording = false;
        reloaded.CameraModeIndex.Should().Be(1);
    }

    [Fact]
    public void SpendCaps_FailedAtomicSaveReportsFailureAndRetainsPreviousValues()
    {
        var paths = IsolatedPaths();
        var vault = new InMemorySecretVault();
        var vm = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);
        vm.TrySaveSpendCaps(3m, 30m).Should().BeTrue();
        string path = Path.Combine(paths.ConfigDirectory, "settings.json");
        string previous = File.ReadAllText(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            vm.TrySaveSpendCaps(20m, 200m).Should().BeFalse();

        vm.DailySpendCapUsd.Should().Be(3m);
        vm.MonthlySpendCapUsd.Should().Be(30m);
        vm.PersistenceStatus.Should().StartWith("Could not save settings:");
        File.ReadAllText(path).Should().Be(previous);
    }

    private sealed class InMemorySecretVault : ISecretVault
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

        public void SetSecret(string keyName, string secretValue) => _secrets[keyName] = secretValue;
        public string? GetSecret(string keyName) => _secrets.TryGetValue(keyName, out var val) ? val : null;
        public bool HasSecret(string keyName) => _secrets.ContainsKey(keyName);
        public bool RemoveSecret(string keyName) => _secrets.Remove(keyName);
        public IReadOnlyList<string> ListSecretKeys() => _secrets.Keys.ToList();
    }

    private static AppDataPathResolver IsolatedPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "ZenithMacrosSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new AppDataPathResolver(root);
    }

    private static SettingsViewModel CreateVm(
        ISecretVault vault,
        AIVisionRouter router,
        IDisplayInfoService? display = null)
        => new(vault, router, display, IsolatedPaths());

    private sealed class MockVisionProvider : IAIVisionProvider
    {
        public string ProviderId { get; set; } = "openai";
        public string DisplayName { get; set; } = "OpenAI";
        public IReadOnlyList<string> SupportedModels { get; set; } = new[] { "gpt-4o", "gpt-4o-mini" };
        public string DefaultModel { get; set; } = "gpt-4o";

        public ApiKeyValidationResult ValidationResultToReturn { get; set; } =
            ApiKeyValidationResult.Success("openai", "gpt-4o", TimeSpan.FromMilliseconds(120), availableModels: new[] { "gpt-4o", "gpt-4o-mini" });

        public Task<AIResponse> AnalyzeAsync(AIRequest request, string apiKey, CancellationToken ct = default)
        {
            return Task.FromResult(new AIResponse
            {
                Action = new AIAction { ActionType = "click", X = 100, Y = 100 },
                InputTokens = 100,
                OutputTokens = 50,
                EstimatedCostUsd = 0.002m
            });
        }

        public Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey, string? modelId = null, CancellationToken ct = default)
        {
            return Task.FromResult(ValidationResultToReturn);
        }
    }

    [Fact]
    public void Constructor_InitializesDefaultProviderAndModels()
    {
        var vault = new InMemorySecretVault();
        var mockProvider = new MockVisionProvider { ProviderId = "openai" };
        var router = new AIVisionRouter(vault, new[] { mockProvider });

        var vm = CreateVm(vault, router);

        vm.SelectedProvider.Should().Be("openai");
        vm.AvailableModels.Should().Contain("gpt-4o");
        vm.SelectedModel.Should().Be("gpt-4o");
        router.DefaultProviderId.Should().Be("openai");
        router.DefaultModelId.Should().Be("gpt-4o");
        vm.HasStoredKey.Should().BeFalse();
    }

    [Fact]
    public void SelectedProvider_UpdatesVisionRouterDefault()
    {
        var vault = new InMemorySecretVault();
        var openai = new MockVisionProvider { ProviderId = "openai" };
        var gemini = new MockVisionProvider
        {
            ProviderId = "gemini",
            DisplayName = "Gemini",
            DefaultModel = "gemini-1.5-flash",
            SupportedModels = new[] { "gemini-1.5-flash", "gemini-1.5-pro" }
        };
        var router = new AIVisionRouter(vault, [openai, gemini]);

        var vm = CreateVm(vault, router);
        vm.SelectedProvider = "gemini";

        router.DefaultProviderId.Should().Be("gemini");
        router.DefaultModelId.Should().Be("gemini-1.5-flash");
        vm.SelectedModel.Should().Be("gemini-1.5-flash");
    }

    [Fact]
    public void SelectedProvider_PersistsAndReloads()
    {
        var vault = new InMemorySecretVault();
        var openai = new MockVisionProvider { ProviderId = "openai" };
        var gemini = new MockVisionProvider
        {
            ProviderId = "gemini",
            DefaultModel = "gemini-1.5-flash",
            SupportedModels = new[] { "gemini-1.5-flash" }
        };
        var paths = IsolatedPaths();
        var first = new SettingsViewModel(vault, new AIVisionRouter(vault, [openai, gemini]), paths: paths);
        first.SelectedProvider = "gemini";

        var second = new SettingsViewModel(vault, new AIVisionRouter(vault, [openai, gemini]), paths: paths);

        second.SelectedProvider.Should().Be("gemini");
        second.SelectedModel.Should().Be("gemini-1.5-flash");
    }

    [Fact]
    public void SaveApiKey_StoresEncryptedInVaultAndClearsInput()
    {
        var vault = new InMemorySecretVault();
        var mockProvider = new MockVisionProvider { ProviderId = "openai" };
        var router = new AIVisionRouter(vault, new[] { mockProvider });

        var vm = CreateVm(vault, router);
        vm.ApiKeyInput = "sk-test-secret-key";

        vm.SaveKeyCommand.Execute(null);

        vm.HasStoredKey.Should().BeTrue();
        vm.ApiKeyInput.Should().BeEmpty();
        vault.GetSecret("openai").Should().Be("sk-test-secret-key");
        vm.TestStatusMessage.Should().Contain("DPAPI vault");
    }

    [Fact]
    public void RemoveApiKey_DeletesSecretFromVault()
    {
        var vault = new InMemorySecretVault();
        vault.SetSecret("openai", "existing-key");
        var mockProvider = new MockVisionProvider { ProviderId = "openai" };
        var router = new AIVisionRouter(vault, new[] { mockProvider });

        var vm = CreateVm(vault, router);
        vm.HasStoredKey.Should().BeTrue();

        vm.RemoveKeyCommand.Execute(null);

        vm.HasStoredKey.Should().BeFalse();
        vault.HasSecret("openai").Should().BeFalse();
        vm.TestStatusMessage.Should().Contain("removed");
    }

    [Fact]
    public async Task TestApiKey_Successful_SetsLatencyAndStatus()
    {
        var vault = new InMemorySecretVault();
        vault.SetSecret("openai", "valid-key");
        var mockProvider = new MockVisionProvider
        {
            ProviderId = "openai",
            ValidationResultToReturn = ApiKeyValidationResult.Success("openai", "gpt-4o", TimeSpan.FromMilliseconds(85), "Models listed successfully.", new[] { "gpt-4o", "gpt-4-turbo" })
        };
        var router = new AIVisionRouter(vault, new[] { mockProvider });

        var vm = CreateVm(vault, router);

        await vm.TestApiKeyAsync();

        vm.TestIsValid.Should().BeTrue();
        vm.TestLatencyText.Should().Be("85 ms");
        vm.TestStatusMessage.Should().Contain("Connected");
    }

    [Fact]
    public async Task TestApiKey_Failure_SetsInvalidAndErrorDetails()
    {
        var vault = new InMemorySecretVault();
        vault.SetSecret("openai", "bad-key");
        var mockProvider = new MockVisionProvider
        {
            ProviderId = "openai",
            ValidationResultToReturn = ApiKeyValidationResult.Failure("openai", "gpt-4o", "HTTP 401: Unauthorized", TimeSpan.FromMilliseconds(45), 401)
        };
        var router = new AIVisionRouter(vault, new[] { mockProvider });

        var vm = CreateVm(vault, router);

        await vm.TestApiKeyAsync();

        vm.TestIsValid.Should().BeFalse();
        vm.TestStatusMessage.Should().Contain("Verification Failed");
        vm.TestStatusMessage.Should().Contain("401");
    }

    [Fact]
    public async Task Gemini_TestApiKey_ReplacesCatalogWithServedGenerateContentModels()
    {
        var vault = new InMemorySecretVault();
        vault.SetSecret("gemini", "AIzaSyTest");
        var handler = new GeminiListModelsHandler("""
            {
              "models": [
                { "name": "models/gemini-3.5-flash", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-3.5-flash-lite", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-2.5-flash", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-1.5-flash", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-embedding-001", "supportedGenerationMethods": ["embedContent"] }
              ]
            }
            """);
        using var client = new HttpClient(handler);
        var openai = new MockVisionProvider { ProviderId = "openai" };
        var gemini = new GoogleGeminiVisionProvider(client);
        var router = new AIVisionRouter(vault, [openai, gemini]);
        var vm = CreateVm(vault, router);

        vm.SelectedProvider = "gemini";
        await vm.TestApiKeyAsync();

        vm.TestIsValid.Should().BeTrue();
        vm.AvailableModels.Should().Equal("gemini-3.5-flash-lite", "gemini-2.5-flash", "gemini-3.5-flash");
        vm.AvailableModels.Should().NotContain("gemini-1.5-flash");
        vm.AvailableModels.Should().NotContain("gemini-embedding-001");
        vm.SelectedModel.Should().Be("gemini-3.5-flash-lite");
        router.DefaultModelId.Should().Be("gemini-3.5-flash-lite");
    }

    [Fact]
    public async Task Gemini_TestApiKey_KeepsSavedServedModel_AndDropsRetiredIds()
    {
        var vault = new InMemorySecretVault();
        vault.SetSecret("gemini", "AIzaSyTest");
        var paths = IsolatedPaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(
            Path.Combine(paths.ConfigDirectory, "settings.json"),
            """{"selectedProvider":"gemini","selectedModel":"gemini-1.5-flash"}""");

        var handler = new GeminiListModelsHandler("""
            {
              "models": [
                { "name": "models/gemini-3.5-flash-lite", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-3.5-flash", "supportedGenerationMethods": ["generateContent"] }
              ]
            }
            """);
        using var client = new HttpClient(handler);
        var openai = new MockVisionProvider { ProviderId = "openai" };
        var gemini = new GoogleGeminiVisionProvider(client);
        var vm = new SettingsViewModel(vault, new AIVisionRouter(vault, [openai, gemini]), paths: paths);

        await vm.TestApiKeyAsync();

        vm.AvailableModels.Should().Equal("gemini-3.5-flash-lite", "gemini-3.5-flash");
        vm.SelectedModel.Should().Be("gemini-3.5-flash-lite");
        vm.AvailableModels.Should().NotContain("gemini-1.5-flash");
    }

    private sealed class GeminiListModelsHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly object _gate = new();

        public GeminiListModelsHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                request.RequestUri!.AbsolutePath.Should().Be("/v1beta/models");
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
                });
            }
        }
    }

    [Theory]
    [InlineData(500, 165)]
    [InlineData(1, 5)]
    [InlineData(100, 100)]
    [InlineData(165, 165)]
    public void TargetFps_ClampsToBounds(int input, int expected)
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var vm = CreateVm(vault, router);

        vm.TargetFps = input;

        vm.TargetFps.Should().Be(expected);
    }

    [Fact]
    public void SettingsViewModel_WithHighRefreshMonitor_SetsMaxTargetFps()
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var mockDisplay = new MockDisplayInfoService(240);
        var vm = CreateVm(vault, router, mockDisplay);

        vm.MaxTargetFps.Should().Be(240);
        vm.MonitorRefreshRateText.Should().Contain("240 Hz");

        vm.TargetFps = 200;
        vm.TargetFps.Should().Be(200);

        vm.TargetFps = 999;
        vm.TargetFps.Should().Be(240);
    }

    [Fact]
    public void TargetFpsInputText_SynchronizesWithTargetFps()
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var vm = CreateVm(vault, router);

        vm.TargetFps = 60;
        vm.TargetFpsInputText.Should().Be("60");

        vm.TargetFpsInputText = "144";
        vm.TargetFps.Should().Be(144);

        vm.TargetFps = 165;
        vm.TargetFpsInputText.Should().Be("165");
    }

    private sealed class MockDisplayInfoService : MacroSuite.Core.Abstractions.IDisplayInfoService
    {
        private readonly int _maxRate;
        public MockDisplayInfoService(int maxRate) => _maxRate = maxRate;
        public int GetMaxMonitorRefreshRate() => _maxRate;
        public int GetPrimaryMonitorRefreshRate() => _maxRate;
    }

    [Theory]
    [InlineData(100, 60)]
    [InlineData(1, 8)]
    [InlineData(15, 15)]
    public void ApprovalTimeout_ClampsToBounds(int input, int expected)
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var vm = CreateVm(vault, router);

        vm.ApprovalTimeoutSeconds = input;

        vm.ApprovalTimeoutSeconds.Should().Be(expected);
    }

    [Fact]
    public void AiSafetyLimits_PersistAndReload()
    {
        var vault = new InMemorySecretVault();
        var openai = new MockVisionProvider { ProviderId = "openai" };
        var paths = IsolatedPaths();
        var first = new SettingsViewModel(vault, new AIVisionRouter(vault, [openai]), paths: paths);
        first.MaxAiTextLength = 250;
        first.MinAiConfidence = 0.75;

        var second = new SettingsViewModel(vault, new AIVisionRouter(vault, [openai]), paths: paths);
        second.MaxAiTextLength.Should().Be(250);
        second.MinAiConfidence.Should().BeApproximately(0.75, 0.0001);
    }

    [Fact]
    public void AlwaysOnTop_RaisesEventAndUpdatesProperty()
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var vm = CreateVm(vault, router);

        bool targetValue = !vm.AlwaysOnTop;
        bool eventFired = false;
        bool receivedValue = false;
        vm.AlwaysOnTopChanged += val =>
        {
            eventFired = true;
            receivedValue = val;
        };

        vm.AlwaysOnTop = targetValue;

        vm.AlwaysOnTop.Should().Be(targetValue);
        eventFired.Should().BeTrue();
        receivedValue.Should().Be(targetValue);
    }

    [Fact]
    public void Hotkeys_RaiseEventsAndSetValues()
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var vm = CreateVm(vault, router);

        string? recFired = null;
        string? stopFired = null;
        vm.RecordHotkeyChanged += h => recFired = h;
        vm.StopHotkeyChanged += h => stopFired = h;

        string targetRec = vm.RecordHotkey == "F8" ? "F7" : "F8";
        string targetStop = vm.StopHotkey == "F9" ? "F10" : "F9";

        vm.RecordHotkey = targetRec;
        vm.StopHotkey = targetStop;

        vm.RecordHotkey.Should().Be(targetRec);
        vm.StopHotkey.Should().Be(targetStop);
        recFired.Should().Be(targetRec);
        stopFired.Should().Be(targetStop);
    }

    [Fact]
    public void PanicHotkey_DefaultsToF12_WithoutChangingExistingSettings()
    {
        var paths = IsolatedPaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "settings.json"),
            """{"recordHotkey":"Shift+9","stopHotkey":"Ctrl+Shift+L","targetFps":60}""");
        var vault = new InMemorySecretVault();

        var vm = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);

        vm.PanicHotkey.Should().Be("F12");
        vm.RecordHotkey.Should().Be("Shift+9");
        vm.StopHotkey.Should().Be("Ctrl+Shift+L");
        vm.TargetFps.Should().Be(60);
    }

    [Fact]
    public void PanicHotkey_MissingSettingUsesFreeFallback_AndPreservesTransportBinds()
    {
        var paths = IsolatedPaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "settings.json"),
            """{"recordHotkey":"F12","stopHotkey":"Ctrl+Shift+F12","playHotkey":"F4","pauseHotkey":"F5"}""");
        var vault = new InMemorySecretVault();

        var vm = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);

        vm.PanicHotkey.Should().Be("Ctrl+Alt+Shift+F12");
        vm.RecordHotkey.Should().Be("F12");
        vm.StopHotkey.Should().Be("Ctrl+Shift+F12");
        vm.PlayHotkey.Should().Be("F4");
        vm.PauseHotkey.Should().Be("F5");
    }

    [Theory]
    [InlineData("Shift+9")]
    [InlineData("Ctrl+Shift+L")]
    [InlineData("ESC")]
    [InlineData("G")]
    public void PanicHotkey_CustomBindPersistsAndRaisesEvent(string hotkey)
    {
        var paths = IsolatedPaths();
        var vault = new InMemorySecretVault();
        var first = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);
        string? notified = null;
        first.PanicHotkeyChanged += value => notified = value;

        first.PanicHotkey = hotkey;
        var reloaded = new SettingsViewModel(vault, new AIVisionRouter(vault), paths: paths);

        notified.Should().Be(hotkey);
        reloaded.PanicHotkey.Should().Be(hotkey);
        reloaded.PanicHotkeyDisplay.Should().Be(hotkey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NoSuchKey")]
    public void PanicHotkey_InvalidValueKeepsCurrentBind(string invalid)
    {
        var vault = new InMemorySecretVault();
        var vm = CreateVm(vault, new AIVisionRouter(vault));
        vm.PanicHotkey = "Shift+9";
        bool notified = false;
        vm.PanicHotkeyChanged += _ => notified = true;

        vm.PanicHotkey = invalid;

        vm.PanicHotkey.Should().Be("Shift+9");
        notified.Should().BeFalse();
    }

    [Fact]
    public void PanicHotkeyCapture_AcceptsEscape_AndReservesTheCurrentPanicBind()
    {
        var vault = new InMemorySecretVault();
        var vm = CreateVm(vault, new AIVisionRouter(vault));
        vm.BeginHotkeyCapture("panic");
        vm.PanicHotkeyDisplay.Should().Be("Press a key…");
        vm.CompleteHotkeyCapture("ESC").Should().BeTrue();
        vm.PanicHotkey.Should().Be("ESC");

        vm.BeginHotkeyCapture("panic");
        vm.CompleteHotkeyCapture("Shift+9").Should().BeTrue();
        vm.BeginHotkeyCapture("record");
        vm.CompleteHotkeyCapture("Shift+9").Should().BeTrue();
        vm.RecordHotkey.Should().Be("F2");
        vm.PanicHotkey.Should().Be("Shift+9");
    }

    [Fact]
    public void HotkeyCapture_ClickThenKey_SetsTheBind()
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var vm = CreateVm(vault, router);

        vm.BeginHotkeyCapture("record");
        vm.HotkeyCaptureTarget.Should().Be("record");
        vm.RecordHotkeyDisplay.Should().Be("Press a key…");

        vm.CompleteHotkeyCapture("F8").Should().BeTrue();
        vm.RecordHotkey.Should().Be("F8");
        vm.HotkeyCaptureTarget.Should().BeEmpty();
        vm.RecordHotkeyDisplay.Should().Be("F8");
    }

    [Fact]
    public void HotkeyCapture_DoesNotStealAnotherTransportBind()
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var vm = CreateVm(vault, router);

        string originalStop = vm.StopHotkey;
        vm.BeginHotkeyCapture("stop");
        vm.CompleteHotkeyCapture(vm.RecordHotkey).Should().BeTrue();
        vm.StopHotkey.Should().Be(originalStop);
    }

    [Fact]
    public void TransportHotkey_EmptyValueFallsBackToDefault()
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var vm = CreateVm(vault, router);

        vm.RecordHotkey = "   ";
        vm.RecordHotkey.Should().BeEmpty();
        vm.RecordHotkeyDisplay.Should().Be("—");
        vm.StopHotkey = "";
        vm.StopHotkey.Should().BeEmpty();
    }

    [Fact]
    public void SwitchToMiniModeCommand_RaisesRequestMiniMode()
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var vm = CreateVm(vault, router);

        bool requested = false;
        vm.RequestMiniMode += () => requested = true;

        vm.SwitchToMiniModeCommand.Execute(null);

        requested.Should().BeTrue();
    }

    [Fact]
    public void SettingsView_GroupsPreferencesAndKeepsSharedMiniToolbarAndShortcutBindings()
    {
        string baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MacroSuite.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull();
        string repoRoot = dir!.FullName;

        string settingsXamlPath = Path.Combine(repoRoot, "src", "MacroSuite.App", "Views", "SettingsView.xaml");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var settings = XDocument.Load(settingsXamlPath);
        var categories = settings.Descendants(wpf + "TabItem").ToArray();
        categories.Select(item => (string?)item.Attribute("Header")).Should().Equal(
            "General", "Appearance", "Recording", "Playback", "Shortcuts", "Detection & AI", "Advanced");
        categories[0].Descendants(wpf + "CheckBox").Should().Contain(element =>
            (string?)element.Attribute("IsChecked") == "{Binding AlwaysOnTop}");
        categories[0].Descendants(wpf + "Button").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Open Mini toolbar"
            && (string?)element.Attribute("Command") == "{Binding SwitchToMiniModeCommand}");
        categories[1].Descendants(wpf + "ListBox").Should().ContainSingle(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ThemePresets}"
            && (string?)element.Attribute("SelectedItem") == "{Binding SelectedPreset, Mode=TwoWay}");
        categories[2].Descendants(wpf + "CheckBox").Should().Contain(element =>
            (string?)element.Attribute("IsChecked") == "{Binding MiniModeWhileRecording}");

        var shortcutButtons = categories[4].Descendants(wpf + "Button").ToArray();
        shortcutButtons.Select(button => (string?)button.Attribute("CommandParameter"))
            .Should().Equal("record", "stop", "play", "pause", "panic", "mini-click-through");
        shortcutButtons.Should().OnlyContain(button =>
            (string?)button.Attribute("Command") == "{Binding BeginHotkeyCaptureCommand}");
        categories[5].Descendants(wpf + "PasswordBox").Should().ContainSingle();

        string mainXamlPath = Path.Combine(repoRoot, "src", "MacroSuite.App", "MainWindow.xaml");
        var main = XDocument.Load(mainXamlPath);
        main.Descendants(wpf + "Button").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Mini toolbar"
            && (string?)element.Attribute("Command") == "{Binding SettingsVM.SwitchToMiniModeCommand}");
        main.Descendants(wpf + "Button").Should().Contain(element =>
            (string?)element.Attribute("Command") == "{Binding PanicCommand}");
    }

    [Fact]
    public async Task Views_CanBeInstantiated_WithoutXamlErrors()
    {
        await WpfTestApplication.RunAsync(() =>
        {
                var app = System.Windows.Application.Current!;
                var dicts = app.Resources.MergedDictionaries;
                if (dicts.Count == 0)
                {
                    dicts.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/Colors.xaml", UriKind.Absolute)
                    });
                    dicts.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/ModernControls.xaml", UriKind.Absolute)
                    });
                }

                var settingsView = new MacroSuite.App.Views.SettingsView();
                settingsView.Should().NotBeNull();

                var vault = new InMemorySecretVault();
                var router = new AIVisionRouter(vault);
                var vm = CreateVm(vault, router);
                settingsView.DataContext = vm;
                settingsView.UpdateLayout();
                return Task.CompletedTask;
        });
    }
}

