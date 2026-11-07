using System.Windows;
using System.Windows.Controls;
using MacroSuite.App.Common;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed class ResponsiveColumnsPanelTests
{
    [Theory]
    [InlineData(759, false)]
    [InlineData(760, true)]
    [InlineData(1120, true)]
    public void CardsRespondToAvailableWidthAndKeepTheirOwnHeight(double width, bool twoColumns)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var panel = new ResponsiveColumnsPanel { Gap = 24, ColumnThreshold = 760 };
                var first = new Border { Height = 100 };
                var second = new Border { Height = 160 };
                panel.Children.Add(first); panel.Children.Add(second);
                panel.Measure(new Size(width, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
                Assert.Equal(twoColumns ? 160 : 284, panel.DesiredSize.Height);
                Point secondOrigin = second.TranslatePoint(new Point(), panel);
                Assert.Equal(twoColumns ? 0 : 124, secondOrigin.Y);
                Assert.Equal(twoColumns ? (width + 24) / 2 : 0, secondOrigin.X);
                Assert.Equal(twoColumns ? (width - 24) / 2 : width, first.ActualWidth);
                Assert.Equal(100, first.ActualHeight);
                Assert.Equal(160, second.ActualHeight);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
    }
}
