using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Controls;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class CaptionIconAlignmentTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void CaptionGlyphsShareTheSameVerticalCenterAtCommonScales(double scale)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                double? referenceCenter = null;
                foreach (var kind in new[] { QingSnapIconKind.Minimize, QingSnapIconKind.Maximize,
                             QingSnapIconKind.Restore, QingSnapIconKind.Close })
                {
                    var icon = new QingSnapIcon { Kind = kind, Width = 16, Height = 16, Foreground = Brushes.White };
                    icon.Measure(new Size(16, 16));
                    icon.Arrange(new Rect(0, 0, 16, 16));
                    icon.UpdateLayout();
                    var size = (int)Math.Round(16 * scale);
                    var image = new RenderTargetBitmap(size, size, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    image.Render(icon);
                    var pixels = new byte[size * size * 4];
                    image.CopyPixels(pixels, size * 4, 0);
                    var first = size;
                    var last = -1;
                    for (var y = 0; y < size; y++)
                    {
                        for (var x = 0; x < size; x++)
                        {
                            if (pixels[(y * size + x) * 4 + 3] <= 16) continue;
                            first = Math.Min(first, y);
                            last = Math.Max(last, y);
                        }
                    }

                    Assert.True(last >= first, $"{kind} did not render.");
                    var center = (first + last + 1) / 2D;
                    referenceCenter ??= center;
                    Assert.InRange(Math.Abs(center - referenceCenter.Value), 0, 0.5);
                    Assert.InRange(Math.Abs(center - size / 2D), 0, 0.5);
                }
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Caption icon rendering did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
