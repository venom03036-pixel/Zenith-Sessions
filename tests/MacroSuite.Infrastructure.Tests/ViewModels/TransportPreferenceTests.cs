using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using MacroSuite.App.Common;
using MacroSuite.App.ViewModels;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed partial class MiniToolbarHotkeyPreferenceTests
{
    [Fact]
    public void FreshTransportDefaults_AreRequestedKeys_AndLegacyCustomValuesArePreserved()
    {
        var settings = Create();
        new[] { settings.RecordHotkey, settings.StopHotkey, settings.PlayHotkey, settings.PauseHotkey }.Should().Equal("F6", "F8", "F4", "F5");
        settings.RecordHotkey = "F2"; settings.StopHotkey = "Ctrl+F3";
        var reloaded = Create();
        reloaded.RecordHotkey.Should().Be("F2"); reloaded.StopHotkey.Should().Be("Ctrl+F3");
    }

    [Fact]
    public void RejectedOrBusyRemap_KeepsMemoryAndSettingsFileUnchanged()
    {
        var settings = Create(); settings.SaveSettings().Should().BeTrue();
        string before = File.ReadAllText(SettingsPath); int attempts = 0;
        settings.TryApplyHotkeys = _ => { attempts++; return false; };
        settings.RecordHotkey = "Ctrl+F9";
        settings.RecordHotkey.Should().Be("F6"); File.ReadAllText(SettingsPath).Should().Be(before); attempts.Should().Be(1);
        settings.CanChangeHotkeys = () => false;
        settings.RecordHotkey = "Ctrl+F10";
        attempts.Should().Be(1); settings.RecordHotkey.Should().Be("F6"); File.ReadAllText(SettingsPath).Should().Be(before);
    }

    [Fact]
    public void ExplicitReset_ValidatesAllFourKeysOnce_AndPublishesAnAtomicSnapshot()
    {
        var settings = Create(); settings.RecordHotkey = "Ctrl+R"; settings.StopHotkey = "Ctrl+S";
        int attempts = 0; var observed = new List<string>();
        settings.TryApplyHotkeys = proposal => { attempts++; proposal.Should().HaveCount(4); return true; };
        settings.RecordHotkeyChanged += _ => observed.Add(settings.RecordHotkey + "/" + settings.StopHotkey);
        settings.StopHotkeyChanged += _ => observed.Add(settings.RecordHotkey + "/" + settings.StopHotkey);
        settings.ResetTransportDefaults();
        attempts.Should().Be(1); observed.Should().Equal("F6/F8", "F6/F8");
        var reloaded = Create(); new[] { reloaded.RecordHotkey, reloaded.StopHotkey }.Should().Equal("F6", "F8");
    }

    [Fact]
    public void BundledDefaults_FreshSettingsAndReset_AllUseTransportHotkeyDefaults()
    {
        string app = Path.Combine(FindRepositoryRoot(), "src", "MacroSuite.App");
        using (var bundled = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "Defaults", "settings.json"))))
        {
            var root = bundled.RootElement;
            root.GetProperty("recordHotkey").GetString().Should().Be(TransportHotkeyDefaults.Record);
            root.GetProperty("stopHotkey").GetString().Should().Be(TransportHotkeyDefaults.Stop);
            root.GetProperty("playHotkey").GetString().Should().Be(TransportHotkeyDefaults.Play);
            root.GetProperty("pauseHotkey").GetString().Should().Be(TransportHotkeyDefaults.Pause);
            root.GetProperty("panicHotkey").GetString().Should().Be(TransportHotkeyDefaults.Panic);
            root.GetProperty("autoClickerHotkey").GetString().Should().Be(TransportHotkeyDefaults.AutoClicker);
            root.GetProperty("miniToolbarClickThroughHotkey").GetString().Should().Be(TransportHotkeyDefaults.MiniClickThrough);
            root.GetProperty("shortcutDefaultsVersion").GetInt32().Should().Be(TransportHotkeyDefaults.Version);
        }

        var settings = Create();
        new[] { settings.RecordHotkey, settings.StopHotkey, settings.PlayHotkey, settings.PauseHotkey, settings.PanicHotkey }.Should().Equal(
            TransportHotkeyDefaults.Record, TransportHotkeyDefaults.Stop, TransportHotkeyDefaults.Play, TransportHotkeyDefaults.Pause, TransportHotkeyDefaults.Panic);
        settings.AutoClickerHotkey.Should().Be(TransportHotkeyDefaults.AutoClicker);
        settings.MiniToolbarClickThroughHotkey.Should().Be(TransportHotkeyDefaults.MiniClickThrough);

        settings.RecordHotkey = "Ctrl+R"; settings.StopHotkey = "Ctrl+S"; settings.PlayHotkey = "Ctrl+P"; settings.PauseHotkey = "Ctrl+U";
        IReadOnlyDictionary<string, string>? proposal = null;
        settings.TryApplyHotkeys = next => { proposal = next; return true; };
        settings.ResetTransportDefaults();
        proposal.Should().Equal(new Dictionary<string, string>
        {
            ["record"] = TransportHotkeyDefaults.Record, ["stop"] = TransportHotkeyDefaults.Stop,
            ["play"] = TransportHotkeyDefaults.Play, ["pause"] = TransportHotkeyDefaults.Pause,
        });
        new[] { settings.RecordHotkey, settings.StopHotkey, settings.PlayHotkey, settings.PauseHotkey }.Should().Equal(
            TransportHotkeyDefaults.Record, TransportHotkeyDefaults.Stop, TransportHotkeyDefaults.Play, TransportHotkeyDefaults.Pause);
        using (var saved = JsonDocument.Parse(File.ReadAllText(SettingsPath)))
            saved.RootElement.GetProperty("shortcutDefaultsVersion").GetInt32().Should().Be(TransportHotkeyDefaults.Version);

        typeof(MacroEditorViewModel).GetProperty("CommonHotkeyPresets").Should().BeNull(
            "the unused preset list offered the reserved auto-clicker, transport and Panic keys");
    }

    [Fact]
    public void DefaultShortcutLiterals_AreDeclaredOnlyInTransportHotkeyDefaults()
    {
        string app = Path.Combine(FindRepositoryRoot(), "src", "MacroSuite.App");
        var literal = new Regex("\"(F1|F4|F5|F6|F8|F12|Ctrl\\+Alt\\+F10)\"", RegexOptions.CultureInvariant);
        string[] separators = [Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar];
        var copies = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => !separators.Any(part => path.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .Where(path => !string.Equals(Path.GetFileName(path), "TransportHotkeyDefaults.cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(item => literal.IsMatch(item.line))
            .Select(item => $"{Path.GetRelativePath(app, item.path)}:{item.index + 1}: {item.line.Trim()}")
            .ToArray();
        copies.Should().BeEmpty("default shortcut keys must come from TransportHotkeyDefaults so first run, repair, reset and the editor cannot drift");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MacroSuite.slnx"))) directory = directory.Parent;
        directory.Should().NotBeNull();
        return directory!.FullName;
    }
}
