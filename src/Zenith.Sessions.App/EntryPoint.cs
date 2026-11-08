using System.Diagnostics.CodeAnalysis;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

internal static class EntryPoint
{
    internal const string AlreadyStartingMessage = "Zenith Sessions is already starting. Select its taskbar or tray icon.";
    internal const string DifferentPermissionsMessage = "Another Zenith Sessions is running with different permissions (for example as administrator). Close it, then start Zenith Sessions normally.";
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
        if (!TryOpen(options.DataRoot, out var opened, out string? openProblem)) { System.Windows.MessageBox.Show(openProblem,"Zenith Sessions"); return 2; }
        using var instance = opened;
        if (!instance.IsOwner)
        {
            string? problem = RestoreOrExplain(instance);
            if (problem is not null) System.Windows.MessageBox.Show(problem,"Zenith Sessions");
            return problem == DifferentPermissionsMessage ? 2 : 0;
        }
        if (!options.SampleProfiles)
        {
            try
            {
                if (PendingReleaseUpdateHandoff.Offer(options.DataRoot, AppContext.BaseDirectory,
                    version => System.Windows.MessageBox.Show($"Version {version} was approved for the next start. Open the attended installer now? Zenith will close first; the installer will verify the release again.", "Zenith Sessions update", System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes,
                    start => { using var installer = System.Diagnostics.Process.Start(start) ?? throw new IOException("Installer could not start."); })) return 0;
            }
            catch (Exception error) { System.Windows.MessageBox.Show($"The pending update could not start ({error.GetType().Name}). Zenith will open normally; existing files were preserved.", "Zenith Sessions update"); }
        }
        var app = new App { Coordinator = instance,Options=options }; app.InitializeComponent();
        try { return app.Run(); }
        finally { instance.StopAsync().GetAwaiter().GetResult(); }
    }
    /// <summary>Opens this launch's single-instance coordinator, or gives the message to show when the data folder cannot be used.</summary>
    internal static bool TryOpen(string root, [NotNullWhen(true)] out SingleInstanceCoordinator? instance, [NotNullWhen(false)] out string? problem)
    {
        problem = null;
        // The coordinator creates, identifies and locks the data folder, so an unusable folder stops here with the type only (Rule 10).
        try { instance = new(root); return true; }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            instance = null;
            problem = $"Zenith Sessions could not open its data folder ({ex.GetType().Name}). Existing files were preserved. Check the data-folder access, then retry.";
            return false;
        }
    }
    /// <summary>Asks the running copy to show its window. Returns null once it was asked, otherwise the message to show.</summary>
    internal static string? RestoreOrExplain(SingleInstanceCoordinator instance)
    {
        if (instance.OwnerHasDifferentPermissions) return DifferentPermissionsMessage;
        try { return instance.RestoreExistingAsync(default).GetAwaiter().GetResult() ? null : AlreadyStartingMessage; }
        // The restore pipe admits only the owner's own token, so an elevated copy and a normal one refuse each other.
        catch (UnauthorizedAccessException) { return DifferentPermissionsMessage; }
    }
}
