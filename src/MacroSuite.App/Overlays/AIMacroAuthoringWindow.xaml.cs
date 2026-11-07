using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using MacroSuite.App.ViewModels;

namespace MacroSuite.App.Overlays;

public partial class AIMacroAuthoringWindow : Window
{
    private readonly AIMacroAuthoringViewModel _viewModel;
    private bool _allowClose;
    private bool _closing;
    public Func<System.Drawing.Rectangle?>? ChooseRegion { get; set; }
    public AIMacroAuthoringWindow(AIMacroAuthoringViewModel viewModel)
    { InitializeComponent(); _viewModel = viewModel; DataContext = viewModel; }
    private async void Generate_Click(object sender, RoutedEventArgs e) => await _viewModel.GenerateAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _viewModel.Cancel();
    private void ClearArea_Click(object sender, RoutedEventArgs e) { _viewModel.ClearScreen(); ScreenPreview.Source = null; }
    private async void ChooseArea_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanEdit || ChooseRegion is null) return;
        Hide();
        try
        {
            System.Drawing.Rectangle? region = ChooseRegion();
            if (region.HasValue) await _viewModel.CaptureAreaAsync(region.Value);
        }
        catch (Exception ex)
        { _viewModel.ReportProblem("Could not choose the screen area: " + ex.Message); }
        finally { if (!_closing) { Show(); Activate(); } }
        try
        {
            if (!_closing && _viewModel.PreviewJpeg is { Length: > 0 } bytes)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream; image.EndInit(); image.Freeze(); ScreenPreview.Source = image;
            }
        }
        catch (Exception ex)
        { _viewModel.ClearScreen(); ScreenPreview.Source = null; _viewModel.ReportProblem("Could not preview the captured image: " + ex.Message); }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    { await _viewModel.SaveAsync(); if (_viewModel.SavedMacro is not null) Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        try
        {
            await _viewModel.CloseAsync();
            _allowClose = true;
            // CloseAsync can complete synchronously. Leave this Closing event before
            // requesting the final close, otherwise WPF rejects the nested Close call.
            await Dispatcher.InvokeAsync(new Action(Close));
        }
        catch (Exception ex)
        {
            _closing = false;
            IsEnabled = true;
            _viewModel.ReportProblem("Could not finish closing: " + ex.Message + ". Try closing again.");
        }
    }
}
