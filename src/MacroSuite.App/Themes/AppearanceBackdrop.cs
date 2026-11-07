using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MacroSuite.App.ViewModels;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.App.Themes;

/// <summary>One wallpaper owner per shell. Effects apply only to pixels behind the controls.</summary>
public sealed class AppearanceBackdrop : Grid
{
    public static readonly DependencyProperty AppearanceProperty = DependencyProperty.Register(
        nameof(Appearance), typeof(AppearanceViewModel), typeof(AppearanceBackdrop),
        new PropertyMetadata(null, (d, e) => ((AppearanceBackdrop)d).AppearanceChanged((AppearanceViewModel?)e.OldValue)));
    public AppearanceViewModel? Appearance { get => (AppearanceViewModel?)GetValue(AppearanceProperty); set => SetValue(AppearanceProperty, value); }
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly Border _dim = new() { Background = Brushes.Black, IsHitTestVisible = false };
    private readonly DispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly BlurEffect _blur = new() { RenderingBias = RenderingBias.Performance, KernelType = KernelType.Gaussian };
    private Window? _owner;
    private AppearanceAsset? _asset;
    private int _frame;
    private double _duration;
    private bool _attached, _acrylic;
    private bool? _lastDesktopRequest;
    private bool _lastDark;
    private WindowInteractionGuard? _interaction;

    public AppearanceBackdrop()
    {
        IsHitTestVisible = false; ClipToBounds = true;
        Children.Add(_image); Children.Add(_dim);
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality);
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _timer.Tick += AdvanceFrame;
        Loaded += Attach; Unloaded += Detach;
    }
    private void AppearanceChanged(AppearanceViewModel? old)
    {
        if (_attached) { if (old is not null) old.Changed -= Refresh; if (Appearance is not null) Appearance.Changed += Refresh; }
        Refresh();
    }
    private void Attach(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true; _owner = Window.GetWindow(this);
        if (Appearance is not null) Appearance.Changed += Refresh;
        if (_owner is not null)
        {
            _interaction = new WindowInteractionGuard(_owner, Refresh);
            _owner.Activated += WindowChanged; _owner.Deactivated += WindowChanged;
            _owner.StateChanged += WindowChanged; _owner.SourceInitialized += WindowChanged;
        }
        SystemParameters.StaticPropertyChanged += SystemSettingsChanged;
        Refresh();
    }
    private void Detach(object sender, RoutedEventArgs e)
    {
        _interaction?.Dispose(); _interaction = null;
        _timer.Stop(); _clock.Reset(); _image.Source = null; _image.Effect = null; _asset = null;
        if (!_attached) return;
        _attached = false;
        if (Appearance is not null) Appearance.Changed -= Refresh;
        SystemParameters.StaticPropertyChanged -= SystemSettingsChanged;
        if (_owner is not null)
        {
            _owner.Activated -= WindowChanged; _owner.Deactivated -= WindowChanged;
            _owner.StateChanged -= WindowChanged; _owner.SourceInitialized -= WindowChanged;
            if (_acrylic) DesktopBackdropController.TryApply(new WindowInteropHelper(_owner).Handle, false, true, out _);
        }
        _owner = null; _lastDesktopRequest = null; _acrylic = false;
    }
    private void WindowChanged(object? sender, EventArgs e) => Refresh();
    private void SystemSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Refresh);
    }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Refresh); return; }
        var appearance = Appearance;
        if (appearance is null)
        {
            _timer.Stop(); _clock.Reset(); _image.Source = null; _image.Effect = null; _asset = null;
            return;
        }
        var profile = appearance.CurrentProfile;
        bool hardware = (RenderCapability.Tier >> 16) > 0;
        bool interacting = _interaction?.IsInteracting == true;
        bool reduce = interacting || appearance.RuntimeReducedEffects || !hardware || SystemParameters.HighContrast;
        bool desktopRequested = profile.BackgroundKind == AppearanceBackgroundKind.DesktopAcrylic && !reduce;
        bool dark = AppearancePalette.Luminance(appearance.CurrentPalette.Colors["AppBackground"]) < .35;
        nint handle = _owner is null ? 0 : new WindowInteropHelper(_owner).Handle;
        if (_owner is not null && handle != 0 && (_lastDesktopRequest != desktopRequested || _lastDark != dark))
        {
            _lastDesktopRequest = desktopRequested; _lastDark = dark;
            _acrylic = DesktopBackdropController.TryApply(handle, desktopRequested, dark, out string? reason);
            if (reason is not null) appearance.ReportBackdropStatus(reason);
            _owner.SetResourceReference(Window.BackgroundProperty, "AppBackground");
            if (_acrylic) _owner.Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                target.BackgroundColor = _acrylic ? Colors.Transparent : appearance.CurrentPalette.Colors["AppBackground"];
        }
        Background = _acrylic ? Brushes.Transparent : new SolidColorBrush(appearance.CurrentPalette.Colors["AppBackground"]);
        var nextAsset = profile.BackgroundKind == AppearanceBackgroundKind.Image ? appearance.Asset : null;
        if (!ReferenceEquals(_asset, nextAsset))
        {
            _timer.Stop(); _clock.Reset(); _asset = nextAsset; _frame = 0;
            _duration = _asset?.Frames.Sum(frame => frame.Duration.TotalMilliseconds) ?? 1;
            _image.Source = _asset?.Frames.FirstOrDefault()?.Image;
        }
        _dim.Opacity = _asset is null ? 0 : profile.Dim;
        _blur.Radius = reduce ? 0 : profile.BlurRadius;
        _image.Effect = _asset is not null && _blur.Radius > 0 ? _blur : null;
        bool animate = CanAnimate(appearance.ShouldAnimate && !interacting, _asset?.Frames.Count ?? 0,
            _attached && _owner?.IsActive == true, _owner?.WindowState == WindowState.Minimized,
            hardware, SystemParameters.ClientAreaAnimation);
        if (!animate) { _timer.Stop(); _clock.Stop(); }
        else if (!_timer.IsEnabled) ScheduleFrame();
    }
    public static bool CanAnimate(bool requested, int frameCount, bool active, bool minimized, bool hardware, bool motionEnabled)
        => requested && frameCount > 1 && active && !minimized && hardware && motionEnabled;
    private void ScheduleFrame()
    {
        if (_asset is null) return;
        _timer.Interval = TimeSpan.FromMilliseconds(67);
        _clock.Start();
        _timer.Start();
    }
    private void AdvanceFrame(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!_attached || _asset is null || Appearance?.ShouldAnimate != true) return;
        // Select from the original timeline. Slow UI frames never cause a burst of
        // catch-up work, and fast GIF frames are sampled without slowing the loop.
        double position = _clock.Elapsed.TotalMilliseconds % Math.Max(1, _duration);
        _frame = 0;
        while (_frame + 1 < _asset.Frames.Count && position >= _asset.Frames[_frame].Duration.TotalMilliseconds)
            position -= _asset.Frames[_frame++].Duration.TotalMilliseconds;
        _image.Source = _asset.Frames[_frame].Image;
        ScheduleFrame();
    }
}
