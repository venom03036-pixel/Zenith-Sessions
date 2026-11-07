using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacroSuite.App.Themes;

/// <summary>A bounded 192px HSV wheel with keyboard alternatives and a separate value slider.</summary>
public sealed class ColorWheel : FrameworkElement
{
    public static readonly DependencyProperty HexColorProperty = DependencyProperty.Register(nameof(HexColor), typeof(string), typeof(ColorWheel),
        new FrameworkPropertyMetadata("#83D8C1", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(nameof(Brightness), typeof(double), typeof(ColorWheel),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, BrightnessChanged, (_, value) => double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 1) : 1d));
    public static readonly DependencyProperty FocusIndicatorBrushProperty = DependencyProperty.Register(nameof(FocusIndicatorBrush), typeof(Brush), typeof(ColorWheel),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly BitmapSource Wheel = MakeWheel();
    private bool _updating;
    private double _hue, _saturation;
    private long _lastMove;
    public ColorWheel() { Focusable = true; Cursor = Cursors.Cross; MinWidth = 160; MinHeight = 160; SetResourceReference(FocusIndicatorBrushProperty, "BorderBrushFocus"); ToHsv(AppearancePalette.Parse(HexColor), out _hue, out _saturation, out double value); SetCurrentValue(BrightnessProperty, value); }
    public string HexColor { get => (string)GetValue(HexColorProperty); set => SetValue(HexColorProperty, value); }
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public Brush FocusIndicatorBrush { get => (Brush)GetValue(FocusIndicatorBrushProperty); set => SetValue(FocusIndicatorBrushProperty, value); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var wheel = (ColorWheel)d;
        if (!wheel._updating && AppearancePalette.TryParse(e.NewValue as string))
        {
            ToHsv(AppearancePalette.Parse((string)e.NewValue), out wheel._hue, out wheel._saturation, out double value);
            wheel._updating = true; wheel.SetCurrentValue(BrightnessProperty, value); wheel._updating = false;
        }
        wheel.InvalidateVisual();
    }
    private static void BrightnessChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var wheel = (ColorWheel)d; if (!wheel._updating) wheel.Publish();
    }
    protected override void OnRender(DrawingContext dc)
    {
        double side = Math.Min(ActualWidth, ActualHeight); var rectangle = new Rect((ActualWidth - side) / 2, (ActualHeight - side) / 2, side, side);
        dc.DrawImage(Wheel, rectangle);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)((1 - Brightness) * 255), 0, 0, 0)), null,
            new Point(ActualWidth / 2, ActualHeight / 2), side / 2, side / 2);
        double angle = _hue * Math.PI / 180, radius = _saturation * side / 2;
        var selected = new Point(ActualWidth / 2 + Math.Cos(angle) * radius, ActualHeight / 2 + Math.Sin(angle) * radius);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 4), selected, 6, 6);
        dc.DrawEllipse(null, new Pen(Brushes.White, 2), selected, 6, 6);
        if (IsKeyboardFocused) dc.DrawRectangle(null, new Pen(FocusIndicatorBrush, 2), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); Choose(e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        long now = Stopwatch.GetTimestamp(); if (Stopwatch.GetElapsedTime(_lastMove, now).TotalMilliseconds < 50) return;
        _lastMove = now; Choose(e.GetPosition(this));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (IsMouseCaptured) { Choose(e.GetPosition(this)); ReleaseMouseCapture(); e.Handled = true; } }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key) { case Key.Left: _hue = (_hue + 355) % 360; break; case Key.Right: _hue = (_hue + 5) % 360; break; case Key.Up: _saturation = Math.Min(1, _saturation + .05); break; case Key.Down: _saturation = Math.Max(0, _saturation - .05); break; default: base.OnKeyDown(e); return; }
        Publish(); e.Handled = true;
    }
    private void Choose(Point point)
    {
        double x = point.X - ActualWidth / 2, y = point.Y - ActualHeight / 2;
        _hue = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        _saturation = Math.Clamp(Math.Sqrt(x * x + y * y) / (Math.Min(ActualWidth, ActualHeight) / 2), 0, 1); Publish();
    }
    private void Publish() { _updating = true; SetCurrentValue(HexColorProperty, AppearancePalette.Hex(FromHsv(_hue, _saturation, Brightness))); _updating = false; InvalidateVisual(); }
    public static Color FromHsv(double hue, double saturation, double value)
    {
        double h = (hue % 360 + 360) % 360 / 60, c = value * saturation, x = c * (1 - Math.Abs(h % 2 - 1)), m = value - c;
        (double r, double g, double b) = h switch { < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x), < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
    private static void ToHsv(Color color, out double hue, out double saturation, out double value)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        value = max; saturation = max == 0 ? 0 : d / max;
        hue = d == 0 ? 0 : 60 * (max == r ? ((g - b) / d + 6) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4);
    }
    private static BitmapSource MakeWheel()
    {
        const int size = 192; byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double dx = x - size / 2d, dy = y - size / 2d, saturation = Math.Sqrt(dx * dx + dy * dy) / (size / 2d);
            if (saturation > 1) continue;
            Color color = FromHsv(Math.Atan2(dy, dx) * 180 / Math.PI, saturation, 1); int i = (y * size + x) * 4;
            pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4); bitmap.Freeze(); return bitmap;
    }
}
