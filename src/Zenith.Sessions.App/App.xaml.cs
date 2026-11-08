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
    // Well inside the roughly 5 s Windows gives an app to answer a session end before it reports the app as hung.
    private static readonly TimeSpan SessionEndFlushBudget=TimeSpan.FromSeconds(2);
    /// <summary>Keeps by type only (Rule 10) a fault caught during exit that has nowhere else to go, and flushes the log.</summary>
    internal void RecordFault(Type errorType)=>_guard?.Record(errorType);
    /// <summary>
    /// Windows sign-out, restart or shutdown. WPF calls Shutdown right after this and closes every window without the
    /// tray Exit path, so stop the workspace and flush its Shutdown record here. The session end is never cancelled.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if(e.Cancel || _shell is not {} shell) return;
        try { shell.PrepareForSessionEnd(SessionEndFlushBudget); }
        // A fault escaping here would skip WPF's Shutdown call that follows, so keep it by type only (Rule 10) and let the session end.
        catch(Exception ex) { _guard?.Record(ex is AggregateException {InnerExceptions.Count:1} single ? single.InnerExceptions[0].GetType() : ex.GetType()); }
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); ShutdownMode = ShutdownMode.OnMainWindowClose;
        // The isolated packaged composition check pumps WPF's dispatcher to resolve
        // bindings. Its Application must never run the ordinary visible startup path.
        if (Options.SelfCheck) { ShutdownMode = ShutdownMode.OnExplicitShutdown; return; }
        // Attached before the workspace exists, so startup, running and exit faults are all recorded by type only.
        var guard=_guard=UnhandledExceptionGuard.Attach(Dispatcher,Options.DataRoot,()=>_shell);
        await OpenWorkspaceAsync(()=>
        {
            ApplyStartupTheme(Resources, Options.DataRoot);
            _shell = new ShellViewModel(Options.DataRoot, Options.SampleProfiles, Dispatcher,enableAutomaticPublicImages:!Options.SampleProfiles);
            MainWindow = new MainWindow(_shell); MainWindow.Show();
            ListenForRepeatLaunches(Coordinator,_shell,ct => Dispatcher.InvokeAsync(() => ((MainWindow)MainWindow).RestoreFromLaunch(), System.Windows.Threading.DispatcherPriority.Normal, ct).Task);
        },()=>_shell,guard.Record,text=>MessageBox.Show(text,"Zenith Sessions"),Shutdown);
    }
    /// <summary>
    /// Starts the owner's restore listener, so a later launch shows this window. A fault it survives or stops on reaches the
    /// shell's Diagnostics by type only (Rule 10) instead of leaving every later "already starting" message unexplained.
    /// </summary>
    internal static void ListenForRepeatLaunches(Zenith.Sessions.Windows.SingleInstanceCoordinator? coordinator,ShellViewModel shell,Func<CancellationToken,Task> restore)=>
        coordinator?.Listen(restore,shell.RestoreListenerFaulted);
    /// <summary>
    /// The visible startup and its failure path, apart from the Application object (one per process) so tests can run both.
    /// <paramref name="open"/> builds the workspace and shows its window; <paramref name="workspace"/> returns whatever part of
    /// it exists. A fault is recorded by type only (Rule 10), shown with <paramref name="reportFailure"/> and ends the app with code 2.
    /// </summary>
    internal static async Task OpenWorkspaceAsync(Action open,Func<IAsyncDisposable?> workspace,Action<Type> record,Action<string> reportFailure,Action<int> shutdown)
    {
        try { open(); }
        catch(Exception ex)
        {
            record(ex.GetType());
            // Stop before telling the user: the message box's modal loop runs the shown window's Loaded event, which would
            // otherwise open the workspace (credential cleanup, queue recovery, monitoring) behind a message that says it did not.
            // The shell cancels all of its work synchronously when its disposal starts; the rest is awaited after the message.
            Task stopping=workspace() is {} started ? StopAsync(started) : Task.CompletedTask;
            reportFailure(StartupFailureMessage(ex.GetType()));
            try { await stopping; }
            // Shutdown(2) below must still run; the teardown fault is kept by type next to the startup fault.
            catch(Exception disposeError) { record(disposeError.GetType()); }
            shutdown(2);
        }
        static async Task StopAsync(IAsyncDisposable started)=>await started.DisposeAsync();
    }
    /// <summary>
    /// UI thread, before the manager window is created: the theme saved in <paramref name="root"/>, as Windows currently shows it
    /// (Follow Windows and High Contrast), so the first frame does not flash the default palette. A missing, damaged or unreadable
    /// file keeps the default without delay; the settings load that follows the window's Loaded event applies the theme it reads
    /// (for example from the backup) and reports any problem.
    /// </summary>
    internal static void ApplyStartupTheme(ResourceDictionary resources,string root)=>ApplyStartupTheme(resources,root,SystemAppearance.Read());
    internal static void ApplyStartupTheme(ResourceDictionary resources,string root,SystemAppearance system)=>
        ThemeManager.Apply(resources,SessionsThemes.Effective(Zenith.Sessions.Windows.SettingsStore.ReadTheme(root),system));
    internal static string StartupFailureMessage(Type error)=>
        $"Zenith Sessions could not open its local workspace ({error.Name}). Existing files were preserved. Check the app files and data-folder access, then retry.";
}
