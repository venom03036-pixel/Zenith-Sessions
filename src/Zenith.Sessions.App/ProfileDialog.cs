using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed class ProfileDialog : Window
{
    private readonly TextBox _label = new(), _username = new(), _group = new(), _additionalGroups = new(), _place = new(), _priority = new(), _notes = new();
    private readonly ComboBox? _groupPicker;
    private readonly CheckBox _favorite = new() { Content="Favorite account",Margin=new Thickness(0,0,0,14) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private bool _saving;
    public ProfileDialog(AccountProfile profile, Func<AccountProfile, Task> save, IReadOnlyList<AccountGroupDefinition>? groups = null)
    {
        Title = profile.Revision == 0 ? "Add account profile" : "Edit account profile";
        var workArea = SystemParameters.WorkArea;
        double availableWidth = Math.Max(1, workArea.Width), availableHeight = Math.Max(1, workArea.Height);
        Width = Math.Min(540, availableWidth); Height = Math.Min(730, availableHeight);
        MinWidth = Math.Min(480, availableWidth); MinHeight = Math.Min(420, availableHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, "CardBackground"); SetResourceReference(ForegroundProperty, "TextPrimary");
        _label.Text = profile.Label; _username.Text = profile.Username; _group.Text = profile.Group; _additionalGroups.Text = profile.AdditionalGroups;
        _place.Text = profile.PreferredPlaceId?.ToString(CultureInfo.InvariantCulture) ?? ""; _priority.Text = profile.Priority.ToString(CultureInfo.InvariantCulture);
        _notes.Text=profile.Notes; _favorite.IsChecked=profile.IsFavorite;
        var layout = new Grid { Margin = new Thickness(26) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        var description = new TextBlock { Text = "Save local metadata. A username does not authenticate an account or identify a running client.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
        description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); header.Children.Add(description); layout.Children.Add(header);
        var panel = new StackPanel();
        AddField(panel, "Display name", _label); AddField(panel, "Username · optional", _username);
        if (groups is null) AddField(panel, "Primary group", _group);
        else
        {
            panel.Children.Add(new TextBlock { Text = "Primary group", Margin = new Thickness(0, 0, 0, 6) });
            _groupPicker = GroupPresentation.EditablePicker(groups, profile.Group, "Primary group");
            _groupPicker.Margin = new Thickness(0, 0, 0, 6); panel.Children.Add(_groupPicker);
            var groupHelp = new TextBlock { Text = GroupPresentation.SuggestionsHelp, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
            groupHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); panel.Children.Add(groupHelp);
        }
        _group.MaxLength = AccountGroups.MaximumNameLength;
        AddField(panel, "Other groups · one per line, up to 31", _additionalGroups);
        _additionalGroups.MaxLength = AccountGroups.MaximumInputLength; _additionalGroups.AcceptsReturn = true;
        _additionalGroups.TextWrapping = TextWrapping.Wrap; _additionalGroups.Height = 80; _additionalGroups.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        AddField(panel, "Preferred Roblox place ID · optional", _place); AddField(panel, "Priority · 0 to 100", _priority);
        panel.Children.Add(_favorite);
        AddField(panel,"Notes · included in metadata exports; never enter secrets",_notes);
        _notes.MaxLength=4000; _notes.AcceptsReturn=true; _notes.TextWrapping=TextWrapping.Wrap; _notes.Height=100; _notes.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        var fields = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 4, 0) };
        System.Windows.Automation.AutomationProperties.SetName(fields, "Account profile fields");
        Grid.SetRow(fields, 1); layout.Children.Add(fields);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "AccentDanger");
        System.Windows.Automation.AutomationProperties.SetName(_error, "Profile validation error");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_error, System.Windows.Automation.AutomationLiveSetting.Polite);
        // Errors stay next to the fixed actions. A long storage/validation message can
        // scroll independently without moving Save and Cancel out of the window.
        var errors = new ScrollViewer { Content = _error, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetName(errors, "Profile validation details");
        Grid.SetRow(errors, 2); layout.Children.Add(errors);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var commit = new Button { Content = "Save profile", MinWidth = 110, IsDefault = true };
        commit.SetResourceReference(StyleProperty, "PrimaryButton");
        commit.Click += async (_, _) =>
        {
            if (_saving) return;
            try
            {
                long? place = null;
                if (!string.IsNullOrWhiteSpace(_place.Text)) { if (!long.TryParse(_place.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0) throw new ArgumentException("Enter a positive numeric place ID, or leave it blank."); place = id; }
                if (!int.TryParse(_priority.Text, out int priority)) throw new ArgumentException("Priority must be a whole number between 0 and 100.");
                var draft = (profile with { Label = _label.Text, Username = _username.Text, Group = _groupPicker?.Text ?? _group.Text, AdditionalGroups = _additionalGroups.Text, PreferredPlaceId = place, Priority = priority,Notes=_notes.Text,IsFavorite=_favorite.IsChecked==true }).Validated();
                _saving = true; commit.IsEnabled = false; cancel.IsEnabled = false; commit.Content = "Saving…";
                await save(draft); _saving = false; DialogResult = true;
            }
            catch (Exception ex)
            {
                _error.Text = ex is ArgumentException or RevisionConflictException ? ex.Message : "The profile could not be saved. Your edits remain here; check storage access and retry.";
                errors.Visibility = Visibility.Visible;
                _saving = false; commit.IsEnabled = true; cancel.IsEnabled = true; commit.Content = "Save profile";
            }
        };
        actions.Children.Add(cancel); actions.Children.Add(commit); Grid.SetRow(actions, 3); layout.Children.Add(actions);
        Content = layout;
        Loaded += (_, _) => { _label.Focus(); _label.SelectAll(); };
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
    }
    private static void AddField(Panel parent, string label, TextBox field)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        field.Margin = new Thickness(0, 0, 0, 12); field.MinHeight = 36; field.MaxLength = 100;
        System.Windows.Automation.AutomationProperties.SetName(field, label); parent.Children.Add(field);
    }
}
