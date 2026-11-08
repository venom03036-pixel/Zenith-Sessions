using System.Globalization;
using System.Resources;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Zenith.Sessions.App;

// Localization infrastructure (R-shell-16). The shell's texts live in Localization/ShellStrings.resx (English, the neutral and
// only shipped language). A translation is a ShellStrings.<culture>.resx beside it, which the SDK builds into a satellite assembly;
// nothing else changes. The window binds every migrated text through {local:Text Key}, a markup extension that reads the
// ShellStrings the window hands down with the inherited attached property Localization.Strings, so a language change re-resolves
// every text at once, the way a theme change does, without rebuilding the window. Elements without a window (templates loaded on
// their own, the packaged check's fragments) read English. Two pseudo-locales exist for testing only: they show every catalog text
// marked, so a text that stays plain is one that has not been migrated, and the right-to-left one mirrors the window as a
// right-to-left language would. Lookups are hash-table reads on change only; no thread, timer or network (Rules 14, 18).

/// <summary>
/// The language the shell shows. <see cref="Culture"/> is the culture the catalog is read with (strings fall back to English until a
/// translation for it ships); <see cref="Pseudo"/> marks the catalog text for testing; <see cref="RightToLeft"/> mirrors the window.
/// </summary>
public sealed record ShellLanguage(string Id, CultureInfo Culture, bool Pseudo, bool RightToLeft)
{
    /// <summary>The language tag the window reports to text services (hyphenation, font fallback): the Windows pseudo-locale tags for the pseudo choices.</summary>
    public string Tag => Pseudo ? (RightToLeft ? "qps-plocm" : "qps-ploc") : Culture.IetfLanguageTag.Length == 0 ? ShellLanguages.EnglishId : Culture.IetfLanguageTag;
}

/// <summary>
/// The Settings language choices: English (the default and today's behaviour), Follow Windows (the Windows display language,
/// mirrored for a right-to-left language), and the two pseudo-locales for testing. An unknown saved id is read as English.
/// </summary>
internal static class ShellLanguages
{
    public const string EnglishId = "en", FollowWindowsId = "follow-windows", PseudoId = "pseudo", PseudoRightToLeftId = "pseudo-rtl";
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    public static IReadOnlyList<string> ChoiceIds { get; } = Array.AsReadOnly([EnglishId, FollowWindowsId, PseudoId, PseudoRightToLeftId]);

