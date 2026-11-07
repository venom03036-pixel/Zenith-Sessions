using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed record AccountGroupColorChoice(string Name, string? ColorHex)
{ public override string ToString() => Name; }
public sealed record AccountGroupOption(AccountGroupDefinition Definition)
{
    public string Name => Definition.Name;
    public string? ColorHex => Definition.ColorHex;
    public override string ToString() => Name;
}

/// <summary>Bounded presentation only. Names/colors never select recipients or confer authority.</summary>
public static class GroupPresentation
{
    public const string SuggestionsHelp = "Suggestions show a bounded catalog page. Type any group name, or use Manage groups to browse the catalog.";
    public static IReadOnlyList<AccountGroupColorChoice> Colors { get; } = Array.AsReadOnly(new AccountGroupColorChoice[]
    {
        new AccountGroupColorChoice("Theme accent", null), new("Mint", "#71D7C0"), new("Blue", "#78B5F4"),
        new("Amber", "#F0BC63"), new("Violet", "#AF9AF6"), new("Rose", "#E99CAE"),
        new("Teal", "#57C4D0"), new("Green", "#9ACD7E"), new("Orange", "#F09A68")
    });
    public static string? ColorInput(string? text) => string.IsNullOrWhiteSpace(text) ? null : AccountGroupDefinition.ValidateColor(text);
    public static AccountGroupColorChoice? ColorChoice(string? text)
    {
        try { string? color = ColorInput(text); return Colors.FirstOrDefault(choice => choice.ColorHex == color); }
        catch (ArgumentException) { return null; }
    }
    public static ComboBox EditablePicker(IReadOnlyList<AccountGroupDefinition> groups, string text, string accessibleName)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count > 1000) throw new ArgumentException("Use a catalog suggestion page of at most 1,000 groups.");
        var options = new AccountGroupOption[groups.Count]; var ids = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < groups.Count; index++)
        {
            var definition = groups[index].Validated();
            if (!ids.Add(definition.Id) || !names.Add(definition.Name)) throw new ArgumentException("Group suggestions contain duplicates.");
            options[index] = new(definition);
        }
        var picker = new ComboBox { IsEditable = true, ItemsSource = Array.AsReadOnly(options), ItemTemplate = ItemTemplate(), MinHeight = 36, MaxDropDownHeight = 240, Text = text };
        picker.SetResourceReference(FrameworkElement.StyleProperty, "SessionsEditableComboBox");
        TextSearch.SetTextPath(picker, nameof(AccountGroupOption.Name));
        System.Windows.Automation.AutomationProperties.SetName(picker, accessibleName);
        picker.Loaded += (_, _) =>
        {
            picker.ApplyTemplate();
            if (picker.Template?.FindName("PART_EditableTextBox", picker) is TextBox field) field.MaxLength = AccountGroups.MaximumNameLength;
        };
        return picker;
    }
    public static DataTemplate ItemTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var swatch = new FrameworkElementFactory(typeof(GroupColorSwatch)); swatch.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        swatch.SetBinding(GroupColorSwatch.ColorHexProperty, new Binding("ColorHex")); row.AppendChild(swatch);
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new Binding("Name")); name.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Name"));
        name.SetValue(FrameworkElement.MaxWidthProperty, 260d); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); row.AppendChild(name);
        var color = new FrameworkElementFactory(typeof(TextBlock)); color.SetBinding(TextBlock.TextProperty, new Binding("ColorHex") { TargetNullValue = "Theme accent" });
        color.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); color.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0)); color.SetValue(TextBlock.FontSizeProperty, 11d); color.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); row.AppendChild(color);
        return new DataTemplate { VisualTree = row };
    }
}

/// <summary>A theme-following swatch with text supplied alongside it by the item template.</summary>
public sealed class GroupColorSwatch : Border
{
    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(nameof(ColorHex), typeof(string), typeof(GroupColorSwatch),
        new PropertyMetadata(null, (owner, _) => ((GroupColorSwatch)owner).RefreshColor()));
    public string? ColorHex { get => (string?)GetValue(ColorHexProperty); set => SetValue(ColorHexProperty, value); }
    public GroupColorSwatch()
    {
        Width = Height = 12; CornerRadius = new CornerRadius(2); BorderThickness = new Thickness(1); VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(BorderBrushProperty, "ControlBorder"); RefreshColor();
    }
    private void RefreshColor()
    {
        string? color;
        try { color = GroupPresentation.ColorInput(ColorHex); }
        catch (ArgumentException) { color = null; } // An unfinished HEX draft gets theme preview; Save performs validation.
        if (color is null) { SetResourceReference(BackgroundProperty, "AccentPrimary"); return; }
        var brush = new SolidColorBrush(Color.FromRgb(byte.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber),
            byte.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber), byte.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber)));
        brush.Freeze(); Background = brush;
    }
}
