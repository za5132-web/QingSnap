using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingSnap.App.Models;
using QingSnap.App.Views;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class CaptureAdjustmentBadgeTests
{
    [Theory]
    [InlineData(350, 250)]
    [InlineData(950, 650)]
    [InlineData(20, 12)]
    public void RepeatedWheelFeedbackDoesNotOscillate(double x, double y) => Run(window =>
    {
        var root = (Grid)window.FindName("Root");
        var badge = (Border)window.FindName("AdjustmentBadge");
        Show(window, new Point(x, y), "线宽  1 px");
        root.UpdateLayout();
        var first = badge.Margin;
        for (var index = 0; index < 24; index++)
        {
            Show(window, new Point(x, y), "线宽  1 px");
            root.UpdateLayout();
            Assert.Equal(first.Left, badge.Margin.Left, 3);
            Assert.Equal(first.Top, badge.Margin.Top, 3);
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void PositionUsesCurrentBadgeSizeNotPreviousMargin(double scale) => Run(window =>
    {
        var root = (Grid)window.FindName("Root");
        var badge = (Border)window.FindName("AdjustmentBadge");
        var available = new Size(1000 / scale, 700 / scale);
        root.Measure(available);
        root.Arrange(new Rect(available));
        foreach (var (point, text) in new[]
        {
            (new Point(260 / scale, 190 / scale), "线宽  2 px"),
            (new Point(810 / scale, 560 / scale), "字号  120 px"),
            (new Point(980 / scale, 680 / scale), "序号大小  64 px"),
            (new Point(400 / scale, 340 / scale), "线宽  12 px")
        })
        {
            Show(window, point, text);
            root.UpdateLayout();
            Assert.Equal(Math.Clamp(point.X + 16, 8, Math.Max(8, root.ActualWidth - badge.ActualWidth - 8)),
                badge.Margin.Left, 1);
            Assert.Equal(Math.Clamp(point.Y - badge.ActualHeight - 12, 8,
                Math.Max(8, root.ActualHeight - badge.ActualHeight - 8)), badge.Margin.Top, 1);
        }
    });

    [Fact]
    public void CollapsedFeedbackDoesNotReuseStaleOffsets() => Run(window =>
    {
        var root = (Grid)window.FindName("Root");
        var badge = (Border)window.FindName("AdjustmentBadge");
        Show(window, new Point(650, 500), "线宽  3 px");
        root.UpdateLayout();
        var first = badge.Margin;
        for (var index = 0; index < 10; index++)
        {
            badge.Visibility = Visibility.Collapsed;
            root.UpdateLayout();
            Show(window, new Point(650, 500), "线宽  3 px");
            root.UpdateLayout();
            Assert.Equal(first, badge.Margin);
        }
        Assert.False(badge.IsHitTestVisible);
    });

    [Fact]
    public void ClosingOverlayStopsFeedbackTimer() => Run(window =>
    {
        Show(window, new Point(400, 300), "线宽  4 px");
        var timer = (DispatcherTimer)typeof(CaptureOverlayWindow)
            .GetField("_adjustmentBadgeTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        Assert.True(timer.IsEnabled);
        window.Close();
        Assert.False(timer.IsEnabled);
    });

    private static void Show(CaptureOverlayWindow window, Point point, string text) =>
        typeof(CaptureOverlayWindow).GetMethod("ShowAdjustmentBadge", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [point, text]);

    private static void Run(Action<CaptureOverlayWindow> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.Resources["UiFont"] = new FontFamily("Segoe UI");
                    application.Resources["DataFont"] = new FontFamily("Consolas");
                }
                var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
                image.Freeze();
                var window = new CaptureOverlayWindow(new ScreenSnapshot(new System.Drawing.Rectangle(-1000, -700, 1000, 700), image));
                try
                {
                    var root = (Grid)window.FindName("Root");
                    root.Measure(new Size(1000, 700));
                    root.Arrange(new Rect(0, 0, 1000, 700));
                    action(window);
                }
                finally { window.Close(); }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Adjustment badge test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
