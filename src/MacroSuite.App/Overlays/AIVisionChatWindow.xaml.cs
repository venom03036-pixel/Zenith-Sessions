using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Pre-flight chat dialog for AI Vision steps.
/// The user types instructions, sends them to refine the running prompt,
/// then clicks "Use This Prompt" to finalize the step prompt (Rule 8, Rule 15).
/// </summary>
public partial class AIVisionChatWindow : Window
{
    private readonly StringBuilder _accumulatedPrompt = new();
    private bool _cancelled = true;

    /// <summary>True if user cancelled the dialog.</summary>
    public bool IsCancelled => _cancelled;

    /// <summary>The finalized prompt built from all chat messages. Null if the user cancelled.</summary>
    public string? FinalPrompt { get; private set; }

    public AIVisionChatWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => InputBox.Focus();
    }

    // --- Internal helpers ------------------------------------------------

    private void AppendUserBubble(string text)
    {
        var bubble = new Border
        {
            Style = (Style)FindResource("UserBubble"),
        };
        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        bubble.Child = label;
        ChatHistory.Children.Add(bubble);
        ChatScrollViewer.ScrollToEnd();
    }

    private void AccumulateMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        string text = raw.Trim();

        // Append separator between turns
        if (_accumulatedPrompt.Length > 0)
            _accumulatedPrompt.Append(". ");
        _accumulatedPrompt.Append(text);

        AppendUserBubble(text);
        UpdateFinalizeButton();
    }

    private void UpdateFinalizeButton()
    {
        int len = _accumulatedPrompt.Length;
        FinalizeButton.IsEnabled = len > 0;
        CharCountLabel.Text = $"{len} chars";
    }

    // --- Event Handlers --------------------------------------------------

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cancelled = true;
        Close();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        string text = InputBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        AccumulateMessage(text);
        InputBox.Clear();
        InputBox.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelButton_Click(sender, e);
        }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(InputBox.Text))
        {
            SendButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Live char count reflects what would be accumulated
        string preview = InputBox.Text?.Trim() ?? string.Empty;
        int total = _accumulatedPrompt.Length + (preview.Length > 0 ? preview.Length + 2 : 0);
        CharCountLabel.Text = $"{total} chars";
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _accumulatedPrompt.Clear();
        InputBox.Clear();

        // Remove all user bubbles, keep only the first assistant bubble
        while (ChatHistory.Children.Count > 1)
            ChatHistory.Children.RemoveAt(ChatHistory.Children.Count - 1);

        UpdateFinalizeButton();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancelled = true;
        Close();
    }

    private void FinalizeButton_Click(object sender, RoutedEventArgs e)
    {
        // Flush anything remaining in the text box before finalizing
        string pending = InputBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(pending))
            AccumulateMessage(pending);

        FinalPrompt = _accumulatedPrompt.ToString();
        _cancelled = false;
        DialogResult = true;
        Close();
    }
}

