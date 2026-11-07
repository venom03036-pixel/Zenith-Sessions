using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

// The global panic shortcut: F12 unless Settings saves another allowed combination. The window lends a registrar bound to its
// own handle; the shell asks it to register the saved shortcut once settings have loaded (so a different saved choice never
// takes F12 first) and again after each Save settings. The registrar takes a new combination before it releases the old one,
// and a combination Windows refuses leaves the working one registered, so Panic never loses its global key (Rule 11). The
// focused F12 binding and the Panic button never depend on this. Nothing here runs in the background.
public sealed partial class ShellViewModel
{
    private Func<PanicShortcut, bool>? _registerPanicShortcut;
    private bool _panicShortcutKnown, _panicShortcutTried;
    private PanicShortcut? _activePanicShortcut;
    private string _panicShortcutText = PanicShortcut.Default.ToString();

    /// <summary>Settings draft for the global panic shortcut, such as F12 or Ctrl+Alt+P. Applies after Save settings.</summary>
    public string PanicShortcutText { get => _panicShortcutText; set { if (Set(ref _panicShortcutText, value ?? "")) PreferencesEdited(); } }
    public string PanicLabel => _activePanicShortcut is { } active ? "Panic stop · " + active : "Panic stop";
    /// <summary>The header key cap: the registered global shortcut, otherwise F12, which works while this window is focused.</summary>
    public string PanicKeyCap => (_activePanicShortcut ?? PanicShortcut.Default).ToString();
    public string PanicShortcutStatus
    {
        get
        {
            if (IsSample) return "Isolated sample previews do not register global shortcuts. F12 works while this preview is focused; Panic stop is also available.";
            var saved = SavedPanicShortcut;
            if (!_panicShortcutTried) return $"{saved} is not registered globally yet. The Panic button and F12 while this window is focused are available.";
            if (_activePanicShortcut == saved)
                return saved == PanicShortcut.Default ? "F12 is registered globally for this manager." : $"{saved} is registered globally for this manager. F12 also works while this window is focused.";
            if (_activePanicShortcut is { } kept)
                return $"{saved} is unavailable as a global shortcut, usually because another program or Zenith already uses it. {kept} stays registered globally. Choose another shortcut, or close that program and save settings again.";
            return $"Global {saved} is unavailable, usually because Zenith or another program already uses it. The Panic button and focused F12 remain available; existing shortcuts are preserved.";
        }
    }
    private PanicShortcut SavedPanicShortcut => PanicShortcut.TryParse(_savedSettings.PanicShortcut, out var saved, out _) ? saved : PanicShortcut.Default;
    private bool PanicShortcutDraftIsSaved => PanicShortcut.TryParse(PanicShortcutText, out var draft, out _) && draft == SavedPanicShortcut;

    /// <summary>
    /// UI thread, once the window's handle exists. <paramref name="register"/> registers a shortcut for that window on every
    /// desktop program and returns false when Windows refuses it. Isolated sample previews never take a desktop shortcut.
    /// </summary>
    internal void AttachGlobalPanicShortcut(Func<PanicShortcut, bool> register)
    {
        ArgumentNullException.ThrowIfNull(register);
        if (IsSample) return;
        if (_registerPanicShortcut is not null) throw new InvalidOperationException("The panic shortcut registrar is already attached.");
        _registerPanicShortcut = register;
        if (_panicShortcutKnown) ApplySavedPanicShortcut();
    }

    // UI thread, after settings load (or fail to load, which leaves F12) and after Save settings. True when the saved shortcut
    // is registered or there is nothing to register with; false when Windows refused it.
    private bool ApplySavedPanicShortcut()
    {
        _panicShortcutKnown = true;
        if (_registerPanicShortcut is not { } register || _shuttingDown) return true;
        var wanted = SavedPanicShortcut;
        if (wanted != _activePanicShortcut && register(wanted)) _activePanicShortcut = wanted;
        _panicShortcutTried = true;
        Raise(nameof(PanicLabel)); Raise(nameof(PanicKeyCap)); Raise(nameof(PanicShortcutStatus));
        return _activePanicShortcut == wanted;
    }

    // After Save settings, show the saved text in its canonical form unless the field was edited meanwhile.
    private void ShowSavedPanicShortcut(PanicShortcut saved)
    {
        if (PanicShortcut.TryParse(PanicShortcutText, out var draft, out _) && draft == saved && PanicShortcutText != saved.ToString())
        { _panicShortcutText = saved.ToString(); Raise(nameof(PanicShortcutText)); }
    }
}
