#if DEVELOPER_MODE
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using MacroSuite.App.ViewModels;
using Zenith.MacroHost.Execution;
using Zenith.MacroHost.Windows;

namespace MacroSuite.App;

public partial class App
{
    private ForegroundControlPipeServer? _foregroundServer;
    private SavedMacroPlanPreparer? _foregroundPreparer;
    private Task? _foregroundClose;

    private void EnableSessionsPairing(MainWindow window, MacroEditorViewModel editor)
    {
        var hash = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "ZenithSessionsPeerSha256")?.Value;
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)) return;
        var ownership = new ForegroundPeerOwnership(hash);
        window.EnableSessionsPairing(() => PairSessionsAsync(window, editor, ownership));
    }

    private async Task PairSessionsAsync(Window window, MacroEditorViewModel editor, ForegroundPeerOwnership ownership)
    {
        try
        {
            if (_foregroundServer is { Completion.IsCompleted: false } active)
            {
                if (MessageBox.Show(window, "Cancel this pairing and stop any macro it owns?", "Zenith Sessions",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                active.SignalStop();
                await CloseSessionsPairingAsync();
                return;
            }
            await CloseSessionsPairingAsync();
            _foregroundPreparer = new(editor.CompanionDocuments, RecordForegroundFault);
            var binding = editor.CreateForegroundBinding(_foregroundPreparer, ownership, RecordForegroundFault);
            _foregroundServer = new(binding.StartAsync);
            ShowPairingCode(window, _foregroundServer.TakePairingCode());
        }
        catch (Exception error)
        {
            RecordForegroundFault(error.GetType());
            try { await CloseSessionsPairingAsync(); }
            catch (Exception cleanup) { RecordForegroundFault(cleanup.GetType()); }
            MessageBox.Show(window, "Could not prepare Sessions pairing. Check that Personal is activated and try again.",
                "Zenith Sessions", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static void ShowPairingCode(Window owner, string code)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Copy this one-time code to Automation in the matching Zenith Sessions preview. It expires in two minutes.",
            TextWrapping = TextWrapping.Wrap
        });
        var field = new TextBox { Text = code, IsReadOnly = true, Margin = new Thickness(0, 12, 0, 12), FontSize = 16 };
        field.SetResourceReference(Control.BackgroundProperty, "PanelMaterial");
        field.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        field.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Sessions pairing code");
        panel.Children.Add(field);
        var close = new Button { Content = "Done", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
        panel.Children.Add(close);
        var dialog = new Window
        {
            Title = "Pair Zenith Sessions", Owner = owner, Content = panel, Width = 460,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "AppBackground");
        dialog.SetResourceReference(Window.ForegroundProperty, "TextPrimary");
        close.Click += (_, _) => dialog.Close();
        dialog.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        try { dialog.ShowDialog(); }
        finally { field.Clear(); }
    }

    private static void RecordForegroundFault(Type type)
        => System.Diagnostics.Trace.TraceWarning("The Personal foreground control endpoint reported {0}.", type.Name);

    private Task CloseSessionsPairingAsync()
    {
        if (_foregroundClose is { IsCompleted: false }) return _foregroundClose;
        var server = _foregroundServer;
        var preparer = _foregroundPreparer;
        _foregroundServer = null;
        _foregroundPreparer = null;
        return _foregroundClose = CloseSessionsResourcesAsync(server, preparer);
    }

    private static async Task CloseSessionsResourcesAsync(ForegroundControlPipeServer? server, SavedMacroPlanPreparer? preparer)
    {
        try { if (server is not null) await server.DisposeAsync().ConfigureAwait(false); }
        finally { if (preparer is not null) await preparer.DisposeAsync().ConfigureAwait(false); }
    }

    private void CloseSessionsPairing()
    {
        try { CloseSessionsPairingAsync().GetAwaiter().GetResult(); }
        catch (Exception error) { RecordForegroundFault(error.GetType()); }
    }
}
#endif
