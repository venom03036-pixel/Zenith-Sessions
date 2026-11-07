using System.Windows;
using MacroSuite.App.Themes;

namespace Zenith.Sessions.App;

public partial class App : Application
{
    public Zenith.Sessions.Windows.SingleInstanceCoordinator? Coordinator { get; init; }
    public StartupOptions Options { get; init; }=new(StartupOptions.DefaultRoot,false);
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); ShutdownMode = ShutdownMode.OnMainWindowClose;
        // The isolated packaged composition check pumps WPF's dispatcher to resolve
        // bindings. Its Application must never run the ordinary visible startup path.
        if (Options.SelfCheck) { ShutdownMode = ShutdownMode.OnExplicitShutdown; return; }
        ShellViewModel? vm=null;
        try
        {
            ThemeManager.Apply(Resources, ThemeCatalog.DefaultId);
            vm = new ShellViewModel(Options.DataRoot, Options.SampleProfiles, Dispatcher,enableAutomaticPublicImages:!Options.SampleProfiles);
            MainWindow = new MainWindow(vm); MainWindow.Show();
            Coordinator?.Listen(ct => Dispatcher.InvokeAsync(() => ((MainWindow)MainWindow).RestoreFromLaunch(), System.Windows.Threading.DispatcherPriority.Normal, ct).Task);
        }
        catch(Exception ex)
        {
            MessageBox.Show($"Zenith Sessions could not open its local workspace ({ex.GetType().Name}). Existing files were preserved. Check the app files and data-folder access, then retry.","Zenith Sessions");
            if(vm is not null) await vm.DisposeAsync();
            Shutdown(2);
        }
    }
}
