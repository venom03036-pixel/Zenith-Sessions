using System.Windows;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Keeps modal overlays visible above an Always-On-Top main window.
/// Without an owner and Topmost, ShowDialog freezes the studio while the dialog sits behind it.
/// </summary>
internal static class OverlayWindow
{
    public static void Attach(Window overlay, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        owner ??= Application.Current?.MainWindow;
        if (owner != null
            && !ReferenceEquals(overlay, owner)
            && owner.IsVisible
            && owner.WindowState != WindowState.Minimized
            && owner.Visibility == Visibility.Visible)
        {
            try
            {
                overlay.Owner = owner;
            }
            catch (InvalidOperationException)
            {
                // Owner may already be set or in a closing state.
            }
        }

        overlay.Topmost = true;
        overlay.ShowInTaskbar = false;
        if (overlay.Owner == null)
            overlay.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    public static void DetachOwner(Window overlay)
    {
        try
        {
            overlay.Owner = null;
        }
        catch (InvalidOperationException)
        {
            // Ignore: the window may already be closing.
        }
    }
}
