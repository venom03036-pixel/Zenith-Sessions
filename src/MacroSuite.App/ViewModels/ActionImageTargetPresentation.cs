using System.IO;
using MacroSuite.App.Common;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    public Func<double, ImageTemplateSelection?>? ImageImportPrompter { get; set; }
    public Func<double, ImageTemplateSelection?>? ImageCaptureOnlyPrompter { get; set; }
}

public sealed partial class ActionPropertiesViewModel
{
    public RelayCommand ImportImageTemplateCommand { get; private set; } = null!;
    public RelayCommand CaptureTargetImageCommand { get; private set; } = null!;
    public IReadOnlyList<string> ImageDetectionModeNames { get; } = ["Find once", "Repeat while visible", "Jump during playback"];
    public string ImageTemplateDimensions => CurrentImageDraft() is { } image && TryReadImageTemplateSize(image, out var size)
        ? $"{size.Width} × {size.Height} physical pixels" : "Template dimensions unavailable";
    public double ImageConfidencePercent
    {
        get => (CurrentImageDraft()?.MinConfidence ?? .8) * 100;
        set
        {
            if (_loading || !HasSelection || _editor.IsEditingLocked || !double.IsFinite(value) || CurrentImageDraft() is not { } image) return;
            double confidence = Math.Clamp(value, 50, 100) / 100;
            if (Math.Abs(confidence - image.MinConfidence) < .000001) return;
            _pendingImageCondition = new ImageCondition(image.TemplatePath, image.TemplateBytes, confidence, _imageSearchRegion) { ScanOptions = _imageScanOptions };
            ImageTemplateSummary = !string.IsNullOrWhiteSpace(image.TemplatePath)
                ? $"Template: {Path.GetFileName(image.TemplatePath)} (≥{confidence:P0})"
                : $"Captured image template (≥{confidence:P0})";
            OnPropertyChanged(nameof(ImageConfidencePercent));
        }
    }

    public int ImageDetectionModeIndex
    {
        get => _redirectOnAppearance ? 2 : _repeatImageWhileVisible ? 1 : 0;
        set
        {
            if (_loading || !HasSelection || _editor.IsEditingLocked || value is < 0 or > 2 || value == ImageDetectionModeIndex) return;
            // Change the draft atomically so selecting a mode never commits an intermediate combination.
            _repeatImageWhileVisible = value == 1;
            _redirectOnAppearance = value == 2;
            if (value != 0) _waitWhile = false;
            if (value == 1)
                _imageScanOptions = (_imageScanOptions ?? new ImageScanOptions()) with { MatchHandling = ImageMatchHandling.All };
            OnPropertyChanged(nameof(RepeatImageWhileVisible)); OnPropertyChanged(nameof(RedirectOnAppearance));
            OnPropertyChanged(nameof(WaitWhile)); OnPropertyChanged(nameof(ImageOneShot));
            OnPropertyChanged(nameof(ImageDetectionModeIndex));
            NotifyImageSetupChanged();
        }
    }

    private void InitializeImageTargetPresentation()
    {
        ImportImageTemplateCommand = new RelayCommand(() => SelectTargetImage(import: true), CanEditImageSearchArea);
        CaptureTargetImageCommand = new RelayCommand(() => SelectTargetImage(import: false), CanEditImageSearchArea);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TemplatePreview) or nameof(ImageTemplateSummary) or nameof(HasSelection))
            {
                OnPropertyChanged(nameof(ImageTemplateDimensions)); OnPropertyChanged(nameof(ImageConfidencePercent));
                ImportImageTemplateCommand.RaiseCanExecuteChanged(); CaptureTargetImageCommand.RaiseCanExecuteChanged();
            }
            if (e.PropertyName is nameof(RepeatImageWhileVisible) or nameof(RedirectOnAppearance) or nameof(ImageOneShot))
                OnPropertyChanged(nameof(ImageDetectionModeIndex));
        };
    }

    private void SelectTargetImage(bool import)
    {
        if (!CanEditImageSearchArea()) return;
        var stamp = _editor.CaptureAuthoringSelection();
        try
        {
            var selection = _editor.PromptOwnedTargetImage(import, ImageConfidencePercent / 100);
            // Pickers run nested message loops. Reject their result if playback/selection changed.
            if (selection is null || string.IsNullOrWhiteSpace(selection.ImagePath)
                || !_editor.IsCurrentAuthoringSelection(stamp) || !CanEditImageSearchArea()) return;
            var image = new ImageCondition(selection.ImagePath, selection.TemplateBytes, selection.Confidence);
            if (!ValidateImageSearchArea(image, selection.SourceRegion)) return;
            _pendingImageCondition = image;
            AdoptNewImageSelection(selection);
            _draftError = null;
            ImageTemplateSummary = $"Template: {Path.GetFileName(selection.ImagePath)} (≥{selection.Confidence:P0})";
            NotifyImageSearchAreaChanged(); NotifyImageSetupChanged(); UpdateDetectionPresentation();
            _editor.NotifyStatus("Target image updated. Apply to keep the image and its search area.");
        }
        catch (Exception ex) { _editor.NotifyStatus($"Could not prepare the target image: {ex.Message}"); }
    }
}
