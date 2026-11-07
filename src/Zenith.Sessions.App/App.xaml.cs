using System.Windows;
using MacroSuite.App.Themes;

namespace Zenith.Sessions.App;

public partial class App : Application
{
    // Owned for the whole process (never disposed) so a fault in EntryPoint's own teardown is still recorded.
    private UnhandledExceptionGuard? _guard;
    private ShellViewModel? _shell;
    public Zenith.Sessions.Windows.SingleInstanceCoordinator? Coordinator { get; init; }
    public StartupOptions Options { get; init; }=new(StartupOptions.DefaultRoot,false);
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); ShutdownMode = ShutdownMode.OnMainWindowClose;
        // The isolated packaged composition check pumps WPF's dispatcher to resolve
        // bindings. Its Application must never run the ordinary visible startup path.
        if (Options.SelfCheck) { ShutdownMode = ShutdownMode.OnExplicitShutdown; return; }
        // Attached before the workspace exists, so startup, running and exit faults are all recorded by type only.
        var guard=_guard=UnhandledExceptionGuard.Attach(Dispatcher,Options.DataRoot,()=>_shell);
        try
        {
            ThemeManager.Apply(Resources, ThemeCatalog.DefaultId);
            _shell = new ShellViewModel(Options.DataRoot, Options.SampleProfiles, Dispatcher,enableAutomaticPublicImages:!Options.SampleProfiles);
            MainWindow = new MainWindow(_shell); MainWindow.Show();
            Coordinator?.Listen(ct => Dispatcher.InvokeAsync(() => ((MainWindow)MainWindow).RestoreFromLaunch(), System.Windows.Threading.DispatcherPriority.Normal, ct).Task);
        }
        catch(Exception ex)
        {
            guard.Record(ex.GetType());
            MessageBox.Show($"Zenith Sessions could not open its local workspace ({ex.GetType().Name}). Existing files were preserved. Check the app files and data-folder access, then retry.","Zenith Sessions");
            if(_shell is not null)
                try { await _shell.DisposeAsync(); }
                // Shutdown(2) below must still run; the teardown fault is kept by type next to the startup fault.
                catch(Exception disposeError) { guard.Record(disposeError.GetType()); }
            Shutdown(2);
        }
    }
}
