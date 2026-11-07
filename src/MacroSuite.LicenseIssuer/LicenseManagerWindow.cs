using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using MacroSuite.Core.Licensing;
using Microsoft.Win32;

namespace MacroSuite.LicenseIssuer;

/// <summary>Private personal-edition owner workflow. This entire assembly is absent from Public.</summary>
public sealed class LicenseManagerWindow : Window
{
    private readonly OwnerIssuerService _issuer;
    private readonly string? _expectedTrustManifestJson;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) };
    private readonly TextBlock _fingerprint = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new("Consolas"), Margin = new(0, 4, 0, 8) };
    private readonly TextBox _request = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, MaxLength = ActivationProduct.MaximumFileBytes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _customer = new() { MaxLength = 128, MinHeight = 36 };
    private readonly CheckBox _fallback = new() { Content = "Approve weaker software protection for this request", Margin = new(0, 8, 0, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _replacement = new() { Content = "Replace this customer's approved device", Margin = new(0, 4, 0, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _reason = new() { MaxLength = 256, MinHeight = 36, ToolTip = "Required when replacing an approved device" };
    private readonly TextBox _license = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 82, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _testLicenseSelection = new() { MinHeight = 36, DisplayMemberPath = nameof(OwnerTestLicense.DisplayName), Visibility = Visibility.Collapsed };
    private IReadOnlyList<OwnerTestLicense>? _testLicenses;
    private readonly PasswordBox _password = new() { MinHeight = 36, MaxLength = 1024, ToolTip = "Backup password: at least 12 characters. It is never stored." };
    private readonly DataGrid _ledger = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 180, EnableRowVirtualization = true, CanUserAddRows = false };
    private readonly List<Button> _buttons = new();
    private Task _operationTask = Task.CompletedTask;
    private bool _busy, _allowClose;

    public LicenseManagerWindow(OwnerIssuerService? issuer = null, string? expectedTrustManifestJson = null)
    {
        _issuer = issuer ?? new OwnerIssuerService();
        _expectedTrustManifestJson = expectedTrustManifestJson;
        _request.TextChanged += (_, _) =>
        {
            _testLicenses = null; _testLicenseSelection.ItemsSource = null;
            _testLicenseSelection.Visibility = Visibility.Collapsed; _license.Clear();
        };
        Title = "Zenith Macros · Owner license manager"; Width = 1000; Height = 850; MinWidth = 740; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        ConfigureLedgerTheme();
        var content = new StackPanel { Margin = new(24) };
        content.Children.Add(new TextBlock { Text = "Owner license manager", FontSize = 26, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "Issue one offline, device-bound Standard license per approved customer. Signing identity and ledger stay in your Windows profile.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 4) });
        content.Children.Add(_fingerprint); content.Children.Add(_status);
        var identityActions = new WrapPanel();
        identityActions.Children.Add(Button("Initialize / verify owner identity", async () =>
        {
            if (_expectedTrustManifestJson is not null && !File.Exists(Path.Combine(_issuer.OwnerDataDirectory, "issuer-identity-v2.bin")))
            {
                _status.Text = "This release already has an established owner identity. Restore its encrypted owner backup; creating another identity would produce licenses that Public cannot verify.";
                return;
            }
            var result = await _issuer.ProvisionAsync(_shutdown.Token); ShowIdentity(result);
            if (result.Success) await RefreshLedgerAsync();
        }));
        identityActions.Children.Add(Button("Export public trust manifest…", async () =>
        {
            var dialog = new SaveFileDialog { Filter = "Public trust manifest|*.json", FileName = "zenith-issuer-trust.json" };
            if (dialog.ShowDialog(this) != true) return;
            await _issuer.ExportTrustAsync(dialog.FileName, _shutdown.Token); _status.Text = "Public verification identity exported. This file contains no private signing key.";
        }));
        content.Children.Add(identityActions);
        Heading(content, "Customer activation request");
        content.Children.Add(Button("Import request…", async () =>
        {
            var dialog = new OpenFileDialog { Filter = "Activation request|*.zrequest;*.json;*.txt|All files|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            if (new FileInfo(dialog.FileName).Length > ActivationProduct.MaximumFileBytes) throw new FormatException("Activation requests must be at most 64 KiB.");
            string text = await File.ReadAllTextAsync(dialog.FileName, _shutdown.Token);
            if (!ActivationRequestCodec.TryVerify(text, out var request, out var error)) throw new FormatException(error);
            _request.Text = text; _fallback.IsChecked = false; _replacement.IsChecked = false; _reason.Text = string.Empty;
            _testLicenses = null; _testLicenseSelection.ItemsSource = null; _testLicenseSelection.Visibility = Visibility.Collapsed; _license.Clear();
            _status.Text = $"Verified request · {request!.DeviceProtection} · device {request.DeviceKeyId[..16]}… . TPM status is asserted by the requesting app; this flow does not provide remote attestation.";
        }));
        content.Children.Add(_request);
        Label(content, "Customer reference"); content.Children.Add(_customer); content.Children.Add(_fallback); content.Children.Add(_replacement);
        Label(content, "Replacement reason"); content.Children.Add(_reason);
        content.Children.Add(new TextBlock { Text = "A replacement is recorded in your ledger. Existing permanent offline copies cannot be remotely revoked.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 8) });
        content.Children.Add(Button("Generate device license", async () =>
        {
            if (!await EnsureCompatibleIssuerAsync()) return;
            var result = await _issuer.IssueAsync(_request.Text, _customer.Text, _fallback.IsChecked == true, _replacement.IsChecked == true, _reason.Text, _shutdown.Token);
            _status.Text = result.Message;
            if (result.Success) { _testLicenses = null; _testLicenseSelection.ItemsSource = null; _testLicenseSelection.Visibility = Visibility.Collapsed; _license.Text = result.LicenseText ?? string.Empty; await RefreshLedgerAsync(); }
        }));
        content.Children.Add(Button("Generate 3 test licenses", async () =>
        {
            if (!await EnsureCompatibleIssuerAsync()) return;
            var result = await _issuer.IssueThreeTestLicensesAsync(_request.Text, _customer.Text, _fallback.IsChecked == true, _shutdown.Token);
            _status.Text = result.Message;
            if (!result.Success) return;
            _testLicenses = result.Licenses; _testLicenseSelection.ItemsSource = _testLicenses;
            _testLicenseSelection.Visibility = Visibility.Visible; _testLicenseSelection.SelectedIndex = 0;
            await RefreshLedgerAsync();
        }));
        content.Children.Add(new TextBlock { Text = "Test licenses use this imported request's PC. They are ready to try individually in Public; they do not unlock other PCs.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 8) });
        _testLicenseSelection.SelectionChanged += (_, _) =>
        {
            if (_testLicenseSelection.SelectedItem is OwnerTestLicense selected) _license.Text = selected.LicenseText;
        };
        content.Children.Add(_testLicenseSelection);
        content.Children.Add(_license);
        var outputActions = new WrapPanel();
        outputActions.Children.Add(Button("Save license…", async () =>
        {
            if (string.IsNullOrEmpty(_license.Text)) throw new InvalidOperationException("Generate a license first.");
            var dialog = new SaveFileDialog { Filter = "Zenith license|*.zlicense", FileName = "Zenith-activation.zlicense" };
            if (dialog.ShowDialog(this) == true) await File.WriteAllTextAsync(dialog.FileName, _license.Text, _shutdown.Token);
        }));
        outputActions.Children.Add(Button("Copy activation code", () =>
        {
            if (string.IsNullOrEmpty(_license.Text)) throw new InvalidOperationException("Generate a license first.");
            Clipboard.SetText(ActivationWireFormat.AsLicenseCode(_license.Text)); _status.Text = "Device-bound activation code copied."; return Task.CompletedTask;
        }));
        outputActions.Children.Add(Button("Save all 3 test licenses…", async () =>
        {
            if (_testLicenses is not { Count: 3 }) throw new InvalidOperationException("Generate the three test licenses first.");
            var dialog = new SaveFileDialog { Filter = "Zenith test license bundle|*.zip", FileName = "Zenith-three-test-licenses.zip", OverwritePrompt = false };
            if (dialog.ShowDialog(this) != true) return;
            await OwnerTestLicenseExport.SaveAsync(dialog.FileName, _testLicenses, _shutdown.Token);
            _status.Text = "Three test licenses saved. Extract the ZIP and import any one .zlicense in Public on the request's PC.";
        }));
        content.Children.Add(outputActions);
        Heading(content, "Issuance and replacements");
        foreach (var column in new[] { ("Customer", "CustomerId", 2.0), ("License", "LicenseId", 2.0), ("Protection", "Protection", 1.0), ("State", "State", 1.0), ("Issued UTC", "IssuedUtc", 2.0) })
            _ledger.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new System.Windows.Data.Binding(column.Item2), ElementStyle = (Style)_ledger.Resources["OwnerLedgerText"], Width = new DataGridLength(column.Item3, DataGridLengthUnitType.Star) });
        content.Children.Add(_ledger); content.Children.Add(Button("Refresh ledger", RefreshLedgerAsync));
        Heading(content, "Encrypted owner backup");
        content.Children.Add(new TextBlock { Text = "Store the encrypted backup and its public trust manifest somewhere safe. Losing this identity prevents new licenses from working with existing public builds.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        Label(content, "Backup password (12 or more characters)"); content.Children.Add(_password);
        var backupActions = new WrapPanel();
        backupActions.Children.Add(Button("Save encrypted backup…", async () =>
        {
            var dialog = new SaveFileDialog { Filter = "Encrypted owner backup|*.zenithissuer", FileName = "Zenith-owner-identity.zenithissuer" };
            if (dialog.ShowDialog(this) != true) return;
            string password = _password.Password; _password.Clear();
            await _issuer.ExportBackupAsync(dialog.FileName, password, _shutdown.Token); _status.Text = "Encrypted owner identity and customer ledger backup saved. Keep the password separately.";
        }));
        backupActions.Children.Add(Button("Restore encrypted backup…", async () =>
        {
            var backup = new OpenFileDialog { Filter = "Encrypted owner backup|*.zenithissuer", Title = "Choose the encrypted owner backup" };
            if (backup.ShowDialog(this) != true) return;
            var trust = new OpenFileDialog { Filter = "Public trust manifest|*.json", Title = "Choose its original public trust manifest to verify the identity" };
            if (trust.ShowDialog(this) != true) return;
            if (new FileInfo(trust.FileName).Length > ActivationProduct.MaximumFileBytes) throw new FormatException("Trust manifest is too large.");
            string password = _password.Password; _password.Clear();
            var result = await _issuer.RestoreBackupAsync(backup.FileName, password, await File.ReadAllTextAsync(trust.FileName, _shutdown.Token), _shutdown.Token);
            ShowIdentity(result); if (result.Success) await RefreshLedgerAsync();
        }));
        content.Children.Add(backupActions);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Loaded += async (_, _) => await RunOperationAsync(async () =>
        {
            if (!File.Exists(Path.Combine(_issuer.OwnerDataDirectory, "issuer-identity-v2.bin"))) { _status.Text = "Initialize the owner identity once, or restore its encrypted backup if previously created."; return; }
            ShowIdentity(await _issuer.GetIdentityAsync(_shutdown.Token)); await RefreshLedgerAsync();
        });
        Closing += OnClosing;
        Closed += (_, _) => { _password.Clear(); _shutdown.Dispose(); };
    }
    private Button Button(string text, Func<Task> operation)
    {
        var button = new Button { Content = text, MinHeight = 36, Padding = new(14, 6, 14, 6), Margin = new(0, 6, 8, 6) };
        button.Click += async (_, _) => await RunOperationAsync(operation); _buttons.Add(button); return button;
    }
    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (_busy) return; _busy = true; foreach (var button in _buttons) button.IsEnabled = false;
        try { _operationTask = operation(); await _operationTask; }
        catch (OperationCanceledException) { _status.Text = "Operation cancelled."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or FormatException or ArgumentException)
        { _status.Text = ex is CryptographicException ? "The owner identity or backup could not be unlocked or verified. Check its password and original public fingerprint; no replacement identity was generated." : ex.Message; }
        finally { _busy = false; foreach (var button in _buttons) button.IsEnabled = true; }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_busy) return;
        e.Cancel = true; _shutdown.Cancel();
        try { await _operationTask; } catch (Exception) { /* RunOperationAsync reports the operation failure. */ }
        _allowClose = true; Close();
    }
    private void ShowIdentity(OwnerIdentityResult result)
    {
        var compatibility = IssuerReleaseCompatibility.Evaluate(_expectedTrustManifestJson, result);
        _status.Text = compatibility.Message;
        _fingerprint.Text = result.Fingerprint is { } id ? "Issuer public fingerprint: " + id : string.Empty;
    }
    private async Task<bool> EnsureCompatibleIssuerAsync()
    {
        var identity = await _issuer.GetIdentityAsync(_shutdown.Token);
        var compatibility = IssuerReleaseCompatibility.Evaluate(_expectedTrustManifestJson, identity);
        if (!compatibility.Compatible) { ShowIdentity(identity); return false; }
        return true;
    }

    private void ConfigureLedgerTheme()
    {
        // Scope these templates to the ledger: native DataGrid defaults otherwise mix a
        // white system surface with the window's inherited dark-theme text foreground.
        var resources = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="{x:Type DataGrid}">
                <Setter Property="Background" Value="{DynamicResource CardBackground}"/>
                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource ControlBorder}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="RowBackground" Value="{DynamicResource CardBackground}"/>
                <Setter Property="AlternatingRowBackground" Value="{DynamicResource RowAlternateBackground}"/>
                <Setter Property="HorizontalGridLinesBrush" Value="{DynamicResource BorderBrushLight}"/>
                <Setter Property="VerticalGridLinesBrush" Value="{DynamicResource BorderBrushLight}"/>
                <Setter Property="GridLinesVisibility" Value="Horizontal"/>
                <Setter Property="HeadersVisibility" Value="Column"/>
                <Setter Property="RowHeaderWidth" Value="0"/>
                <Setter Property="RowHeight" Value="36"/>
                <Setter Property="ColumnHeaderHeight" Value="36"/>
                <Setter Property="AlternationCount" Value="2"/>
                <Setter Property="SelectionMode" Value="Single"/>
                <Setter Property="SelectionUnit" Value="FullRow"/>
                <Style.Triggers>
                  <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Background" Value="{DynamicResource DisabledBackground}"/>
                    <Setter Property="Foreground" Value="{DynamicResource DisabledForeground}"/>
                  </Trigger>
                </Style.Triggers>
              </Style>
              <Style TargetType="{x:Type DataGridColumnHeader}">
                <Style.Resources>
                  <Style TargetType="{x:Type TextBlock}"><Setter Property="Foreground" Value="{Binding Foreground, RelativeSource={RelativeSource AncestorType={x:Type DataGridColumnHeader}}}"/></Style>
                </Style.Resources>
                <Setter Property="Background" Value="{DynamicResource HeaderBackground}"/>
                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource BorderBrushLight}"/>
                <Setter Property="BorderThickness" Value="0,0,1,1"/>
                <Setter Property="Padding" Value="8,0"/>
                <Setter Property="FontWeight" Value="SemiBold"/>
                <Setter Property="HorizontalContentAlignment" Value="Left"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="{x:Type DataGridColumnHeader}">
                      <Grid>
                        <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                          <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
                        </Border>
                        <Thumb x:Name="PART_LeftHeaderGripper" Width="5" HorizontalAlignment="Left" Cursor="SizeWE">
                          <Thumb.Template><ControlTemplate TargetType="{x:Type Thumb}"><Border Background="Transparent"/></ControlTemplate></Thumb.Template>
                        </Thumb>
                        <Thumb x:Name="PART_RightHeaderGripper" Width="5" HorizontalAlignment="Right" Cursor="SizeWE">
                          <Thumb.Template><ControlTemplate TargetType="{x:Type Thumb}"><Border Background="Transparent"/></ControlTemplate></Thumb.Template>
                        </Thumb>
                      </Grid>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
                <Style.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource HoverBackground}"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter Property="Background" Value="{DynamicResource SelectionBackground}"/><Setter Property="Foreground" Value="{DynamicResource SelectionForeground}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Background" Value="{DynamicResource DisabledBackground}"/><Setter Property="Foreground" Value="{DynamicResource DisabledForeground}"/></Trigger>
                </Style.Triggers>
              </Style>
              <Style TargetType="{x:Type DataGridRow}">
                <Setter Property="Background" Value="{DynamicResource CardBackground}"/>
                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                <Style.Triggers>
                  <Trigger Property="AlternationIndex" Value="1"><Setter Property="Background" Value="{DynamicResource RowAlternateBackground}"/></Trigger>
                  <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource HoverBackground}"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{DynamicResource SelectionBackground}"/><Setter Property="Foreground" Value="{DynamicResource SelectionForeground}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Background" Value="{DynamicResource DisabledBackground}"/><Setter Property="Foreground" Value="{DynamicResource DisabledForeground}"/></Trigger>
                </Style.Triggers>
              </Style>
              <Style TargetType="{x:Type DataGridCell}">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                <Setter Property="BorderBrush" Value="Transparent"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="8,4"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="{x:Type DataGridCell}">
                      <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                        <ContentPresenter VerticalAlignment="{TemplateBinding VerticalContentAlignment}" SnapsToDevicePixels="True"/>
                      </Border>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
                <Style.Triggers>
                  <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource BorderBrushFocus}"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{DynamicResource SelectionBackground}"/><Setter Property="Foreground" Value="{DynamicResource SelectionForeground}"/><Setter Property="BorderBrush" Value="{DynamicResource SelectionBorder}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Background" Value="{DynamicResource DisabledBackground}"/><Setter Property="Foreground" Value="{DynamicResource DisabledForeground}"/></Trigger>
                </Style.Triggers>
              </Style>
              <Style x:Key="OwnerLedgerText" TargetType="{x:Type TextBlock}">
                <Setter Property="Foreground" Value="{Binding Foreground, RelativeSource={RelativeSource AncestorType={x:Type DataGridCell}}}"/>
                <Setter Property="VerticalAlignment" Value="Center"/>
                <Setter Property="TextTrimming" Value="CharacterEllipsis"/>
              </Style>
            </ResourceDictionary>
            """);
        _ledger.Resources.MergedDictionaries.Add(resources);
        _ledger.Style = (Style)resources[typeof(DataGrid)];
    }

    private async Task RefreshLedgerAsync() => _ledger.ItemsSource = (await _issuer.GetLedgerAsync(_shutdown.Token)).Reverse().ToArray();
    private static void Heading(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new(0, 20, 0, 8) });
    private static void Label(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new(0, 8, 0, 6) });
}
