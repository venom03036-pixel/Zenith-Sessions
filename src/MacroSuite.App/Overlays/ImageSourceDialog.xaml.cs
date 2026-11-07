using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using MacroSuite.App.Authoring;
using Microsoft.Win32;
using DrawingPoint = System.Drawing.Point;
using DrawingRect = System.Drawing.Rectangle;

namespace MacroSuite.App.Overlays;

public sealed record ImageTemplateSelection(
    string ImagePath,
    double Confidence,
    bool AutoClick,
    DrawingPoint? InitialCenter,
    DrawingRect? SourceRegion = null,
    System.Drawing.Size? TemplateSize = null,
    byte[]? TemplateBytes = null);

public partial class ImageSourceDialog : Window
{
    private readonly CancellationToken _stop;
    private ImageTemplateSourceRequest? _request;
    private ExceptionDispatchInfo? _fileFailure;
    private bool _fileDialogOpen;

    public ImageSourceDialog() : this(default) { }

    private ImageSourceDialog(CancellationToken stop)
    {
        _stop = stop;
        InitializeComponent();
    }

    internal static ImageTemplateSourceRequest? PromptSource(Window? owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dialog = new ImageSourceDialog(ct);
        OverlayWindow.Attach(dialog, owner);
        bool? accepted = AuthoringModalCancellation.ShowDialog(dialog, ct, () => !dialog._fileDialogOpen);
        if (ct.IsCancellationRequested) return null;
        dialog._fileFailure?.Throw();
        return accepted == true ? dialog._request : null;
    }

    internal static ImageTemplateSourceRequest? PromptImportSource(Window? owner, double confidence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var picker = CreateFilePicker("Import target image");
        bool? accepted = owner is null ? picker.ShowDialog() : picker.ShowDialog(owner);
        return accepted == true && !ct.IsCancellationRequested
            ? new(ImageTemplateSourceKind.File, picker.FileName, confidence, false)
            : null;
    }

    private static OpenFileDialog CreateFilePicker(string title) => new()
    {
        Title = title,
        Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*",
        CheckFileExists = true,
        Multiselect = false
    };

    private void OnBrowseFileClick(object sender, RoutedEventArgs e)
    {
        if (_stop.IsCancellationRequested) { Close(); return; }
        var picker = CreateFilePicker("Select Template Image / Photo");
        bool? accepted = null;
        _fileDialogOpen = true;
        try { accepted = picker.ShowDialog(this); }
        catch (Exception error) { _fileFailure = ExceptionDispatchInfo.Capture(error); }
        finally { _fileDialogOpen = false; }
        // The system file dialog may require dismissal. Keep the captured chooser owned
        // until that actual modal has returned, then discard any selection after Stop.
        if (_stop.IsCancellationRequested || _fileFailure is not null) { Close(); return; }
        if (accepted == true) Finish(ImageTemplateSourceKind.File, picker.FileName);
    }

    private void OnSnipScreenClick(object sender, RoutedEventArgs e) => Finish(ImageTemplateSourceKind.Screen, null);

    private void Finish(ImageTemplateSourceKind kind, string? path)
    {
        if (_stop.IsCancellationRequested) { Close(); return; }
        _request = new(kind, path, ConfidenceSlider.Value, AutoClickCheckBox.IsChecked == true);
        DialogResult = true;
    }

    private void OnDialogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.F10 or Key.Enter)
        {
            e.Handled = true;
            Finish(ImageTemplateSourceKind.Screen, null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancelClick(sender, e);
        }
    }

    private void OnConfidenceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ConfidenceLabel is not null) ConfidenceLabel.Text = $"{e.NewValue:P0}";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
