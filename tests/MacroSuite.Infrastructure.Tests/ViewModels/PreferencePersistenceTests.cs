using System.IO;
using System.Text.Json;
using FluentAssertions;
using MacroSuite.App.Common;
using MacroSuite.App.Themes;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Security;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed class PreferencePersistenceTests
{
    private static AppDataPathResolver Paths() => new(Path.Combine(Path.GetTempPath(), "ZenithPreferenceTests", Guid.NewGuid().ToString("N")));
    private static SettingsViewModel Settings(AppDataPathResolver paths)
    {
        var vault = new Vault(); return new(vault, new AIVisionRouter(vault), new Display(), paths, new ImageScanBudget());
    }
    [Fact]
    public async Task ExplicitSave_PersistsThemeLogoAndAutoClickerWithoutCredentialsOrRuntimeState()
    {
        var paths = Paths(); var settings = Settings(paths); using var clicker = new AutoClickerViewModel(new Input(), paths: paths);
        settings.AttachAutoClickerPreferences(clicker);
        settings.LogoColorMode = LogoColorMode.Original; settings.RecordHotkey = "Ctrl+Shift+9";
        settings.Appearance.ThemeName = "Saved palette"; settings.Appearance.SelectedColorHex = "#EEAA22";
        clicker.ClicksPerSecond = 21.5; clicker.SelectedMouseButton = "Right"; clicker.SelectedClickType = "Double";
        clicker.UseFixedPosition = true; clicker.FixedX = -120; clicker.FixedY = 302; clicker.RepeatNTimes = true; clicker.RepeatCount = 7;
        settings.SaveSettings().Should().BeTrue(); settings.SettingsStatus.Should().Be("Settings saved.");
        var reloaded = Settings(paths); using var nextClicker = new AutoClickerViewModel(new Input(), paths: paths);
        reloaded.LogoColorMode.Should().Be(LogoColorMode.Original); reloaded.RecordHotkey.Should().Be("Ctrl+Shift+9");
        reloaded.Appearance.CurrentProfile.Accent.Should().Be("#EEAA22"); nextClicker.ClicksPerSecond.Should().Be(21.5);
        nextClicker.SelectedMouseButton.Should().Be("Right"); nextClicker.SelectedClickType.Should().Be("Double");
        nextClicker.FixedX.Should().Be(-120); nextClicker.FixedY.Should().Be(302); nextClicker.RepeatNTimes.Should().BeTrue(); nextClicker.RepeatCount.Should().Be(7);
        nextClicker.IsRunning.Should().BeFalse(); nextClicker.TotalClicksCompleted.Should().Be(0);
        File.ReadAllText(Path.Combine(paths.ConfigDirectory, "settings.json")).Should().NotContain("apiKeyInput").And.NotContain("secretValue");
        settings.DetachRuntimeSettings(); reloaded.DetachRuntimeSettings(); await settings.Appearance.ShutdownAsync(); await reloaded.Appearance.ShutdownAsync();
    }
    [Fact]
    public async Task SaveFailure_PreservesPreviousSettingsAndDraftAndReportsFailure()
    {
        var paths = Paths(); var settings = Settings(paths); settings.SaveSettings().Should().BeTrue();
        string path = Path.Combine(paths.ConfigDirectory, "settings.json"), before = File.ReadAllText(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings.PlayHotkey = "Ctrl+Alt+L";
            settings.SaveSettings().Should().BeFalse(); settings.SettingsStatus.Should().NotBe("Settings saved.");
        }
        File.ReadAllText(path).Should().Be(before); settings.SaveSettings().Should().BeTrue();
        settings.DetachRuntimeSettings(); await settings.Appearance.ShutdownAsync();
    }
    [Fact]
    public async Task AtomicWrites_SerializeConcurrentWritersAndLeaveCompleteJson()
    {
        var paths = Paths(); string path = Path.Combine(paths.ConfigDirectory, "concurrent.json");
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => SettingsJsonStore.SaveAtomic(path, JsonSerializer.Serialize(new { index = i, value = new string('x', 4000) })))));
        using var document = JsonDocument.Parse(File.ReadAllText(path)); document.RootElement.GetProperty("value").GetString()!.Length.Should().Be(4000);
        Directory.GetFiles(paths.ConfigDirectory, "*.tmp").Should().BeEmpty();
    }
    [Fact]
    public void Bootstrap_CopiesKnownDefaultsOnceAndNeverOverwritesExistingPreferences()
    {
        var paths = Paths(); string defaults = Path.Combine(paths.RootDirectory, "Defaults"); Directory.CreateDirectory(defaults);
        File.WriteAllText(Path.Combine(defaults, "settings.json"), "{\"recordHotkey\":\"F8\"}");
        File.WriteAllText(Path.Combine(defaults, "credentials.json"), "{\"secret\":\"never-copy\"}");
        SettingsBootstrapDefaults.Ensure(paths.ConfigDirectory, defaults);
        string target = Path.Combine(paths.ConfigDirectory, "settings.json"); File.WriteAllText(target, "{\"recordHotkey\":\"Ctrl+9\"}");
        SettingsBootstrapDefaults.Ensure(paths.ConfigDirectory, defaults);
        File.ReadAllText(target).Should().Contain("Ctrl+9"); File.Exists(Path.Combine(paths.ConfigDirectory, "credentials.json")).Should().BeFalse();
    }
    [Fact]
    public async Task UnlicensedAutoClicker_DoesNotSendAnyInput()
    {
        var input = new Input(); using var clicker = new AutoClickerViewModel(input, featureGate: new Denied());
        clicker.ToggleRun(); await clicker.RunCompletion;
        input.Clicks.Should().Be(0); clicker.IsRunning.Should().BeFalse(); clicker.PreferencesStatus.Should().Be("Activate Zenith before starting.");
    }
    [Fact]
    public async Task FirstLaunchBootstrap_DoesNotHideRecoveryForMissingPrimarySettings()
    {
        var paths = Paths(); var first = Settings(paths); first.PlayHotkey = "Ctrl+Alt+8"; first.PlayHotkey = "Ctrl+Alt+9";
        first.DetachRuntimeSettings(); await first.Appearance.ShutdownAsync();
        File.Delete(Path.Combine(paths.ConfigDirectory, "settings.json"));
        var restored = Settings(paths);
        restored.PlayHotkey.Should().Be("Ctrl+Alt+8"); restored.PersistenceStatus.Should().Contain("Recovered");
        restored.DetachRuntimeSettings(); await restored.Appearance.ShutdownAsync();
    }
    [Fact]
    public async Task AppearanceAndAutoClicker_RestoreTheirLastValidBackupsWithVisibleRecovery()
    {
        var paths = Paths(); var appearance = new AppearanceViewModel(paths.RootDirectory, ThemeCatalog.DefaultId);
        appearance.ThemeName = "Previous"; appearance.SaveThemeCommand.Execute(null); appearance.ThemeName = "Latest"; appearance.SaveThemeCommand.Execute(null);
        await appearance.WaitForBackgroundAsync(); File.WriteAllText(Path.Combine(paths.ConfigDirectory, "appearance.json"), "corrupt");
        var restored = new AppearanceViewModel(paths.RootDirectory, ThemeCatalog.DefaultId);
        restored.ThemeName.Should().Be("Previous"); restored.Status.Should().Contain("Recovered");
        using var clicker = new AutoClickerViewModel(new Input(), paths: paths); clicker.ClicksPerSecond = 12; clicker.ClicksPerSecond = 13;
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "autoclicker.json"), "corrupt"); using var next = new AutoClickerViewModel(new Input(), paths: paths);
        next.ClicksPerSecond.Should().Be(12); next.PreferencesStatus.Should().Contain("Recovered");
        await appearance.ShutdownAsync(); await restored.ShutdownAsync();
    }
    private sealed class Denied : IFeatureGate { public bool IsAvailable(FeatureId feature) => false; public void Require(FeatureId feature) => throw new FeatureNotLicensedException(feature, "Activate Zenith before starting."); }
    [Fact]
    public async Task ActivationIsRefreshedBeforeInput_AndAnExpiredCachedLicenseCannotStart()
    {
        var input = new Input(); var activation = new Activation(); using var clicker = new AutoClickerViewModel(input, activationService: activation);
        clicker.CanStart.Should().BeTrue(); clicker.ToggleRun(); await clicker.RunCompletion;
        activation.RefreshCount.Should().Be(1); input.Clicks.Should().Be(0); clicker.IsRunning.Should().BeFalse();
        clicker.PreferencesStatus.Should().Be("Activation expired.");
    }
    [Fact]
    public async Task PanicDuringActivationValidationCancelsBeforeAnyInput()
    {
        var input = new Input(); var activation = new Activation { WaitForCancellation = true }; using var clicker = new AutoClickerViewModel(input, activationService: activation);
        clicker.ToggleRun(); clicker.IsStarting.Should().BeTrue(); clicker.StopClicker(); await clicker.RunCompletion;
        input.Clicks.Should().Be(0); clicker.IsStarting.Should().BeFalse();
    }
    private sealed class Activation : IActivationService
    {
        public bool IsActive { get; private set; } = true;
        public string StatusMessage => "Activation expired.";
        public VerifiedLicense? CurrentLicense => null;
        public event EventHandler? StateChanged;
        public int RefreshCount; public bool WaitForCancellation;
        public async Task<ActivationOperationResult> RefreshAsync(CancellationToken ct = default)
        {
            RefreshCount++;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
            IsActive = false; StateChanged?.Invoke(this, EventArgs.Empty); return new(false, StatusMessage);
        }
        public Task<ActivationRequestResult> CreateRequestAsync(bool allowSoftwareFallback = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ActivationOperationResult> ImportAsync(string licenseText, CancellationToken ct = default) => throw new NotSupportedException();
        public void RequireActive() { if (!IsActive) throw new ActivationRequiredException(StatusMessage); }
    }
    private sealed class Display : IDisplayInfoService { public int GetMaxMonitorRefreshRate() => 165; public int GetPrimaryMonitorRefreshRate() => 165; }
    private sealed class Vault : ISecretVault
    {
        public void SetSecret(string keyName, string secretValue) { } public string? GetSecret(string keyName) => null; public bool HasSecret(string keyName) => false;
        public bool RemoveSecret(string keyName) => false; public IReadOnlyList<string> ListSecretKeys() => [];
    }
    private sealed class Input : IInputPlayer
    {
        public int Clicks; public bool IsPlaying => false;
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default) => Task.CompletedTask;
        public void ReleaseAllHeldKeys() { } public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) => Clicks++;
        public void SendRelativeMove(int dx, int dy) { } public void Dispose() { }
    }
}
