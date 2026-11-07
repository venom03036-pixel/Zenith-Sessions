using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacroSuite.App.Authoring;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MouseButton = System.Windows.Input.MouseButton;

namespace MacroSuite.App.Overlays;

/// <summary>A modal coordinate picker with a bounded, nonauthoritative color preview.</summary>
public partial class ColorPickerOverlay : Window
{
    private readonly CancellationToken _stop;
    private readonly ColorHoverPreview _preview;
    private readonly SolidColorBrush _swatchBrush = new();
    private ScreenPoint? _selectedPoint;
    private long _lastLoupeMove;
    private bool _loupePositioned;

    private ColorPickerOverlay(Func<ScreenPoint, CancellationToken, Task<AuthoringPixelSample>> preview, CancellationToken ct)
    {
        _stop = ct;
        InitializeComponent();
        _preview = new(preview, PublishPreview, ct, closeOnFailure: Close);
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Keyboard.Focus(OverlayCanvas);
        };
        Closed += (_, _) => _preview.Stop();
    }

    internal static async Task<ScreenPoint?> PromptPointAsync(Window? owner,
        Func<ScreenPoint, CancellationToken, Task<AuthoringPixelSample>> preview, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var overlay = new ColorPickerOverlay(preview, ct);
        OverlayWindow.Attach(overlay, owner);
        bool? accepted;
        try { accepted = AuthoringModalCancellation.ShowDialog(overlay, ct); }
        finally
        {
            // A returned point is only an intent. The Editor owns the final sample
            // and starts it after this actual hover request and the overlay have ended.
            overlay._preview.Stop();
            await overlay._preview.Completion;
        }
        return accepted == true && !ct.IsCancellationRequested ? overlay._selectedPoint : null;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(OverlayCanvas);
        if (!TryGetPhysicalPoint(PointToScreen(pos), out var point)) return;
        bool canPreview = !ContainsChrome(LoupeCard, pos) && !ContainsChrome(InstructionBanner, pos);
        long now = Environment.TickCount64;
        if (!_loupePositioned || now - _lastLoupeMove >= ColorHoverPreview.SampleIntervalMs)
        {
            _loupePositioned = true;
            _lastLoupeMove = now;
            MoveLoupe(pos);
        }
        _preview.Observe(point, canPreview);
    }

    private static bool ContainsChrome(FrameworkElement element, Point point)
    {
        double left = Canvas.GetLeft(element), top = Canvas.GetTop(element);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        return new Rect(left, top, element.ActualWidth, element.ActualHeight).Contains(point);
    }

    private void MoveLoupe(Point pos)
    {
        double left = pos.X + 24, top = pos.Y + 24;
        if (left + LoupeCard.Width > ActualWidth) left = pos.X - LoupeCard.Width - 24;
        if (top + LoupeCard.ActualHeight > ActualHeight) top = pos.Y - LoupeCard.ActualHeight - 24;
        Canvas.SetLeft(LoupeCard, Math.Max(10, left));
        Canvas.SetTop(LoupeCard, Math.Max(10, top));
    }

    private void PublishPreview(AuthoringPixelSample? sample)
    {
        if (sample is not { } color)
        {
            ColorSwatch.Background = Brushes.Transparent;
            HexText.Text = "—";
            RgbText.Text = "Preview unavailable";
            CoordsText.Text = string.Empty;
            return;
        }
        _swatchBrush.Color = Color.FromRgb(color.R, color.G, color.B);
        if (!ReferenceEquals(ColorSwatch.Background, _swatchBrush)) ColorSwatch.Background = _swatchBrush;
        HexText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        RgbText.Text = $"Preview RGB({color.R}, {color.G}, {color.B})";
        CoordsText.Text = $"X: {color.X}, Y: {color.Y}";
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        e.Handled = true;
        if (_stop.IsCancellationRequested) { Close(); return; }
        if (!TryGetPhysicalPoint(PointToScreen(e.GetPosition(OverlayCanvas)), out var clicked)) return;
        _selectedPoint = clicked;
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.SystemKey == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    internal static bool TryGetPhysicalPoint(Point screen, out ScreenPoint point)
    {
        point = default;
        double x = Math.Round(screen.X), y = Math.Round(screen.Y);
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || x is < int.MinValue or > int.MaxValue || y is < int.MinValue or > int.MaxValue) return false;
        point = new((int)x, (int)y);
        return true;
    }
}

/// <summary>One dispatcher-owned actual sample and one latest pointer observation. No timer.</summary>
internal sealed class ColorHoverPreview
{
    internal const int SampleIntervalMs = 33;
    private readonly Func<ScreenPoint, CancellationToken, Task<AuthoringPixelSample>> _sample;
    private readonly Action<AuthoringPixelSample?> _publish;
    private readonly CancellationToken _stop;
    private readonly Func<long> _clock;
    private readonly Action? _closeOnFailure;
    private Task _work = Task.CompletedTask;
    private ScreenPoint _latest;
    private long _sequence;
    private long _sampledSequence;
    private long _lastStart;
    private bool _hasLatest, _hasStarted, _canSample, _hasPreview, _closed;

    internal ColorHoverPreview(Func<ScreenPoint, CancellationToken, Task<AuthoringPixelSample>> sample,
        Action<AuthoringPixelSample?> publish, CancellationToken stop, Func<long>? clock = null, Action? closeOnFailure = null)
    {
        _sample = sample;
        _publish = publish;
        _stop = stop;
        _clock = clock ?? (static () => Environment.TickCount64);
        _closeOnFailure = closeOnFailure;
    }

    internal Task Completion => _work;

    internal void Observe(ScreenPoint point, bool canSample = true)
    {
        if (_closed || _stop.IsCancellationRequested) return;
        if (!_hasLatest || point != _latest || canSample != _canSample)
        {
            _latest = point;
            _hasLatest = true;
            _canSample = canSample;
            _sequence++;
            if (_hasPreview)
            {
                _hasPreview = false;
                _publish(null);
            }
        }
        if (_work.IsCompleted && CanStart()) _work = RunAsync();
    }

    internal void Stop() => _closed = true;

    private bool CanStart()
    {
        if (_closed || _stop.IsCancellationRequested || !_hasLatest || !_canSample || _sampledSequence == _sequence) return false;
        return !_hasStarted || _clock() - _lastStart >= SampleIntervalMs;
    }

    private async Task RunAsync()
    {
        await Task.Yield(); // The real work is published before invoking the sample callback.
        try
        {
            while (CanStart())
            {
                ScreenPoint point = _latest;
                long sequence = _sequence;
                _hasStarted = true;
                _lastStart = _clock();
                _sampledSequence = sequence;

                AuthoringPixelSample? preview;
                try
                {
                    var sample = await _sample(point, _stop);
                    preview = sample.X == point.X && sample.Y == point.Y ? sample : null;
                }
                catch (OperationCanceledException) when (_closed || _stop.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not HostInputOperationCleanupException)
                {
                    // An unavailable hover must clear the old swatch. The final clicked
                    // pixel remains an independent request owned by the Editor.
                    preview = null;
                }

                if (_closed || _stop.IsCancellationRequested || sequence != _sequence) continue;
                _hasPreview = preview.HasValue;
                _publish(preview);
            }
        }
        catch (Exception)
        {
            Stop();
            try { _closeOnFailure?.Invoke(); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("Color preview close failed: {0}", error.GetType().Name);
                throw new HostInputOperationCleanupException();
            }
            throw;
        }
    }
}

/// <summary>Desktop pixel sample in physical screen coordinates.</summary>
public sealed record ColorPickResult(System.Drawing.Color Color, int X, int Y);
