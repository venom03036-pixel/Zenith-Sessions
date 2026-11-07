using MacroSuite.App.Common;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Vision;
using MacroSuite.Infrastructure.Windows;
using DrawingRect = System.Drawing.Rectangle;

namespace MacroSuite.App.ViewModels;

public sealed partial class ActionPropertiesViewModel
{
    private ImageScanOptions? _imageScanOptions;
    private readonly List<DrawingRect?> _imageMonitors = new() { null };
    private IReadOnlyList<string> _connectedImageMonitorNames = ["All connected displays"];
    private int _connectedImageMonitorCount = 1;
    private DrawingRect? _savedImageMonitorBounds;
    private static readonly int[] ImageScanIntervals = [10, 25, 50, 100, 250, 500];
    public IReadOnlyList<string> ImageScopeNames { get; } = ["Existing automatic area", "Placed search area", "Target window", "Anywhere on screen"];
    public IReadOnlyList<string> ImageResolutionNames { get; } = ["Original pixels", "480p · lowest work", "720p · balanced", "1080p · more detail"];
    public IReadOnlyList<string> ImageScanPresetNames { get; } = ["10 ms", "25 ms", "50 ms", "100 ms", "250 ms", "500 ms", "Custom"];
    public IReadOnlyList<string> ImageMonitorNames { get; private set; } = ["All connected displays"];
    public RelayCommand MoveImageSearchAreaCommand { get; private set; } = null!;
    public bool ImageResourceBudgetEnabled
    {
        get => ImageScanBudget.Shared.Enabled;
        set
        {
            if (value == ImageScanBudget.Shared.Enabled) return;
            ImageScanBudget.Shared.Enabled = value;
            OnPropertyChanged(nameof(ImageResourceBudgetEnabled));
            OnPropertyChanged(nameof(ImageScanPerformanceText));
        }
    }
    public string ImageScanPerformanceText
    {
        get
        {
            var state = ImageScanBudget.Shared.Snapshot;
            string cpu = state.CpuPercent is double c ? $"{c:0.0}%" : "unavailable";
            string gpu = state.GpuPercent is double g ? $"{g:0.0}%" : "unavailable";
            string size = state.SampledSize.Width > 0 ? $" · {state.SampledSize.Width} × {state.SampledSize.Height}" : string.Empty;
            string measured = state.MeasuredIntervalMs is { } interval
                ? $"\n{(state.MeasuredActionId == _sourceStepId ? "This action" : "Latest active image group")} measured {interval:0} ms between scans (requested {state.MeasuredRequestedIntervalMs} ms)"
                : "\nMeasured scan interval is available after two scans.";
            return $"Requested {PollIntervalMs} ms · current minimum {ImageScanBudget.Shared.EffectiveInterval(PollIntervalMs)} ms{size}{measured}\nZenith CPU {cpu} · GPU {gpu}"
                + (!ImageResourceBudgetEnabled ? " · resource target off" : state.IsThrottled ? " · adapting to 3% target" : " · 3% target");
        }
    }
    public int ImageScopeIndex
    {
        get => (int)(_imageScanOptions?.SearchScope ?? ImageSearchScope.Legacy);
        set
        {
            var scope = (ImageSearchScope)Math.Clamp(value, 0, 3);
            if (scope == (_imageScanOptions?.SearchScope ?? ImageSearchScope.Legacy)) return;
            ImageScanOptions options = _imageScanOptions ?? new ImageScanOptions();
            _imageScanOptions = options with { SearchScope = scope,
                Resolution = scope is ImageSearchScope.Anywhere or ImageSearchScope.TargetWindow
                    ? ImageScanResolution.P720 : ImageScanResolution.Original };
            NotifyImageSetupChanged();
        }
    }
    public int ImageResolutionIndex
    {
        get => _imageScanOptions?.Resolution switch { ImageScanResolution.P480 => 1, ImageScanResolution.P720 => 2, ImageScanResolution.P1080 => 3, _ => 0 };
        set
        {
            var resolution = value switch { 1 => ImageScanResolution.P480, 2 => ImageScanResolution.P720, 3 => ImageScanResolution.P1080, _ => ImageScanResolution.Original };
            if (resolution == (_imageScanOptions?.Resolution ?? ImageScanResolution.Original)) return;
            _imageScanOptions = (_imageScanOptions ?? new ImageScanOptions()) with { Resolution = resolution };
            NotifyImageSetupChanged();
        }
    }
    public int ImageMonitorIndex
    {
        get => _imageMonitors.IndexOf(_imageScanOptions?.MonitorBounds);
        set
        {
            if (value < 0 || value >= _imageMonitors.Count || value == ImageMonitorIndex) return;
            _imageScanOptions = (_imageScanOptions ?? new ImageScanOptions()) with { MonitorBounds = _imageMonitors[value] };
            NotifyImageSetupChanged();
        }
    }
    public bool MatchAllImageOccurrences
    {
        get => _imageScanOptions?.MatchHandling == ImageMatchHandling.All;
        set
        {
            if (value == MatchAllImageOccurrences) return;
            _imageScanOptions = (_imageScanOptions ?? new ImageScanOptions()) with { MatchHandling = value ? ImageMatchHandling.All : ImageMatchHandling.Single };
            NotifyImageSetupChanged();
        }
    }
    public int ImageScanPresetIndex
    {
        get { int index = Array.IndexOf(ImageScanIntervals, PollIntervalMs); return index >= 0 ? index : ImageScanIntervals.Length; }
        set { if (value >= 0 && value < ImageScanIntervals.Length) PollIntervalMs = ImageScanIntervals[value]; }
    }
    public bool ShowFastImageScanWarning => PollIntervalMs < 50;
    public string ImageScanSpeedNote => PollIntervalMs < 50
        ? "Intervals below 50 ms can use more resources. Zenith slows scans when needed to aim for 3% total CPU and GPU use; brief spikes and slower detection are possible."
        : "Requested scan interval. Zenith adapts the actual rate to aim for 3% total CPU and GPU use. The delay between action runs is separate.";

