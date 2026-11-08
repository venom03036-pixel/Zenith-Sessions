using System.Globalization;

namespace Zenith.Sessions.App;

// Language (R-shell-16): a saved schema-1 choice, English unless chosen otherwise, so an older settings file reads exactly as it
// did. Like a theme it applies at once and is saved at once (never a draft), and Save settings keeps it. The shell owns one
// immutable ShellStrings for the current language and replaces it on a change; the window and every {local:Text} binding follow,
// PageName and the window title are re-read, and the theme status sentence is composed again. Follow Windows reads the display
// language once, when the shell is created: Windows needs a sign-out to change it, so nothing watches for it (Rule 14).
public sealed partial class ShellViewModel
{
    private CultureInfo _windowsDisplayLanguage = CultureInfo.CurrentUICulture;
    private ShellStrings _strings = ShellStrings.English;
    private bool _languageApplied;
    /// <summary>The catalog for the current language; the window hands it to every element (local:Localization.Strings).</summary>
    public ShellStrings Strings { get => _strings; private set => Set(ref _strings, value); }
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }
    public AsyncCommand ApplyLanguage { get; }
    /// <summary>The saved language choice id (see ShellLanguages). Setting it applies the language at once and saves it, like a theme.</summary>
    public string Language
    {
        get => ShellLanguages.Normalize(_settings.Language);
        set
        {
            // A list rebuild can write null back; before settings load the choice is not yet known, so the control snaps back.
            if (value is null) return;
            string id = ShellLanguages.Normalize(value);
            if (!_ready || id == Language) { Raise(); return; }
            ApplyLanguage.Execute(id);
        }
    }
    /// <summary>One sentence under the language choice: the language in use and, for Follow Windows, why.</summary>
    public string LanguageStatus
    {
        get
        {
            var language = Strings.Language;
            if (language.Pseudo) return Strings[language.RightToLeft ? "Language_StatusPseudoRightToLeft" : "Language_StatusPseudo"];
            if (language.Id != ShellLanguages.FollowWindowsId) return Strings.Format("Language_StatusCurrent", Strings["Language_English"]);
            return string.Equals(language.Culture.TwoLetterISOLanguageName, ShellLanguages.EnglishId, StringComparison.OrdinalIgnoreCase)
                ? Strings.Format("Language_StatusFollowing", Strings["Language_English"])
                : Strings.Format("Language_StatusFollowingUntranslated", language.Culture.EnglishName);
        }
    }

    private IReadOnlyList<LanguageChoice> CreateLanguageChoices() => Array.AsReadOnly(ShellLanguages.ChoiceIds.Select(id => new LanguageChoice(id, () => Strings)).ToArray());

    /// <summary>UI thread (tests): the Windows display language Follow Windows uses. Re-applies once settings have loaded.</summary>
    internal void SetWindowsDisplayLanguage(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (culture.Equals(_windowsDisplayLanguage)) return;
        _windowsDisplayLanguage = culture;
        if (_languageApplied) ApplyEffectiveLanguage();
    }

    // UI thread, after settings load and after each language choice: one new catalog view, then everything that reads it.
    private void ApplyEffectiveLanguage()
    {
        _languageApplied = true;
        Strings = new ShellStrings(ShellLanguages.Resolve(_settings.Language, _windowsDisplayLanguage));
        foreach (var choice in LanguageChoices) choice.Refresh();
        foreach (string name in new[] { nameof(Language), nameof(LanguageStatus), nameof(PageName), nameof(WindowTitle), nameof(ThemeStatus) }) Raise(name);
    }
}
