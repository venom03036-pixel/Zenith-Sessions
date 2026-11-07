using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Topmost, click-through warning banner displayed whenever autonomous mode is running (Rule 8).
/// </summary>
public partial class AutonomousBanner : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public AutonomousBanner()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position at top center of primary monitor
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2.0;
        Top = 0;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Set transparent click-through and no-activate styles
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    }
}
