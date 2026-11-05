using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Infrastructure;
using QingSnap.App.Views;
using Xunit;
using DrawingRectangle = System.Drawing.Rectangle;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class PinZoomPreviewTests
{
    [Fact]
    public void ContinuousVisualUpdatesNeverResizeCarrierOrMutateSource() => Run(() =>
    {
        var source = Source();
        var owner = new Window { Content = source };
        var bounds = new DrawingRectangle(-1200, -100, 1600, 1000);
        using var preview = new PinZoomPreviewWindow(owner, source, bounds);
        var handle = new WindowInteropHelper(preview).EnsureHandle();
        Assert.Null(preview.Owner); // Native owner cloak must not propagate to this carrier.
        Assert.True(NativeMethods.GetWindowRect(handle, out var original));
        Assert.Equal(bounds.Left, original.Left);
        Assert.Equal(bounds.Top, original.Top);
        Assert.Equal(bounds.Width, original.Width);
        Assert.Equal(bounds.Height, original.Height);
        for (var index = 0; index < 100; index++)
        {
            var width = 400 + index * 0.123;
            preview.Present(-400.5, 400.5, width, width * 0.75);
            Assert.True(NativeMethods.GetWindowRect(handle, out var current));
            Assert.Equal(original.Left, current.Left);
            Assert.Equal(original.Top, current.Top);
            Assert.Equal(original.Width, current.Width);
            Assert.Equal(original.Height, current.Height);
            var shape = Get<System.Windows.Shapes.Rectangle>(preview, "_rectangle");
            var dpi = VisualTreeHelper.GetDpi(preview);
            Assert.Equal(width, shape.Width * dpi.DpiScaleX, 6);
            Assert.Equal(width * 0.75, shape.Height * dpi.DpiScaleY, 6);
        }
        Assert.Equal(400, source.Width);
        Assert.Equal(300, source.Height);
        Assert.True(source.RenderTransform.Value.IsIdentity);
        Assert.Same(source, owner.Content);
        owner.Close();
    });

    [Fact]
    public void FreezeDetachesLiveSourceAndDisposalReleasesSurface() => Run(() =>
    {
        var source = Source();
        var owner = new Window { Content = source };
        var preview = new PinZoomPreviewWindow(owner, source, new DrawingRectangle(-20, -30, 800, 600));
        new WindowInteropHelper(preview).EnsureHandle();
        preview.Present(380, 270, 200.25, 150.1875);
        preview.FreezeContent();
        var frozen = Get<BitmapSource>(preview, "_frozenBitmap");
        Assert.True(frozen.IsFrozen);
        Assert.Null(Get<VisualBrush>(preview, "_sourceBrush").Visual);
        var before = Pixels(frozen);
        Assert.Contains(before, value => value != 0);
        source.Background = Brushes.Blue;
        source.UpdateLayout();
        preview.Present(380, 270, 201, 150.75);
        preview.FreezeContent();
        Assert.Same(frozen, Get<BitmapSource>(preview, "_frozenBitmap"));
        Assert.Equal(before, Pixels(frozen));
        preview.Dispose();
        preview.Dispose();
        Assert.False(preview.IsReady);
        Assert.Null(Get<object?>(preview, "_nativeSource"));
        Assert.Null(Get<object?>(preview, "_frozenBitmap"));
        Assert.Null(Get<System.Windows.Shapes.Rectangle>(preview, "_rectangle").Fill);
        Assert.Null(Get<VisualBrush>(preview, "_sourceBrush").Visual);
        Assert.Same(source, owner.Content);
        owner.Close();
    });

    [Theory]
    [InlineData(8193, 10)]
    [InlineData(6000, 4000)]
    public void OversizeCarrierIsRejectedBeforeOpening(int width, int height) => Run(() =>
    {
        var source = Source();
        var owner = new Window { Content = source };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PinZoomPreviewWindow(owner, source, new DrawingRectangle(0, 0, width, height)));
        owner.Close();
    });

    [Fact]
    public void CloseBeforeReadyDoesNotShowOrChangeOwner() => Run(() =>
    {
        var source = Source();
        var owner = new Window { Content = source };
        using var preview = new PinZoomPreviewWindow(owner, source, new DrawingRectangle(0, 0, 800, 600));
        var readyCalls = 0;
        preview.Ready += (_, _) => readyCalls++;
        new WindowInteropHelper(preview).EnsureHandle();
        preview.Close();
        Assert.Equal(0, readyCalls);
        Assert.False(owner.IsVisible);
        Assert.Same(source, owner.Content);
        Assert.Null(Get<object?>(preview, "_nativeSource"));
        owner.Close();
    });

    private static Grid Source()
    {
        var grid = new Grid { Width = 400, Height = 300, Background = Brushes.Red };
        grid.Measure(new Size(400, 300));
        grid.Arrange(new Rect(0, 0, 400, 300));
        grid.UpdateLayout();
        return grid;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static T Get<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
