using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>One library-wide metadata review. SQLite and cancellation ownership stay in the Shell.</summary>
public sealed class AccountGroupManagerDialog : Window
{
    private readonly AccountGroupManagerViewModel _model;
    private readonly ShellViewModel _owner;
    private bool _allowClose, _closing;
    public AccountGroupManagerDialog(ShellViewModel owner)
    {
        _owner = owner;
        Resources.MergedDictionaries.Add(GroupThemeResources());
        _model = new(owner.Group.Value, () => owner.Groups.Where(group => group.Value is not null).Select(group => group.Value!).ToArray(),
            () => owner.IsReady && !owner.IsLocked,
            owner.PreviewGroupChangeAsync, owner.ApplyGroupChangeAsync, catalog: owner.HasGroupCatalog ? owner : null, showAccounts: owner.ShowGroupAccountsAsync);
        DataContext = _model; Title = "Manage account groups";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(620, area.Width); Height = Math.Min(740, area.Height);
        MinWidth = Math.Min(480, area.Width); MinHeight = Math.Min(440, area.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CardBackground"); SetResourceReference(ForegroundProperty, "TextPrimary");
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        var description = new TextBlock { Text = _model.HasCatalog ? "Keep empty groups, choose colors and arrange group lists. Rename or remove memberships with a separate library-wide review."
            : "Rename or remove memberships across the whole library. The review includes accounts on other pages and outside the current filter.",
            Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); heading.Children.Add(description); root.Children.Add(heading);
        var fields = new StackPanel(); fields.SetBinding(IsEnabledProperty, new Binding(nameof(_model.CanEdit)));
        AddLabel(fields, "Operation");
        var operation = new ComboBox { ItemsSource = AccountGroupManagerViewModel.Choices, DisplayMemberPath = "Label", MinHeight = 36, Margin = new Thickness(0, 0, 0, 14) };
        operation.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(_model.Choice)) { Mode = BindingMode.TwoWay });
        System.Windows.Automation.AutomationProperties.SetName(operation, "Group operation"); fields.Children.Add(operation);
        AddLabel(fields, "Existing group");
        var source = new ComboBox { IsEditable = true, MinHeight = 36, MaxDropDownHeight = 220, Margin = new Thickness(0, 0, 0, 14) };
        source.SetResourceReference(StyleProperty, "SessionsEditableComboBox");
        source.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(_model.Groups)));
        source.SetBinding(ComboBox.TextProperty, new Binding(nameof(_model.SourceName)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        System.Windows.Automation.AutomationProperties.SetName(source, "Existing group name"); fields.Children.Add(source);
        fields.Children.Add(Help(GroupPresentation.SuggestionsHelp));
        var destinationLabel = new TextBlock { Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        destinationLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(_model.DestinationLabel))); fields.Children.Add(destinationLabel);
        var destination = new TextBox { MaxLength = AccountGroups.MaximumNameLength, MinHeight = 36, Margin = new Thickness(0, 0, 0, 14) };
        destination.SetBinding(TextBox.TextProperty, new Binding(nameof(_model.DestinationName)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        destination.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(_model.DestinationLabel))); fields.Children.Add(destination);
        var merge = new CheckBox { Content = "Allow merging into an existing group", Margin = new Thickness(0, 0, 0, 14), VerticalContentAlignment = VerticalAlignment.Center };
        merge.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(_model.AllowMerge)) { Mode = BindingMode.TwoWay });
        merge.SetBinding(VisibilityProperty, new Binding(nameof(_model.IsRename)) { Converter = new BooleanToVisibilityConverter() }); fields.Children.Add(merge);
        var review = new Button { Content = "Review affected accounts", HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 36 };
        review.SetBinding(Button.CommandProperty, new Binding(nameof(_model.Review))); review.SetBinding(IsEnabledProperty, new Binding(nameof(_model.CanReview))); fields.Children.Add(review);
        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var tabs = new TabControl { Style = (Style)Resources["GroupManagerTabs"] };
        var catalogTab = new TabItem { Header = "Groups and colors", Style = (Style)Resources["GroupManagerTab"], Content = CatalogPanel(), Visibility = _model.HasCatalog ? Visibility.Visible : Visibility.Collapsed };
        var reviewTab = new TabItem { Header = "Rename or remove", Style = (Style)Resources["GroupManagerTab"], Content = scroll };
        tabs.Items.Add(catalogTab); tabs.Items.Add(reviewTab); tabs.SelectedIndex = _model.HasCatalog ? 0 : 1;
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); status.SetBinding(TextBlock.TextProperty, new Binding(nameof(_model.Status)));
        footer.Children.Add(new ScrollViewer { Content = status, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        actions.Children.Add(close);
        var apply = new Button { MinWidth = 120, MinHeight = 36 };
        apply.SetResourceReference(StyleProperty, "PrimaryButton"); apply.SetBinding(ContentProperty, new Binding(nameof(_model.ApplyLabel)));
        apply.SetBinding(Button.CommandProperty, new Binding(nameof(_model.Apply))); apply.SetBinding(IsEnabledProperty, new Binding(nameof(_model.CanApply)));
        apply.Visibility = _model.HasCatalog ? Visibility.Collapsed : Visibility.Visible;
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, tabs)) apply.Visibility = ReferenceEquals(tabs.SelectedItem, reviewTab) ? Visibility.Visible : Visibility.Collapsed; };
        actions.Children.Add(apply); footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        Loaded += (_, _) =>
        {
            source.ApplyTemplate();
            if (source.Template?.FindName("PART_EditableTextBox", source) is TextBox field) field.MaxLength = AccountGroups.MaximumNameLength;
            if (_model.HasCatalog) _model.RefreshCatalog.Execute(null); else source.Focus();
        };
        owner.PanicRequested += OwnerPanic; owner.PropertyChanged += OwnerChanged;
        Closing += JoinOnClosing;
        Closed += (_, _) => { owner.PanicRequested -= OwnerPanic; owner.PropertyChanged -= OwnerChanged; };
    }
    private static void AddLabel(Panel fields, string text)
        => fields.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6) });
    private FrameworkElement CatalogPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; panel.SetBinding(IsEnabledProperty, new Binding(nameof(_model.CanEdit)));
        var search = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var reload = CatalogButton("Search / reload", nameof(_model.RefreshCatalog), nameof(_model.CanRefreshCatalog)); DockPanel.SetDock(reload, Dock.Right); search.Children.Add(reload);
        var input = new TextBox { MaxLength = AccountGroups.MaximumNameLength, MinHeight = 36, Margin = new Thickness(0, 0, 8, 0) };
        input.SetBinding(TextBox.TextProperty, Edit(nameof(_model.CatalogSearch))); System.Windows.Automation.AutomationProperties.SetName(input, "Search saved groups"); search.Children.Add(input); panel.Children.Add(search);
        var groups = new ListBox { Height = 156, MinHeight = 100, ItemTemplate = CatalogRowTemplate(), SelectionMode = SelectionMode.Single,
            Style = (Style)Resources["GroupCatalogList"], ItemContainerStyle = (Style)Resources["GroupCatalogRow"], BorderThickness = new Thickness(1) };
        groups.SetResourceReference(BackgroundProperty, "SurfaceBackground"); groups.SetResourceReference(ForegroundProperty, "TextPrimary"); groups.SetResourceReference(BorderBrushProperty, "ControlBorder");
        groups.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        VirtualizingPanel.SetIsVirtualizing(groups, true); VirtualizingPanel.SetVirtualizationMode(groups, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(groups, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(groups, ScrollBarVisibility.Disabled);
        groups.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(_model.CatalogGroups))); groups.SetBinding(ListBox.SelectedItemProperty, Edit(nameof(_model.SelectedGroup)));
        System.Windows.Automation.AutomationProperties.SetName(groups, "Saved groups"); panel.Children.Add(groups);
        var pages = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        pages.Children.Add(CatalogButton("Previous", nameof(_model.PreviousCatalogPage), nameof(_model.CanPreviousCatalogPage)));
        pages.Children.Add(CatalogButton("Next", nameof(_model.NextCatalogPage), nameof(_model.CanNextCatalogPage))); panel.Children.Add(pages);
        var pageLabel = Help(""); pageLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(_model.CatalogPageLabel))); panel.Children.Add(pageLabel);
        var selected = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
        selected.SetBinding(TextBlock.TextProperty, new Binding(nameof(_model.SelectedGroupSummary))); panel.Children.Add(selected);
        var selectedColor = ColorEditor(nameof(_model.SelectedColorChoice), nameof(_model.SelectedColorHex), "Selected group");
        selectedColor.SetBinding(IsEnabledProperty, new Binding(nameof(_model.CanSaveGroupColor))); panel.Children.Add(selectedColor);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        actions.Children.Add(CatalogButton("Save color", nameof(_model.SaveGroupColor), nameof(_model.CanSaveGroupColor)));
        actions.Children.Add(CatalogButton("Show accounts", nameof(_model.ShowAccounts), nameof(_model.CanShowAccounts)));
        var up = CatalogButton("Move up", nameof(_model.MoveGroupUp), nameof(_model.CanMoveGroupUp)); up.ToolTip = "Move within the full group list. Account and launch priorities stay unchanged."; actions.Children.Add(up);
        var down = CatalogButton("Move down", nameof(_model.MoveGroupDown), nameof(_model.CanMoveGroupDown)); down.ToolTip = up.ToolTip; actions.Children.Add(down); panel.Children.Add(actions);
        panel.Children.Add(new TextBlock { Text = "Create an empty group", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        AddLabel(panel, "New group name"); var name = new TextBox { MaxLength = AccountGroups.MaximumNameLength, MinHeight = 36, Margin = new Thickness(0, 0, 0, 8) };
        name.SetBinding(TextBox.TextProperty, Edit(nameof(_model.NewGroupName))); System.Windows.Automation.AutomationProperties.SetName(name, "New group name"); panel.Children.Add(name);
        panel.Children.Add(ColorEditor(nameof(_model.NewColorChoice), nameof(_model.NewColorHex), "New group"));
        panel.Children.Add(CatalogButton("Create empty group", nameof(_model.CreateGroup), nameof(_model.CanCreateGroup)));
        panel.Children.Add(Help("Empty groups stay saved until explicitly removed. Pages and suggestions are bounded; they are not account or group storage limits."));
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private static StackPanel ColorEditor(string choiceProperty, string textProperty, string label)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var choices = new ComboBox { ItemsSource = GroupPresentation.Colors, ItemTemplate = GroupPresentation.ItemTemplate(), MinHeight = 36, MaxDropDownHeight = 240 };
        choices.SetBinding(ComboBox.SelectedItemProperty, Edit(choiceProperty)); System.Windows.Automation.AutomationProperties.SetName(choices, label + " color"); panel.Children.Add(choices);
        var hex = new TextBox { MaxLength = 32, MinHeight = 36, Margin = new Thickness(0, 6, 0, 0) };
        hex.SetBinding(TextBox.TextProperty, Edit(textProperty)); System.Windows.Automation.AutomationProperties.SetName(hex, label + " HEX color"); panel.Children.Add(hex);
        panel.Children.Add(Help("Optional #RRGGBB · leave blank to follow the theme accent.")); return panel;
    }
    private static Button CatalogButton(string text, string command, string enabled)
    {
        var button = new Button { Content = text, MinHeight = 36, MinWidth = 82, Margin = new Thickness(0, 0, 8, 0) };
        button.SetBinding(Button.CommandProperty, new Binding(command)); button.SetBinding(IsEnabledProperty, new Binding(enabled));
        System.Windows.Automation.AutomationProperties.SetName(button, text); return button;
    }
    private static Binding Edit(string property) => new(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
    private static TextBlock Help(string text)
    {
        var help = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8), FontSize = 12 };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); return help;
    }
    private static DataTemplate CatalogRowTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var swatch = new FrameworkElementFactory(typeof(GroupColorSwatch)); swatch.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        swatch.SetBinding(GroupColorSwatch.ColorHexProperty, new Binding(nameof(AccountGroupDefinition.ColorHex))); row.AppendChild(swatch);
        var name = new FrameworkElementFactory(typeof(TextBlock), "CatalogGroupName"); name.SetBinding(TextBlock.TextProperty, new Binding(nameof(AccountGroupDefinition.Name)));
        name.SetBinding(ToolTipProperty, new Binding(nameof(AccountGroupDefinition.Name))); name.SetValue(MaxWidthProperty, 260d); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) });
        row.AppendChild(name);
        var color = new FrameworkElementFactory(typeof(TextBlock), "CatalogGroupColor");
        color.SetBinding(TextBlock.TextProperty, new Binding(nameof(AccountGroupDefinition.ColorHex)) { TargetNullValue = "Theme accent" });
        color.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) });
        color.SetValue(MarginProperty, new Thickness(8, 0, 0, 0)); color.SetValue(TextBlock.FontSizeProperty, 11d); color.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); row.AppendChild(color);
        return new DataTemplate { VisualTree = row };
    }
    private static ResourceDictionary GroupThemeResources() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="GroupManagerTabs" TargetType="TabControl">
            <Setter Property="Background" Value="{DynamicResource CardBackground}"/><Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource ControlBorder}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="12"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabControl">
              <Grid KeyboardNavigation.TabNavigation="Local" SnapsToDevicePixels="True">
                <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                <TabPanel x:Name="GroupHeaderPanel" IsItemsHost="True" Panel.ZIndex="1" KeyboardNavigation.TabIndex="0"/>
                <Border x:Name="GroupContentSurface" Grid.Row="1" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                  <ContentPresenter x:Name="PART_SelectedContentHost" ContentSource="SelectedContent" HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/>
                </Border>
              </Grid>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="GroupManagerTab" TargetType="TabItem">
            <Setter Property="Background" Value="{DynamicResource CardBackground}"/><Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource ControlBorder}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="12,8"/>
            <Setter Property="MinHeight" Value="40"/><Setter Property="FontWeight" Value="SemiBold"/><Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{DynamicResource KeyboardFocusRing}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem">
              <Border x:Name="GroupTabHeaderChrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                      BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="4,4,0,0" Margin="0,0,4,0">
                <TextBlock x:Name="GroupTabHeaderText" Text="{TemplateBinding Header}" Foreground="{TemplateBinding Foreground}"
                           FontWeight="{TemplateBinding FontWeight}" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource HoverBackground}"/><Setter Property="BorderBrush" Value="{DynamicResource SelectionBorder}"/></Trigger>
                <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{DynamicResource SelectionBackground}"/><Setter Property="Foreground" Value="{DynamicResource SelectionForeground}"/><Setter Property="BorderBrush" Value="{DynamicResource SelectionBorder}"/></Trigger>
                <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource SelectionBorder}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="GroupTabHeaderChrome" Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="GroupCatalogList" TargetType="ListBox">
            <Setter Property="Background" Value="{DynamicResource SurfaceBackground}"/><Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource ControlBorder}"/><Setter Property="BorderThickness" Value="1"/>
            <Setter Property="FocusVisualStyle" Value="{DynamicResource KeyboardFocusRing}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBox">
              <Border x:Name="GroupListSurface" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="2">
                <ScrollViewer x:Name="PART_ScrollViewer" CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                  <ItemsPresenter/>
                </ScrollViewer>
              </Border>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="GroupCatalogRow" TargetType="ListBoxItem">
            <Setter Property="AutomationProperties.Name" Value="{Binding Name}"/>
            <Setter Property="Background" Value="{DynamicResource SurfaceBackground}"/><Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="BorderBrush" Value="Transparent"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="8,6"/>
            <Setter Property="MinHeight" Value="36"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="FocusVisualStyle" Value="{DynamicResource KeyboardFocusRing}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
              <Border x:Name="GroupRowChrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                      BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="3" Margin="0,1">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource HoverBackground}"/></Trigger>
                <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{DynamicResource SelectionBackground}"/><Setter Property="Foreground" Value="{DynamicResource SelectionForeground}"/><Setter Property="BorderBrush" Value="{DynamicResource SelectionBorder}"/></Trigger>
                <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource SelectionBorder}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="GroupRowChrome" Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);
    private void OwnerPanic() => _model.Fence("Group changes stopped. Close this dialog and review the library before trying again.");
    private void OwnerChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ShellViewModel.IsLocked) && _owner.IsLocked) OwnerPanic(); }
    private async void JoinOnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true; if (_closing) return; _closing = true;
        await _model.DisposeAsync();
        // Disposal can complete synchronously. Let this cancelled Closing event return
        // before requesting Close again; WPF rejects a reentrant close and terminates.
        await Dispatcher.Yield(DispatcherPriority.Background);
        _allowClose = true; Close();
    }
}
