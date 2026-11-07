using FluentAssertions;
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
}
