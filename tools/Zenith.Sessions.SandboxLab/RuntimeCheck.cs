using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// A local, credential-free rendering check. Does not run RoALT or access accounts.
internal sealed class RuntimeCheck : Form
{
    private readonly WebView2 browser = new WebView2();
    private readonly Timer deadline = new Timer();
    private string version = "unknown";
    private bool ready;
    private readonly DateTime started = DateTime.UtcNow;
    [STAThread]
    private static void Main()
    {
        if (!string.Equals(Environment.UserName, "WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase)) return;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new RuntimeCheck());
    }
    private RuntimeCheck()
    {
        Text = "Zenith Sandbox — WebView2 check";
        Width = 700; Height = 440; StartPosition = FormStartPosition.CenterScreen;
        browser.Dock = DockStyle.Fill; Controls.Add(browser);
        deadline.Interval = 60000;
        deadline.Tick += delegate { if (!ready) { Record("Timed out", null); Close(); } };
        Shown += BeginCheck;
        FormClosed += delegate { deadline.Stop(); deadline.Dispose(); browser.Dispose(); };
    }
    private async void BeginCheck(object sender, EventArgs args)
    {
        deadline.Start();
        try
        {
            // Also verify discovery via the guest's SDK registry override,
            // independent of this process inheriting an environment variable.
            Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", null);
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Record("Creating environment", null);
            var environment = await CoreWebView2Environment.CreateAsync(null, @"C:\ZenithLab\CheckData", null);
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.WebMessageReceived += delegate(object s, CoreWebView2WebMessageReceivedEventArgs e)
            {
                if (e.TryGetWebMessageAsString() != "zenith-webview-ready") return;
                ready = true; deadline.Stop(); Record("Ready", null);
                Text = "Zenith Sandbox — WebView2 is working";
            };
            browser.CoreWebView2.NavigateToString("<!doctype html><html><meta charset='utf-8'><style>body{background:#111a20;color:#e8eff2;font:18px Segoe UI;margin:48px}h1{color:#80d5c0;font-size:30px}p{line-height:1.6}</style><h1>WebView2 is working</h1><p>The shared Microsoft runtime rendered this page and ran its script successfully.</p><p>10 GB Sandbox · Local runtime · No account data shared</p><script>window.chrome.webview.postMessage('zenith-webview-ready')</script></html>");
        }
        catch (Exception error)
        {
            Record("Failed", error);
            Text = "Zenith Sandbox — Runtime check failed";
            browser.Visible = false;
            Controls.Add(new Label { Text = "Runtime check failed: " + error.GetType().Name + " (0x" + error.HResult.ToString("X8") + ")", Dock = DockStyle.Fill, Padding = new Padding(24) });
        }
    }
    private void Record(string stage, Exception error)
    {
        Directory.CreateDirectory(@"C:\ZenithLab");
        string value = "Stage=" + stage + "\r\nVersion=" + version + "\r\nRendered=" + ready + "\r\nElapsedMs=" + (long)(DateTime.UtcNow-started).TotalMilliseconds + "\r\n";
        if (error != null) value += "ErrorType=" + error.GetType().Name + "\r\nHResult=0x" + error.HResult.ToString("X8") + "\r\n";
        string path = @"C:\ZenithLab\RuntimeCheck.txt";
        File.WriteAllText(path + ".tmp", value);
        if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
    }
}
