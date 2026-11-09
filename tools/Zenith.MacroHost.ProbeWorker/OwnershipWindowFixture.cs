using System.Globalization;
using System.Runtime.InteropServices;

internal static class OwnershipWindowFixture
{
    public static int Run()
    {
        // A synthetic child can exercise the exact image-name gate without launching any game.
        if (Path.GetFileName(Environment.ProcessPath) != "RobloxPlayerBeta.exe") return 2;
        nint window = CreateWindowEx(0x08000080, "STATIC", "Zenith process-ownership fixture", 0x00CF0000,
            70, 70, 250, 150, 0, 0, 0, 0);
        if (window == 0) return 3;
        try
        {
            ShowWindow(window, 4); // SW_SHOWNOACTIVATE
            Console.WriteLine(window.ToInt64().ToString(CultureInfo.InvariantCulture));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return Console.In.ReadLineAsync(stop.Token).AsTask().GetAwaiter().GetResult() == "EXIT" ? 0 : 4;
        }
        finally { DestroyWindow(window); }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
