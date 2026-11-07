using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using MacroSuite.App.Common;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;

namespace MacroSuite.App.ViewModels;

public sealed partial class ActionPropertiesViewModel
{
    private CancellationTokenSource? _detectionCts;
    private Task _detectionTask = Task.CompletedTask;
    private string _detectionTestResult = "Test scans once without clicking or pressing keys.";
    private bool _isTestingDetection;
    private bool _hasDraftChanges;
    private BitmapSource? _templatePreview;
    public RelayCommand TestDetectionCommand { get; private set; } = null!;
    public RelayCommand CancelDetectionTestCommand { get; private set; } = null!;
    public string DetectionTestResult { get => _detectionTestResult; private set => SetProperty(ref _detectionTestResult, value); }
    public BitmapSource? TemplatePreview { get => _templatePreview; private set => SetProperty(ref _templatePreview, value); }
    public bool IsTestingDetection
    {
        get => _isTestingDetection;
        private set
        {
            if (!SetProperty(ref _isTestingDetection, value)) return;
            RefreshCommandAvailability();
            _editor.RefreshDetectionAvailability();
        }
    }
    public string ImageOutcomeSummary => RepeatImageWhileVisible
        ? $"Found → Run actions until the next image trigger, then wait {ImageRepeatDelayMs} ms. Not found → Keep checking all image groups. Capture or image error → Stop and show the error."
        : RedirectOnAppearance ? $"Found → Jump to {ResolvedDestination(AppearanceTargetStepId)}. Not found → Keep running. Capture or image error → Stop and show the error."
        : $"{(WaitWhile ? "Image disappears" : "Image found")} → Continue. Timeout after {TimeoutMs / 1000.0:0.#} s → {(_editor.SelectedStep?.Step is WaitUntilStep { ThrowOnTimeout: true } ? "Stop macro" : "Continue")}. Capture or image error → Stop and show the error.";
    public string BlockOutcomeSummary
    {
        get
        {
            string flow = Kind is EditableActionKind.RepeatUntil or EditableActionKind.RepeatUntilGone
                ? "Condition matches → Leave this loop and continue. Not satisfied → Run Do actions and check again."
                : ShowIfFields
                    ? $"Condition matches → Run Then actions, then continue. Not satisfied → {(_editor.SelectedStep?.Step is IfThenElseStep branch && branch.ElseSteps.Any(step => step.IsEnabled) ? "Run Else actions, then continue" : RouteDescription(OnNotSatisfiedRouteIndex))}."
                    : $"Condition matches → Run Do actions and check again. Not satisfied → {RouteDescription(OnNotSatisfiedRouteIndex)}.";
            FlowRoute failure = (FlowRoute)OnFailureRouteIndex;
            string inherited = string.Empty;
            if (failure == FlowRoute.Default)
            {
                failure = (ConditionFailurePolicy)FailurePolicyIndex switch
                {
                    ConditionFailurePolicy.Stop => FlowRoute.Stop,
                    ConditionFailurePolicy.Skip => FlowRoute.Continue,
                    ConditionFailurePolicy.Retry => FlowRoute.Retry,
                    _ => FlowRoute.Default
                };
                inherited = "Inherited condition policy: ";
            }
            return $"{flow} Recoverable condition failure → {inherited}{RouteDescription((int)failure)}. Invalid image or configuration → Stop and show the error.";
        }
    }
    private string ResolvedDestination(Guid? id) => RouteTargets.FirstOrDefault(target => target.StepId == id)?.DisplayName ?? "Choose a destination";
    private string RouteDescription(int route) => (FlowRoute)route switch
    {
        FlowRoute.Continue => "Continue to the next action in this scope",
        FlowRoute.Retry => $"Retry {RouteRetryCount} times, then {_editor.NextConditionalDestination(_sourceStepId)}",
        FlowRoute.Jump => "Jump to " + ResolvedDestination(RouteTargetStepId),
        FlowRoute.Stop => "Stop macro",
        _ => "Default: " + _editor.NextConditionalDestination(_sourceStepId)
    };
    private void InitializeDetectionPresentation()
    {
        InitializeImageTargetPresentation();
        TestDetectionCommand = new RelayCommand(() =>
        {
            if (!_editor.CanEditMacro || IsTestingDetection || _editor.ExternalInputIsActive?.Invoke() == true) return;
            _detectionTask = TestDetectionAsync();
        }, () => HasSelection && _editor.CanEditMacro && !IsTestingDetection && _editor.ExternalInputIsActive?.Invoke() != true);
        CancelDetectionTestCommand = new RelayCommand(CancelDetectionTest, () => IsTestingDetection);
        PropertyChanged += (_, _) => RefreshDraftState();
    }
    private ImageCondition? CurrentImageDraft()
    {
        var stored = _editor.GetStepAt(_editor.SelectedStepIndex);
        ImageCondition? image = _pendingImageCondition ?? (stored as WaitUntilStep)?.Condition as ImageCondition;
        if (ShowImageFields && image is not null) return WithImageSearchRegion(image);
        if (ShowPresenceImageFields)
        {
            int padding = SearchModeIndex == 1 ? 50 : 0;
            return new(_currentPresenceTemplatePath, _currentPresenceTemplateBytes, _currentPresenceMinConfidence,
                SearchModeIndex == 2 ? null : new System.Drawing.Rectangle(X - padding, Y - padding,
                    Math.Max(1, Width + padding * 2), Math.Max(1, Height + padding * 2)));
        }
        return null;
    }
    private void UpdateDetectionPresentation()
    {
        CancelDetectionTest();
        DetectionTestResult = "Test scans once without clicking or pressing keys.";
        TemplatePreview = null;
        ImageCondition? image = CurrentImageDraft();
        try
        {
            using Stream? stream = image?.TemplateBytes is { Length: > 0 } bytes ? new MemoryStream(bytes)
                : !string.IsNullOrWhiteSpace(image?.TemplatePath) && File.Exists(image.TemplatePath) ? File.OpenRead(image.TemplatePath) : null;
            if (stream is not null)
            {
                var preview = new BitmapImage();
                preview.BeginInit(); preview.CacheOption = BitmapCacheOption.OnLoad; preview.DecodePixelWidth = 160; preview.StreamSource = stream; preview.EndInit(); preview.Freeze();
                TemplatePreview = preview;
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Runtime.InteropServices.COMException or System.IO.FileFormatException)
        { DetectionTestResult = $"Image preview unavailable: {ex.Message}"; }
        OnPropertyChanged(nameof(ImageOutcomeSummary));
        OnPropertyChanged(nameof(BlockOutcomeSummary));
    }
    private async Task TestDetectionAsync()
    {
        ImageCondition? image = CurrentImageDraft();
        if (image is null) { DetectionTestResult = "Choose a target image before testing."; return; }
        _detectionCts?.Dispose();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _detectionCts = cancellation;
        IsTestingDetection = true;
        DetectionTestResult = "Checking the selected search area…";
        var stepId = _sourceStepId;
        try
        {
            MatchResult result = await _editor.ProbeImageAsync(image,
                RepeatImageWhileVisible || RedirectOnAppearance || ShowPresenceImageFields && SearchModeIndex == 2,
                cancellation.Token).ConfigureAwait(false);
            Publish(() => DetectionTestResult = result.DiagnosticSummary ?? (result.Status switch
            {
                MatchOutcome.Matched => $"Found · score {result.Confidence:P1} · required {image.MinConfidence:P0}. {ImageSearchAreaSummary}",
                MatchOutcome.NotFound => $"Not found · score {result.Confidence:P1} · required {image.MinConfidence:P0}. {ImageSearchAreaSummary}",
                _ => $"Detection error: {result.ErrorMessage ?? result.ErrorCode}. The image was not treated as absent."
            }));
        }
        catch (OperationCanceledException) { Publish(() => DetectionTestResult = "Detection test cancelled or timed out."); }
        catch (Exception ex) { Publish(() => DetectionTestResult = $"Detection test failed: {ex.Message}"); }
        finally
        {
            if (ReferenceEquals(_detectionCts, cancellation)) _detectionCts = null;
            Publish(() => IsTestingDetection = false, checkSelection: false);
        }
        void Publish(Action action, bool checkSelection = true)
        {
            void Apply() { if (!_editor.IsDisposed && (!checkSelection || _sourceStepId == stepId)) action(); }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                try { if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Apply); }
                catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { }
            }
            else Apply();
        }
    }
    public void CancelDetectionTest() => _detectionCts?.Cancel();
    internal async Task StopDetectionTestAsync()
    {
        CancelDetectionTest();
        try { await _detectionTask; }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Detection test shutdown: {ex.Message}"); }
    }
    internal void StopDetectionTestForShutdown()
    {
        CancelDetectionTest();
        // Completion never waits for the UI thread; the probe owns and disposes its one frame.
        // No dispatcher work is awaited by the probe. Drain it completely before the
        // application disposes the shared capture service and matcher.
        try { _detectionTask.GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Detection test shutdown: {ex.Message}"); }
    }
    internal void RefreshCommandAvailability()
    {
        ApplyCommand?.RaiseCanExecuteChanged(); TestDetectionCommand?.RaiseCanExecuteChanged(); CancelDetectionTestCommand?.RaiseCanExecuteChanged();
        ImportImageTemplateCommand?.RaiseCanExecuteChanged(); CaptureTargetImageCommand?.RaiseCanExecuteChanged();
    }
}
