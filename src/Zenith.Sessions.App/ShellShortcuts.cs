using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Zenith.Sessions.App;

/// <summary>What a manager shortcut does. Each one runs the command its sidebar button or page control already uses, so the
/// same lock and shutdown guards apply.</summary>
public enum ShellShortcutAction { Panic, Navigate, ShowWindowsOverview, FindOnPage }

/// <summary>One keyboard shortcut of the manager window. <see cref="Page"/> is the Navigate tab; <see cref="Gesture"/> is the
/// text the Guide shows for the key.</summary>
public sealed record ShellShortcut(Key Key, ModifierKeys Modifiers, ShellShortcutAction Action, string? Page, string Label)
{
    public string Gesture { get; } = ShellShortcuts.Format(Key, Modifiers);
}

/// <summary>
/// The manager window's keyboard shortcuts. MainWindow builds its key bindings from this table and the Guide lists it, so the
/// two cannot drift apart. F12 Panic comes first (Rule 11); Ctrl+1 to Ctrl+8 follow the sidebar order. These keys belong to
/// this window only: none of them is registered with Windows. The global panic shortcut is a separate setting.
/// </summary>
public static class ShellShortcuts
{
    public static IReadOnlyList<ShellShortcut> All { get; } = new ReadOnlyCollection<ShellShortcut>(
    [
        new(Key.F12, ModifierKeys.None, ShellShortcutAction.Panic, null, "Panic stop. Works in this window even if you chose a different global shortcut."),
        new(Key.D1, ModifierKeys.Control, ShellShortcutAction.Navigate, "Overview", "Overview"),
        new(Key.D2, ModifierKeys.Control, ShellShortcutAction.Navigate, "Accounts", "Accounts"),
        new(Key.D3, ModifierKeys.Control, ShellShortcutAction.Navigate, "Sessions", "Sessions"),
        new(Key.D4, ModifierKeys.Control, ShellShortcutAction.ShowWindowsOverview, null, "Windows"),
        new(Key.D5, ModifierKeys.Control, ShellShortcutAction.Navigate, "Automation", "Automation"),
        new(Key.D6, ModifierKeys.Control, ShellShortcutAction.Navigate, "Diagnostics", "Diagnostics"),
        new(Key.D7, ModifierKeys.Control, ShellShortcutAction.Navigate, "Settings", "Settings"),
        new(Key.D8, ModifierKeys.Control, ShellShortcutAction.Navigate, "Guide", "Guide"),
        new(Key.F, ModifierKeys.Control, ShellShortcutAction.FindOnPage, null, "Go to the search box on Accounts, Join a game, Diagnostics or Settings"),
    ]);

    /// <summary>The named search box Ctrl+F focuses on each tab that has one. Other tabs leave Ctrl+F alone.</summary>
    internal static IReadOnlyDictionary<string, string> SearchBoxes { get; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Accounts"] = "AccountsSearchBox",
        ["JoinGame"] = "DestinationInput",
        ["Diagnostics"] = "DiagnosticsFilterBox",
        ["Settings"] = "SettingsSearchBox",
    });

    internal static string? SearchBoxFor(string tab) => SearchBoxes.GetValueOrDefault(tab);

    /// <summary>One key binding per row, in table order. Navigate rows pass their tab as the parameter. The caller owns the
    /// bindings (MainWindow adds them to its own InputBindings).</summary>
    internal static KeyBinding[] CreateBindings(ICommand panic, ICommand navigate, ICommand showWindowsOverview, ICommand findOnPage) =>
        All.Select(row => new KeyBinding(row.Action switch
        {
            ShellShortcutAction.Panic => panic,
            ShellShortcutAction.Navigate => navigate,
            ShellShortcutAction.ShowWindowsOverview => showWindowsOverview,
            _ => findOnPage,
        }, row.Key, row.Modifiers) { CommandParameter = row.Page }).ToArray();

    internal static string Format(Key key, ModifierKeys modifiers)
    {
        string name = key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.A and <= Key.Z or >= Key.F1 and <= Key.F24 => key.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "A manager shortcut uses a digit, a letter or an F-key."),
        };
        return (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "")
            + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + name;
    }
}
