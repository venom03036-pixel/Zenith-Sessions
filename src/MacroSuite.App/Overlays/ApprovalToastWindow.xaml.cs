using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacroSuite.Core.AI;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Non-focus-stealing, topmost toast window presenting AI actions for user approval (Rule 8).
/// </summary>
public partial class ApprovalToastWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly TaskCompletionSource<ApprovalDecision> _tcs = new();
    private readonly DispatcherTimer _timer = new();
    private DateTime _expireTime;
    private TimeSpan _totalTimeout;

    public ApprovalToastWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Apply WS_EX_NOACTIVATE so toast window never steals keyboard/window focus
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// Displays the approval toast, counts down, and waits for user decision or timeout.
    /// </summary>
    public async Task<ApprovalDecision> WaitForDecisionAsync(ApprovalContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        ActionSummaryText.Text = context.ActionSummary;
        ReasoningText.Text = !string.IsNullOrWhiteSpace(context.Reasoning) ? context.Reasoning : "No model reasoning provided.";

        if (context.ScreenshotThumbnail != null && context.ScreenshotThumbnail.Length > 0)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(context.ScreenshotThumbnail);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                ThumbnailImage.Source = bmp;
                ThumbnailBorder.Visibility = Visibility.Visible;
            }
            catch
            {
                ThumbnailBorder.Visibility = Visibility.Collapsed;
            }
        }

        // Position window: bottom right or near region
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - 360;
        Top = workArea.Bottom - 300;

        _totalTimeout = context.Timeout;
        _expireTime = DateTime.UtcNow + _totalTimeout;
        TimeoutProgress.Maximum = _totalTimeout.TotalMilliseconds;
        TimeoutProgress.Value = _totalTimeout.TotalMilliseconds;

        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (s, e) =>
        {
            var remaining = _expireTime - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                _timer.Stop();
                _tcs.TrySetResult(ApprovalDecision.TimedOut);
                Close();
            }
            else
            {
                TimeoutProgress.Value = Math.Max(0, remaining.TotalMilliseconds);
                CountdownText.Text = $"{Math.Ceiling(remaining.TotalSeconds)}s";
            }
        };

        OverlayWindow.Attach(this, owner: null);
        Show();
        // Re-assert z-order so the toast sits above Always-On-Top / mini-mode chrome.
        Topmost = false;
        Topmost = true;
        _timer.Start();

        using (ct.Register(() =>
        {
            _timer.Stop();
            _tcs.TrySetResult(ApprovalDecision.Rejected);
            Dispatcher.Invoke(Close);
        }))
        {
            return await _tcs.Task.ConfigureAwait(true);
        }
    }

    private void OnApproveClicked(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _tcs.TrySetResult(ApprovalDecision.Approved);
        Close();
    }

    private void OnRejectClicked(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _tcs.TrySetResult(ApprovalDecision.Rejected);
        Close();
    }
}
