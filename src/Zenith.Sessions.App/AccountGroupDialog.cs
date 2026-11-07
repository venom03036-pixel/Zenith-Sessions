using System.Windows;
using System.Windows.Controls;

namespace Zenith.Sessions.App;

/// <summary>Edits metadata for the immutable recipient snapshot supplied by the caller.</summary>
public sealed class AccountGroupDialog : Window
{
    public AccountGroupDialog(int count, IReadOnlyList<string> existingGroups, Func<string, Task> apply, IReadOnlyList<Zenith.Sessions.Core.AccountGroupDefinition>? catalogGroups = null)
    {
        Title = "Add accounts to group"; Width = 480; Height = 330; MinWidth = 400; MinHeight = 290;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CardBackground"); SetResourceReference(ForegroundProperty, "TextPrimary");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        var summary = new TextBlock { Text = $"{count} selected account(s). Their existing groups stay in place. This does not launch or move clients.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 18) };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); panel.Children.Add(summary);
        panel.Children.Add(new TextBlock { Text = "Choose a group or enter a new name", Margin = new Thickness(0, 0, 0, 6) });
        var name = catalogGroups is null ? new ComboBox { IsEditable = true, ItemsSource = existingGroups, MinHeight = 36, MaxDropDownHeight = 240 }
            : GroupPresentation.EditablePicker(catalogGroups, "", "Account group name");
        name.SetResourceReference(StyleProperty, "SessionsEditableComboBox");
        System.Windows.Automation.AutomationProperties.SetName(name, "Account group name"); panel.Children.Add(name);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "AccentDanger"); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Add to group", IsDefault = true, MinWidth = 120 };
        save.SetResourceReference(StyleProperty, "PrimaryButton"); actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions);
        bool saving = false;
        save.Click += async (_, _) =>
        {
            if (saving) return;
            saving = true; save.IsEnabled = false; cancel.IsEnabled = false; name.IsEnabled = false; save.Content = "Saving…";
            try { await apply(name.Text); saving = false; DialogResult = true; }
            catch (Exception ex)
            {
                error.Text = ex is OperationCanceledException ? "The change was stopped. Refresh the library before retrying."
                    : ex is ArgumentException or Zenith.Sessions.Core.RevisionConflictException ? ex.Message
                    : "The group change could not finish. Refresh the library and review the selected accounts before retrying.";
                saving = false; save.IsEnabled = true; cancel.IsEnabled = true; name.IsEnabled = true; save.Content = "Add to group";
            }
        };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) =>
        {
            name.ApplyTemplate();
            if (name.Template?.FindName("PART_EditableTextBox", name) is TextBox field)
                field.MaxLength = Zenith.Sessions.Core.AccountGroups.MaximumNameLength;
            name.Focus();
        };
        Closing += (_, e) => { if (saving) e.Cancel = true; };
    }
}