    private void InitializeImageSetup()
    {
        MoveImageSearchAreaCommand = new RelayCommand(MoveImageSearchArea, CanEditImageSearchArea);
        var labels = new List<string> { "All connected displays" };
        foreach (var monitor in Win32VirtualDesktopGeometry.Query().Monitors)
        {
            _imageMonitors.Add(monitor.Bounds);
            labels.Add($"Display {labels.Count} · {monitor.Bounds.Width} × {monitor.Bounds.Height}{(monitor.IsPrimary ? " · primary" : "")}");
        }
        _connectedImageMonitorCount = _imageMonitors.Count;
        _connectedImageMonitorNames = labels.ToArray();
        ImageMonitorNames = _connectedImageMonitorNames;
        ImageScanBudget.Shared.SnapshotChanged += OnImageScanBudgetChanged;
        ImageScanBudget.Shared.EnabledChanged += OnImageScanBudgetEnabledChanged;
    }
    private void OnImageScanBudgetEnabledChanged(bool enabled) => OnImageScanBudgetChanged(ImageScanBudget.Shared.Snapshot);
    private void OnImageScanBudgetChanged(ImageScanPerformanceSnapshot snapshot)
    {
        void Refresh()
        {
            if (_editor.IsDisposed) return;
            OnPropertyChanged(nameof(ImageScanPerformanceText)); OnPropertyChanged(nameof(ImageResourceBudgetEnabled));
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Refresh);
        }
        else Refresh();
    }
    internal void DisposeImageSetup()
    {
        ImageScanBudget.Shared.SnapshotChanged -= OnImageScanBudgetChanged;
        ImageScanBudget.Shared.EnabledChanged -= OnImageScanBudgetEnabledChanged;
    }
    private void NotifyImageSetupChanged()
    {
        RefreshSavedImageMonitorOption();
        OnPropertyChanged(nameof(ImageScopeIndex)); OnPropertyChanged(nameof(ImageResolutionIndex));
        OnPropertyChanged(nameof(ImageMonitorIndex)); OnPropertyChanged(nameof(MatchAllImageOccurrences));
        OnPropertyChanged(nameof(ImageScanPresetIndex)); OnPropertyChanged(nameof(ShowFastImageScanWarning));
        OnPropertyChanged(nameof(ImageScanSpeedNote)); OnPropertyChanged(nameof(ImageSearchAreaSummary));
        OnPropertyChanged(nameof(ImageScanPerformanceText));
        MoveImageSearchAreaCommand?.RaiseCanExecuteChanged();
    }
    private void RefreshSavedImageMonitorOption()
    {
        DrawingRect? savedBounds = _imageScanOptions?.MonitorBounds;
        int index = _imageMonitors.IndexOf(savedBounds);
        DrawingRect? unavailableBounds = index >= 0 && index < _connectedImageMonitorCount ? null : savedBounds;
        if (_savedImageMonitorBounds == unavailableBounds) return;

        if (_imageMonitors.Count > _connectedImageMonitorCount)
            _imageMonitors.RemoveRange(_connectedImageMonitorCount, _imageMonitors.Count - _connectedImageMonitorCount);
        _savedImageMonitorBounds = unavailableBounds;
        if (unavailableBounds is { } area)
        {
            _imageMonitors.Add(area);
            ImageMonitorNames = [.. _connectedImageMonitorNames,
                $"Saved display area (display changed) · {area.Width} × {area.Height} at ({area.X}, {area.Y})"];
        }
        else ImageMonitorNames = _connectedImageMonitorNames;
        // This is a label for the persisted rectangle, never a replacement monitor choice.
        OnPropertyChanged(nameof(ImageMonitorNames));
    }
    private void MoveImageSearchArea()
    {
        if (!CanEditImageSearchArea()) return;
        var selected = _sourceStepId;
        ImageCondition? image = _pendingImageCondition ?? (_editor.GetStepAt(_editor.SelectedStepIndex) as WaitUntilStep)?.Condition as ImageCondition;
        if (image is null || !TryReadImageTemplateSize(image, out var size))
        { _editor.NotifyStatus("Capture or import a readable image before placing its search area."); return; }
        try
        {
            var area = _editor.PromptOwnedImageArea(_imageSearchRegion, size);
            if (area is null || selected != _sourceStepId || !CanEditImageSearchArea()) return;
            if (!ValidateImageSearchArea(image, area)) return;
            _imageSearchRegion = area;
            _imageScanOptions = (_imageScanOptions ?? ImageScanOptions.ForPlacedArea()) with { SearchScope = ImageSearchScope.PlacedArea, Resolution = ImageScanResolution.Original };
            _draftError = null;
            NotifyImageSearchAreaChanged(); NotifyImageSetupChanged();
            _editor.NotifyStatus("Search area placed. Apply to keep it.");
        }
        catch (Exception ex) { _editor.NotifyStatus($"Could not place the search area: {ex.Message}"); }
    }
    private void AdoptNewImageSelection(ImageTemplateSelection selection)
    {
        if (selection.SourceRegion is { Width: > 0, Height: > 0 } region)
        {
            _imageSearchRegion = region;
            _imageScanOptions = ImageScanOptions.ForPlacedArea();
        }
    }
    private string DescribeImageSearchArea()
    {
        var scope = _imageScanOptions?.SearchScope ?? ImageSearchScope.Legacy;
        if (scope == ImageSearchScope.Anywhere)
        {
            if (_imageScanOptions?.MonitorBounds is not { } monitor) return "Anywhere on all connected displays";
            int index = _imageMonitors.IndexOf(monitor);
            return index < 0 || index >= _connectedImageMonitorCount
                ? $"Saved display area (display changed): {monitor.Width} × {monitor.Height} pixels at ({monitor.X}, {monitor.Y}). Original bounds preserved."
                : $"Anywhere on selected display · {monitor.Width} × {monitor.Height} pixels at ({monitor.X}, {monitor.Y})";
        }
        if (scope == ImageSearchScope.TargetWindow) return "Target window · locked to the active window when monitoring starts";
        if (_imageSearchRegion is { Width: > 0, Height: > 0 } area) return $"Search area: {area.Width} × {area.Height} pixels at ({area.X}, {area.Y})";
        if (scope == ImageSearchScope.PlacedArea) return "Place a search area before running.";
        return _repeatImageWhileVisible && !_redirectOnAppearance ? "Search area: Full screen (existing default)"
            : "Search area: Automatic active window (existing default; full screen if unavailable)";
    }
}