    /// <summary>The id to save for <paramref name="id"/>: a known choice, or English for anything else (an edited file, an older or newer id).</summary>
    public static string Normalize(string? id) =>
        ChoiceIds.FirstOrDefault(choice => string.Equals(choice, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? EnglishId;
    /// <summary>The catalog key of the name Settings shows for the choice <paramref name="id"/>.</summary>
    public static string NameKey(string id) => Normalize(id) switch
    {
        FollowWindowsId => "Language_FollowWindows", PseudoId => "Language_Pseudo", PseudoRightToLeftId => "Language_PseudoRightToLeft", _ => "Language_English",
    };
    /// <summary>The language to show for the saved choice <paramref name="id"/>, given the Windows display language.</summary>
    public static ShellLanguage Resolve(string? id, CultureInfo windowsDisplayLanguage)
    {
        ArgumentNullException.ThrowIfNull(windowsDisplayLanguage);
        return Normalize(id) switch
        {
            FollowWindowsId => new(FollowWindowsId, windowsDisplayLanguage, false, windowsDisplayLanguage.TextInfo.IsRightToLeft),
            PseudoId => new(PseudoId, English, true, false),
            PseudoRightToLeftId => new(PseudoRightToLeftId, English, true, true),
            _ => new(EnglishId, English, false, false),
        };
    }
}

/// <summary>
/// The catalog read for one <see cref="ShellLanguage"/>. Immutable: the shell replaces its instance when the language changes, and
/// every bound text follows. A key the catalog lacks shows as the key in brackets, so it is seen rather than blank.
/// </summary>
public sealed class ShellStrings
{
    private static readonly ResourceManager Catalog = new("Zenith.Sessions.App.Localization.ShellStrings", typeof(ShellStrings).Assembly);
    public static ShellStrings English { get; } = new(ShellLanguages.Resolve(ShellLanguages.EnglishId, ShellLanguages.English));
    public ShellLanguage Language { get; }
    public ShellStrings(ShellLanguage language) { ArgumentNullException.ThrowIfNull(language); Language = language; }
    public FlowDirection FlowDirection => Language.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public XmlLanguage XmlLanguage => XmlLanguage.GetLanguage(Language.Tag);
    /// <summary>The text for <paramref name="key"/> in this language, or the key in brackets when the catalog lacks it.</summary>
    public string this[string key] => Get(key, "[" + key + "]");
    /// <summary>The text for <paramref name="key"/>, or <paramref name="fallback"/> when the catalog lacks it.</summary>
    public string Get(string key, string fallback)
    {
        ArgumentNullException.ThrowIfNull(key);
        string? text = Catalog.GetString(key, Language.Culture);
        if (text is null) return fallback;
        return Language.Pseudo ? PseudoLocalizer.Localize(text) : text;
    }
    /// <summary>The composite text for <paramref name="key"/> with <paramref name="arguments"/> formatted in this language.</summary>
    public string Format(string key, params object?[] arguments) => string.Format(Language.Culture, this[key], arguments);
    /// <summary>Tests: every key of the English catalog, in order.</summary>
    internal static IReadOnlyList<string> Keys()
    {
        var set = Catalog.GetResourceSet(CultureInfo.InvariantCulture, true, true) ?? throw new MissingManifestResourceException("The shell string catalog is not embedded.");
        var keys = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in set) keys.Add((string)entry.Key);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}

/// <summary>
/// The pseudo-locale transform for testing: the text in brackets, ASCII letters replaced by accented look-alikes (still readable),
/// about a third longer (translations usually are), with every {n} placeholder kept as it is so composite formats still work.
/// </summary>
internal static class PseudoLocalizer
{
    // a..z and A..Z as accented Latin letters, each one UTF-16 code unit, written as code points so the file stays ASCII.
    private static readonly string Lower = new(
    [
        (char)0x00E0, (char)0x0180, (char)0x00E7, (char)0x010F, (char)0x00E9, (char)0x0192, (char)0x011F, (char)0x0125, (char)0x012F, (char)0x0135, (char)0x0137, (char)0x013E, (char)0x0271,
        (char)0x00F1, (char)0x00F6, (char)0x00FE, (char)0x024B, (char)0x0159, (char)0x0161, (char)0x0165, (char)0x00FB, (char)0x1E7D, (char)0x0175, (char)0x1E8B, (char)0x00FF, (char)0x017E,
    ]);
    private static readonly string Upper = new(
    [
        (char)0x00C0, (char)0x0181, (char)0x00C7, (char)0x010E, (char)0x00C9, (char)0x0191, (char)0x011E, (char)0x0124, (char)0x012E, (char)0x0134, (char)0x0136, (char)0x013D, (char)0x1E40,
        (char)0x00D1, (char)0x00D6, (char)0x00DE, (char)0x024A, (char)0x0158, (char)0x0160, (char)0x0164, (char)0x00DB, (char)0x1E7C, (char)0x0174, (char)0x1E8A, (char)0x0178, (char)0x017D,
    ]);

    public static string Localize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return text;
        var result = new StringBuilder(text.Length * 3 / 2 + 2).Append('[');
        int letters = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                // A {0} or {0:format} placeholder is copied as it is, so the pseudo text formats like the English one.
                int close = text.IndexOf('}', i);
                if (close > i) { result.Append(text, i, close - i + 1); i = close; continue; }
            }
            if (char.IsAsciiLetterLower(c)) { result.Append(Lower[c - 'a']); letters++; }
            else if (char.IsAsciiLetterUpper(c)) { result.Append(Upper[c - 'A']); letters++; }
            else result.Append(c);
        }
        return result.Append('~', (letters + 2) / 3).Append(']').ToString();
    }
}

/// <summary>
/// <c>local:Localization.Strings</c>: the catalog inherited by elements within a window, including templates.
/// Disconnected popup trees must bind it from their placement target. <c>{local:Text Key}</c> binds to it on the element itself.
/// </summary>
public static class Localization
{
    public static readonly DependencyProperty StringsProperty = DependencyProperty.RegisterAttached("Strings", typeof(ShellStrings), typeof(Localization),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
    public static ShellStrings? GetStrings(DependencyObject element) { ArgumentNullException.ThrowIfNull(element); return (ShellStrings?)element.GetValue(StringsProperty); }
    public static void SetStrings(DependencyObject element, ShellStrings? value) { ArgumentNullException.ThrowIfNull(element); element.SetValue(StringsProperty, value); }
}

/// <summary>
/// <c>{local:Text Key}</c>: the catalog text for <see cref="Key"/> in the language of the window the element is in, re-resolved
/// whenever that language changes; English where no window supplies one. A one-way binding on the element's own inherited
/// <see cref="Localization.StringsProperty"/>, so it works on any dependency property, including AutomationProperties.Name and ToolTip.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension(string key) { ArgumentNullException.ThrowIfNull(key); Key = key; }
    [ConstructorArgument("key")] public string Key { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding
        {
            Path = new PropertyPath("(0)", Localization.StringsProperty), RelativeSource = RelativeSource.Self, Mode = BindingMode.OneWay, Converter = new Lookup(Key),
        };
        return binding.ProvideValue(serviceProvider);
    }
    private sealed class Lookup(string key) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as ShellStrings ?? ShellStrings.English)[key];
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

/// <summary>One row of the Settings language list; its name follows the current language.</summary>
public sealed class LanguageChoice : Observable
{
    private readonly Func<ShellStrings> _strings;
    internal LanguageChoice(string id, Func<ShellStrings> strings) { Id = id; _strings = strings; }
    public string Id { get; }
    public string Name => _strings()[ShellLanguages.NameKey(Id)];
    internal void Refresh() => Raise(nameof(Name));
}
