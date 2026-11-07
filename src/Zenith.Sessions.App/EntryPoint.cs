namespace Zenith.Sessions.App;

internal static class EntryPoint
{
    [STAThread]
    public static int Main()
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        StartupOptions options;
        try { options=StartupOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray()); }
        catch(ArgumentException ex) { System.Windows.MessageBox.Show(ex.Message,"Zenith Sessions"); return 2; }
        if(options.SelfCheck)
        {
            try { return DistributionSelfCheck.RunAsync(options.DataRoot).GetAwaiter().GetResult(); }
            catch(Exception ex) { Console.Error.WriteLine($"Distribution self-check could not finish ({ex.GetType().Name})."); return 2; }
        }
        Zenith.Sessions.Windows.SingleInstanceCoordinator coordinator;
        // The coordinator now creates, identifies and locks the data folder, so an unusable folder stops here with the type only (Rule 10).
        try { coordinator=new(options.DataRoot); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show($"Zenith Sessions could not open its data folder ({ex.GetType().Name}). Existing files were preserved. Check the data-folder access, then retry.","Zenith Sessions");
            return 2;
        }
        using var instance = coordinator;
        if (!instance.IsOwner)
        {
            if (!instance.RestoreExistingAsync(default).GetAwaiter().GetResult()) System.Windows.MessageBox.Show("Zenith Sessions is already starting. Select its taskbar or tray icon.","Zenith Sessions");
            return 0;
        }
        var app = new App { Coordinator = instance,Options=options }; app.InitializeComponent();
        try { return app.Run(); }
        finally { instance.StopAsync().GetAwaiter().GetResult(); }
    }
}
