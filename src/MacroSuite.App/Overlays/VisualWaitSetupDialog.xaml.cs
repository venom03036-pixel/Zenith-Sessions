using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MacroSuite.App.Authoring;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Vision;

namespace MacroSuite.App.Overlays;

internal enum VisualWaitIntent { PickPixel, PickRegion, Create }

internal sealed record VisualWaitDraft(
    int ModeIndex = 0,
    int SearchModeIndex = 0,
    int MethodIndex = 0,
    int SimilarityPercent = 90,
    int NeighborhoodIndex = 0,
    bool MaximumAccuracy = false,
    string PollText = "150",
    string TimeoutText = "10",
    int TimeoutActionIndex = 0,
    string RetryText = "3",
    AuthoringPixelSample? PixelSample = null,
    AuthoringVisualSample? RegionSample = null);

internal sealed record VisualWaitDecision(
    VisualWaitIntent Intent, VisualWaitDraft Draft, VisualWaitStep? CreatedStep = null);

/// <summary>Collects one modal decision. The editor performs each pick after this window closes.</summary>
public partial class VisualWaitSetupDialog : Window
{
    private readonly VisualWaitDraft _draft;
    private readonly CancellationToken _stop;
    private VisualWaitDecision? _decision;

    internal VisualWaitSetupDialog(VisualWaitDraft draft, CancellationToken ct)
    {
        _draft = draft;
        _stop = ct;
        InitializeComponent();
        ModePixel.IsChecked = draft.ModeIndex == 0;
        ModeNeighborhood.IsChecked = draft.ModeIndex == 1;
        ModeRegion.IsChecked = draft.ModeIndex == 2;
        SearchModeBox.SelectedIndex = draft.SearchModeIndex;
        MethodBox.SelectedIndex = draft.MethodIndex;
        SimilaritySlider.Value = draft.SimilarityPercent;
        NeighborhoodBox.SelectedIndex = draft.NeighborhoodIndex;
        MaxAccuracyBox.IsChecked = draft.MaximumAccuracy;
        PollBox.Text = draft.PollText;
        TimeoutBox.Text = draft.TimeoutText;
        TimeoutActionBox.SelectedIndex = draft.TimeoutActionIndex;
        RetryBox.Text = draft.RetryText;
        UpdatePreview();
        ModePixel.Checked += (_, _) => UpdatePreview();
        ModeNeighborhood.Checked += (_, _) => UpdatePreview();
        ModeRegion.Checked += (_, _) => UpdatePreview();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    internal static VisualWaitDecision? Prompt(Window? owner, VisualWaitDraft draft, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dialog = new VisualWaitSetupDialog(draft, ct);
        OverlayWindow.Attach(dialog, owner);
        bool? accepted = AuthoringModalCancellation.ShowDialog(dialog, ct);
        return accepted == true && !ct.IsCancellationRequested ? dialog._decision : null;
    }

    internal VisualWaitDraft ReadDraft() => _draft with
    {
        ModeIndex = ModeRegion.IsChecked == true ? 2 : ModeNeighborhood.IsChecked == true ? 1 : 0,
        SearchModeIndex = SearchModeBox.SelectedIndex,
        MethodIndex = MethodBox.SelectedIndex,
        SimilarityPercent = (int)SimilaritySlider.Value,
        NeighborhoodIndex = NeighborhoodBox.SelectedIndex,
        MaximumAccuracy = MaxAccuracyBox.IsChecked == true,
        PollText = PollBox.Text,
        TimeoutText = TimeoutBox.Text,
        TimeoutActionIndex = TimeoutActionBox.SelectedIndex,
        RetryText = RetryBox.Text
    };

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnSimilarityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SimilarityLabel is not null) SimilarityLabel.Text = $"{(int)e.NewValue}%";
    }

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        var draft = ReadDraft();
        Finish(new(draft.ModeIndex == 2 ? VisualWaitIntent.PickRegion : VisualWaitIntent.PickPixel, draft));
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var draft = ReadDraft();
        var step = CreateStep(draft);
        if (step is null)
        {
            RgbText.Text = draft.ModeIndex == 2 ? "Pick a valid image region first." : "Pick a pixel from the screen first.";
            return;
        }
        Finish(new(VisualWaitIntent.Create, draft, step));
    }

    private void Finish(VisualWaitDecision decision)
    {
        if (_stop.IsCancellationRequested) { Close(); return; }
        _decision = decision;
        DialogResult = true;
    }

    internal static VisualWaitStep? CreateStep(VisualWaitDraft draft)
    {
        bool useRegion = draft.ModeIndex == 2;
        var region = draft.RegionSample;
        var pixel = draft.PixelSample;
        int x, y, width, height;
        byte r, g, b;
        if (useRegion)
        {
            if (!ValidRegion(region)) return null;
            x = region!.Region.X;
            y = region.Region.Y;
            width = region.Width;
            height = region.Height;
            (r, g, b) = (region.R, region.G, region.B);
        }
        else
        {
            if (pixel is not { } picked) return null;
            (x, y, r, g, b) = (picked.X, picked.Y, picked.R, picked.G, picked.B);
            (width, height) = (1, 1);
        }

        int neighborhood = draft.NeighborhoodIndex == 2 ? 5
            : draft.ModeIndex == 1 || draft.NeighborhoodIndex == 1 ? 3 : 1;
        if (!int.TryParse(draft.PollText, out int poll)) poll = 150;
        if (!double.TryParse(draft.TimeoutText, out double timeoutSeconds) || !double.IsFinite(timeoutSeconds)) timeoutSeconds = 10;
        if (!int.TryParse(draft.RetryText, out int retries)) retries = 3;

        return new VisualWaitStep
        {
            Label = useRegion ? $"Wait visual region ({x},{y})" : $"Wait visual {ToHex(r, g, b)}",
            Mode = useRegion ? "Region" : "Pixel",
            SearchMode = draft.SearchModeIndex switch { 1 => "Nearby", 2 => "Screen", _ => "Fixed" },
            MatchingMethod = draft.MethodIndex switch { 1 => "Ai", 2 => "ColorImageAndAi", _ => "ColorImage" },
            Quality = draft.MaximumAccuracy ? "MaximumAccuracy" : "Balanced",
            OnTimeout = draft.TimeoutActionIndex switch { 1 => "Skip", 2 => "Retry", _ => "Stop" },
            X = x, Y = y, Width = width, Height = height, R = r, G = g, B = b,
            SimilarityPercent = Math.Clamp(draft.SimilarityPercent, 70, 99),
            NeighborhoodSize = neighborhood,
            PollIntervalMs = Math.Clamp(poll, 50, 1000),
            TimeoutMs = (int)Math.Clamp(timeoutSeconds * 1000.0, 100, 600_000),
            RetryCount = Math.Clamp(retries, 0, 20),
            TemplateBgra = useRegion ? region!.Bgra32 : null,
            TemplateWidth = useRegion ? width : 0,
            TemplateHeight = useRegion ? height : 0
        };
    }

    private static bool ValidRegion(AuthoringVisualSample? region) => region is { Width: > 0, Height: > 0 }
        && !VisualMatchOptions.IsTemplateOversized(region.Width, region.Height)
        && region.Region.Width == region.Width && region.Region.Height == region.Height
        && region.Bgra32 is { } bytes && bytes.Length == (long)region.Width * region.Height * 4;

    private void UpdatePreview()
    {
        if (ModeRegion.IsChecked == true && ValidRegion(_draft.RegionSample))
        {
            var region = _draft.RegionSample!;
            ShowColor(region.R, region.G, region.B,
                $"region ({region.Region.X},{region.Region.Y}) {region.Width}×{region.Height}");
            if ((long)region.Width * region.Height > 80 * 80)
                RgbText.Text += "\nLarger templates use more memory. Prefer a small target.";
            return;
        }
        if (ModeRegion.IsChecked != true && _draft.PixelSample is { } pixel)
        {
            ShowColor(pixel.R, pixel.G, pixel.B, $"({pixel.X},{pixel.Y})");
            return;
        }
        ColorSwatch.Background = Brushes.Transparent;
        HexText.Text = "—";
        RgbText.Text = "Click Pick to sample a pixel or region.";
    }

    private void ShowColor(byte r, byte g, byte b, string location)
    {
        ColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        HexText.Text = ToHex(r, g, b);
        RgbText.Text = $"RGB({r}, {g}, {b})  {location}";
    }

    private static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}
