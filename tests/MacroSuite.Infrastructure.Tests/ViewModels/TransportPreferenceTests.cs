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
        string[] separators = [Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar];
        var copies = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => !separators.Any(part => path.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .Where(path => !string.Equals(Path.GetFileName(path), "TransportHotkeyDefaults.cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => FindDefaultShortcutCopies(File.ReadAllText(path))
                .Select(copy => $"{Path.GetRelativePath(app, path)}:{copy.Line}: {copy.Text}"))
            .ToArray();
        copies.Should().BeEmpty("default shortcut keys must come from TransportHotkeyDefaults so first run, repair, reset and the editor cannot drift");
    }

    // %Name% stands for the value of TransportHotkeyDefaults.Name, so these cases follow the defaults if they change.
    [Theory]
    [InlineData(1, "private string _recordHotkey = \"%Record%\";")]
    [InlineData(1, "private string _startHotkey = \"%AutoClicker%\";")]
    [InlineData(1, "public string PanicHotkey { get; set; } = \"%Panic%\";")]
    [InlineData(1, "public string DefaultPanicHotkey => \"%Panic%\";")]
    [InlineData(1, "_recordHotkey = \"%Record%\"; _stopHotkey = \"%Stop%\";")]
    [InlineData(1, "_miniToolbarClickThroughHotkey = \"%MiniClickThrough%\";")]
    [InlineData(1, "_pauseHotkey = string.IsNullOrWhiteSpace(saved) ? \"%Pause%\" : saved;")]
    [InlineData(1, "string rec = NormalizeBind(_recordHotkey, \"%Record%\");")]
    [InlineData(1, "if (root.TryGetProperty(\"playHotkey\", out var ph)) _playHotkey = ph.GetString() ?? \"%Play%\";")]
    [InlineData(1, "string bind = saved ?? \"%AutoClicker%\";")]
    [InlineData(1, "var defaults = new Dictionary<string, string> { [\"record\"] = \"%Record%\", [\"stop\"] = \"%Stop%\" };")]
    [InlineData(3, "var defaults = new Dictionary<string, string>", "{", "    [\"play\"] = \"%Play%\",", "};")]
    [InlineData(1, "var defaults = new Dictionary<string, string> { { \"pause\", \"%Pause%\" } };")]
    [InlineData(1, "string[] candidates = [\"%Panic%\", \"Ctrl+Shift+F12\", \"Alt+Shift+F11\"];")]
    [InlineData(3, "public List<string> CommonHotkeyPresets { get; } = new()", "{", "    \"%AutoClicker%\", \"F7\", \"Ctrl+1\", \"Space\"", "};")]
    [InlineData(1, "if (_panicHotkey == \"%Panic%\") ShowDefaultHint();")]
    [InlineData(1, "Register(hotkey: \"%Record%\");")]
    [InlineData(1, "void Register(string hotkey = \"%Record%\") { }")]
    [InlineData(1, "[DefaultValue(\"%Stop%\")] public string StopBind { get; set; } = string.Empty;")]
    [InlineData(1, "private const string DefaultRecordKey = \"%Record%\";")]
    [InlineData(1, "private string _recordKey = \"%Record%\";")]
    [InlineData(1, "if (_panicKey == \"%Panic%\") ShowDefaultHint();")]
    [InlineData(1, "string DefaultRecordHotkey() => \"%Record%\";")]
    [InlineData(3, "string DefaultPanicKey()", "{", "    return \"%Panic%\";", "}")]
    [InlineData(1, "Register(\"record\", \"%Record%\");")]
    [InlineData(1, "case \"stop\": return \"%Stop%\";")]
    [InlineData(1, "string key = action switch { \"record\" => \"%Record%\", _ => \"\" };")]
    public void DefaultShortcutGuard_ReportsEveryRoleThatHoldsADefault(int expectedLine, params string[] lines)
    {
        string source = ExpandDefaults(string.Join(Environment.NewLine, lines));
        FindDefaultShortcutCopies(source).Select(copy => copy.Line).Should().Contain(expectedLine,
            "a copy of a shipped default in this role lets first run, repair, reset or the editor drift from TransportHotkeyDefaults: {0}", source);
    }

    [Theory]
    [InlineData("private static readonly Dictionary<string, int> FunctionKeys = new() { [\"%AutoClicker%\"] = 0x70, [\"%Panic%\"] = 0x7B };")]
    [InlineData("private static readonly Dictionary<string, int> FunctionKeys = new()", "{", "    { \"%Play%\", 0x73 },", "    { \"%Pause%\", 0x74 },", "};")]
    [InlineData("string[] functionKeys = [\"%AutoClicker%\", \"F2\", \"F3\", \"%Play%\", \"%Pause%\", \"%Record%\", \"F7\", \"%Stop%\", \"F9\", \"F10\", \"F11\", \"%Panic%\"];")]
    [InlineData("case \"%Record%\": return 0x75;")]
    [InlineData("string name = vk switch { 0x70 => \"%AutoClicker%\", 0x7B => \"%Panic%\", _ => \"?\" };")]
    [InlineData("HelpKeyLabel.Text = \"%AutoClicker%\";")]
    [InlineData("StatusText = string.Format(\"Press {0} for help.\", \"%AutoClicker%\");")]
    [InlineData("/// <summary>Shown when the user presses \"%Panic%\" in the key tester.</summary>")]
    [InlineData("// _panicHotkey = \"%Panic%\" was the old hard-coded value.")]
    [InlineData("internal void SimulateKeyForTests(string key = \"%Record%\") => _keys.Press(key);")]
    [InlineData("_input.SendKeyForTests(\"%MiniClickThrough%\");")]
    [InlineData("string canonical = typed switch { \"f12\" => \"%Panic%\", \"esc\" => \"Escape\", _ => typed };")]
    [InlineData("case \"f6\": return \"%Record%\";")]
    [InlineData("_aliases.Add(\"f1\", \"%AutoClicker%\");")]
    public void DefaultShortcutGuard_IgnoresUnrelatedUsesOfTheSameKeyNames(params string[] lines)
    {
        string source = ExpandDefaults(string.Join(Environment.NewLine, lines));
        FindDefaultShortcutCopies(source).Should().BeEmpty(
            "a key-name table, help text, a comment or a test hook names a key without declaring a default: {0}", source);
    }

    /// <summary>
    /// Reports each line where one C# source writes a shipped default shortcut as a quoted literal in a
    /// role that holds a default. The same key names used for anything else (a key-name table, a switch
    /// over keys, help text, comments or a test hook) are not reported, so the guard fails only where
    /// first run, repair, reset or the editor could drift from <see cref="TransportHotkeyDefaults"/>.
    /// </summary>
    private static IReadOnlyList<(int Line, string Text)> FindDefaultShortcutCopies(string source)
    {
        // Whole-line comments are blanked, keeping line numbers, so notes and documentation never count.
        string code = Regex.Replace(source, @"^[ \t]*//.*$", string.Empty, RegexOptions.Multiline);
        string literal = "\"(?:" + string.Join("|", typeof(TransportHotkeyDefaults).GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => Regex.Escape((string)field.GetRawConstantValue()!))) + ")\"";
        // A field, property, local, parameter or method whose name says it holds a shortcut.
        const string shortcutName = @"\b\w*(?:[Hh]otkeys?|[Ss]hortcuts?|[Bb]inds?|[Dd]efaults?)";
        // A qualified name ending in Key or Keys, as in _recordKey or DefaultPanicKey. A bare key or keys is the key being
        // handled (a test hook), not a shortcut. Such a name holds one key, so its value is read only up to a [ or {:
        // a key-name table such as FunctionKeys = new() { ... } or functionKeys = [ ... ] is not a default.
        const string keyName = @"\b\w+Keys?";
        const string accessors = @"\s*(?:\{[^{}]*\}\s*)?";
        const string anyValue = @"(?<value>[^;]*)";
        const string oneValue = @"(?<value>[^;\[{]*)";
        // A method's result: its expression body, or the first return of its block body.
        const string returns = @"\s*\([^()]*\)\s*(?:=>|\{[^{}]*?\breturn\b)";
        // A quoted action name such as "record". A key name such as "F6" or "f12" is a key-name table's label instead.
        const string action = @"""(?![Ff]\d{1,2}"")[A-Za-z_][\w.-]*""";
        const string entry = @"(?:""[^""\r\n]*""|[\w.]+)";
        Regex[] roles =
        [
            // The value given to or returned for a shortcut-named target, including ?: and ?? fallbacks and multi-line initializers.
            new($@"{shortcutName}{accessors}(?:=>|=(?![=>])){anyValue}"),
            new($@"{keyName}{accessors}(?:=>|=(?![=>])){oneValue}"),
            // The value a shortcut-named method returns, as in DefaultRecordHotkey() => "F6" or { return "F6"; }.
            new($@"{shortcutName}{returns}{anyValue}"),
            new($@"{keyName}{returns}{oneValue}"),
            // Compared with, or passed by name as, a shortcut-named value.
            new($@"(?:{shortcutName}|{keyName}){accessors}(?:[!=]=|:(?!:))\s*(?<value>{literal})"),
            // The value a switch or a call pairs with an action name: "record" => "F6", case "record": return "F6"
            // and Register("record", "F6").
            new($@"{action}\s*=>\s*(?<value>{literal})"),
            new($@"\bcase\s+{action}\s*:\s*return\b{anyValue}"),
            new($@"\b\w+\s*\(\s*{action}\s*,\s*(?<value>{literal})"),
            // The repair fallback given to NormalizeBind, any ?? fallback, and a [DefaultValue].
            new($@"\bNormalizeBind\s*\((?<value>[^;]*)"),
            new($@"\?\?=?\s*(?<value>{literal})"),
            new($@"\bDefaultValue\s*\(\s*(?<value>{literal})"),
            // A dictionary entry's value, as in the reset map: ["record"] = "F6" or { "record", "F6" }.
            new($@"\[\s*""[^""\r\n]*""\s*\]\s*=\s*(?<value>{literal})"),
            new($@"\{{\s*""[^""\r\n]*""\s*,\s*(?<value>{literal})\s*\}}"),
            // A list of binds such as Panic's repair candidates or a preset list: only strings and names, at
            // least one of them a modifier combination. A plain list of key names is not a list of binds.
            new($@"(?<value>[\[{{](?=[^\[\]{{}}]*""(?:Ctrl|Alt|Shift|Win)\+)\s*{entry}(?:\s*,\s*{entry})*\s*,?\s*[\]}}])"),
        ];
        var copy = new Regex(literal, RegexOptions.CultureInvariant);
        var lines = new SortedSet<int>();
        foreach (var role in roles)
            foreach (Match match in role.Matches(code))
            {
                Group value = match.Groups["value"];
                foreach (Match found in copy.Matches(value.Value))
                    lines.Add(code.AsSpan(0, value.Index + found.Index).Count('\n') + 1);
            }
        string[] text = source.Split('\n');
        return lines.Select(line => (line, text[line - 1].Trim())).ToArray();
    }

    private static string ExpandDefaults(string source)
    {
        foreach (var field in typeof(TransportHotkeyDefaults).GetFields().Where(field => field.IsLiteral && field.FieldType == typeof(string)))
            source = source.Replace("%" + field.Name + "%", (string)field.GetRawConstantValue()!, StringComparison.Ordinal);
        source.Should().NotContain("%", "every placeholder must name a TransportHotkeyDefaults constant");
        return source;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MacroSuite.slnx"))) directory = directory.Parent;
        directory.Should().NotBeNull();
        return directory!.FullName;
    }
}
