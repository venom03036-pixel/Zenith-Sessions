using System.Windows;
using System.Windows.Controls;

namespace MacroSuite.App.Common;

/// <summary>Keeps visited shell pages attached so navigation preserves their editor and scroll state.</summary>
public sealed class RetainedViewHost : Grid
{
    private const int MaximumPages = 8;
    private readonly Dictionary<object, ContentPresenter> _pages = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<object> _visitOrder = new();

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(object), typeof(RetainedViewHost),
        new PropertyMetadata(null, (owner, args) => ((RetainedViewHost)owner).SelectPage(args.NewValue)));

    public object? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void SelectPage(object? model)
    {
        if (model == null)
        {
            Children.Clear(); _pages.Clear(); _visitOrder.Clear();
            return;
        }
        if (!_pages.TryGetValue(model, out var selected))
        {
            // The shell has six fixed page models. Keep an explicit bound even
            // if a future caller supplies a new model on every navigation.
            if (_pages.Count == MaximumPages)
            {
                var oldest = _visitOrder.Dequeue();
                Children.Remove(_pages[oldest]); _pages.Remove(oldest);
            }
            selected = new ContentPresenter { Content = model, Visibility = Visibility.Collapsed };
            _pages.Add(model, selected); _visitOrder.Enqueue(model); Children.Add(selected);
        }
        foreach (var page in _pages.Values)
            page.Visibility = ReferenceEquals(page, selected) ? Visibility.Visible : Visibility.Collapsed;
    }
}
