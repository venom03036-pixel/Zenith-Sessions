using System.Windows;
using System.Windows.Input;
using MacroSuite.App.ViewModels;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Studio-styled bind conflict dialog. Cancel restores the original keybind.
/// </summary>
public partial class HotkeyConflictDialog : Window
{
    public string KeyCaption { get; }
    public string OriginalCaption { get; }
    public string SummaryText { get; }
    public string PressedOwnerText { get; }
    public string OriginalOwnerText { get; }
    public string SuggestedHotkey { get; }
    public string SuggestedCaption { get; }
    public string SuggestedButtonText { get; }
    public string StealButtonText { get; }
    public string CancelButtonText { get; }

    public HotkeyConflictChoice Choice { get; private set; } = HotkeyConflictChoice.Cancel;

    public HotkeyConflictDialog(HotkeyConflictPrompt prompt)
    {
        KeyCaption = string.IsNullOrWhiteSpace(prompt.Hotkey) ? "—" : prompt.Hotkey.Trim();
        OriginalCaption = string.IsNullOrWhiteSpace(prompt.OriginalHotkey) ? "—" : prompt.OriginalHotkey.Trim();
        SuggestedHotkey = string.IsNullOrWhiteSpace(prompt.SuggestedHotkey) ? "Ctrl+1" : prompt.SuggestedHotkey.Trim();

        string previous = string.IsNullOrWhiteSpace(prompt.PreviousOwner) ? "another bind" : prompt.PreviousOwner.Trim();
        string next = string.IsNullOrWhiteSpace(prompt.NewOwner) ? "this bind" : prompt.NewOwner.Trim();

        SummaryText = $"{KeyCaption} is already assigned to {previous}. Choose a free combo, steal the key, or cancel to keep {next} on {OriginalCaption}.";
        PressedOwnerText = $"In use by {previous}";
        OriginalOwnerText = string.IsNullOrWhiteSpace(prompt.OriginalHotkey)
            ? $"{next} has no key yet"
            : $"Keep {next} on this key";
        SuggestedCaption = $"Leave {previous} on {KeyCaption} and give {next} this unused combo.";
        SuggestedButtonText = $"Use {SuggestedHotkey}";
        StealButtonText = $"Use {KeyCaption}";
        CancelButtonText = string.IsNullOrWhiteSpace(prompt.OriginalHotkey) ? "Cancel" : $"Keep {OriginalCaption}";

        InitializeComponent();
        DataContext = this;
    }

    public static HotkeyConflictChoice Ask(Window? owner, HotkeyConflictPrompt prompt)
    {
        var dialog = new HotkeyConflictDialog(prompt);
        if (owner != null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.Loaded += (_, _) => ClampToWorkArea(dialog);
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(HotkeyConflictChoice.Cancel);

    private void Confirm_Click(object sender, RoutedEventArgs e) => CloseWith(HotkeyConflictChoice.UnbindPrevious);

    private void UseSuggested_Click(object sender, RoutedEventArgs e) => CloseWith(HotkeyConflictChoice.UseSuggested);

    private void CloseWith(HotkeyConflictChoice choice)
    {
        Choice = choice;
        DialogResult = choice != HotkeyConflictChoice.Cancel;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        CloseWith(HotkeyConflictChoice.Cancel);
    }

    private static void ClampToWorkArea(Window dialog)
    {
        var work = SystemParameters.WorkArea;
        if (dialog.ActualWidth > work.Width)
            dialog.Width = Math.Max(320, work.Width - 24);
        if (dialog.ActualHeight > work.Height)
            dialog.Height = Math.Max(240, work.Height - 24);

        if (dialog.Left + dialog.ActualWidth > work.Right)
            dialog.Left = work.Right - dialog.ActualWidth;
        if (dialog.Top + dialog.ActualHeight > work.Bottom)
            dialog.Top = work.Bottom - dialog.ActualHeight;
        if (dialog.Left < work.Left)
            dialog.Left = work.Left;
        if (dialog.Top < work.Top)
            dialog.Top = work.Top;

        dialog.Activate();
    }
}
