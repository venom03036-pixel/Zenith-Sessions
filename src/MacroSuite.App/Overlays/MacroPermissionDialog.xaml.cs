using System.Windows;
using System.Windows.Input;
using MacroSuite.App.Services;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Security;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Modal WPF dialog that presents a macro's permission requirements and security
/// warnings to the user before their first run (or after the file changes).
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// var dialog = new MacroPermissionDialog(model, validationResult, permissions);
/// if (dialog.ShowDialog() == true)
/// {
///     await approvalService.RecordApprovalAsync(model.Id.ToString(), fileHash);
/// }
/// </code>
/// </remarks>
public partial class MacroPermissionDialog : Window
{
    /// <summary>
    /// <c>true</c> if the user clicked "Approve &amp; Run"; <c>false</c> if they cancelled.
    /// </summary>
    public bool UserApproved { get; private set; }

    /// <summary>
    /// Initializes the dialog and populates all data-bound controls.
    /// </summary>
    /// <param name="model">The macro awaiting approval.</param>
    /// <param name="validationResult">The security scan result for this macro.</param>
    /// <param name="permissions">Human-readable capability list from <see cref="MacroApprovalService"/>.</param>
    public MacroPermissionDialog(
        MacroModel model,
        MacroValidationResult validationResult,
        IReadOnlyList<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(validationResult);
        ArgumentNullException.ThrowIfNull(permissions);

        InitializeComponent();

        // Macro identity
        MacroNameText.Text = string.IsNullOrWhiteSpace(model.Name)
            ? "(Unnamed Macro)"
            : model.Name;
        MacroIdText.Text = $"ID: {model.Id}";

        // Permission list
        PermissionsList.ItemsSource = permissions;

        // Violations card — only visible when there are violations
        if (validationResult.Violations.Count > 0)
        {
            ViolationsList.ItemsSource = validationResult.Violations;
            ViolationsCard.Visibility = Visibility.Visible;

            // Change approve button label to make clear this is risky
            ApproveBtn.Content = "⚠  Approve Anyway";
        }
    }

    // ── Event handlers ─────────────────────────────────────────────────────

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        UserApproved = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        UserApproved = false;
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        UserApproved = false;
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
