using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Views;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class CaptureOverlaySelectionRegressionTests
{
    private const BindingFlags InstanceFlags = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void FreshWindowUsesEmptySelectionSentinels() => Run(window =>
    {
        foreach (var field in new[] { "_selection", "_smartPreview", "_pendingSmartSelection" })
        {
            Assert.True(Get<Rect>(window, field).IsEmpty, field);
        }
    });

    [Fact]
    public void FullShadeClearsRecalledSelection() => Run(window =>
    {
        Call(window, "LoadLocalSelection", new System.Drawing.Rectangle(100, 100, 250, 180));
        Assert.False(Get<Rect>(window, "_selection").IsEmpty);
        Call(window, "ShowFullShade");
        Assert.True(Get<Rect>(window, "_selection").IsEmpty);
    });

    [Fact]
    public void RecalledSelectionKeepsSizeAfterMoveAndRelease() => Run(window =>
    {
        Call(window, "LoadLocalSelection", new System.Drawing.Rectangle(100, 100, 250, 180));
        var initial = Get<Rect>(window, "_selection");
        Assert.True(initial.Width > 1 && initial.Height > 1);
        Set(window, "_selectionAtDragStart", initial);
        Set(window, "_dragStart", new Point(150, 150));
        Set(window, "_dragMode", Enum.Parse(
            typeof(CaptureOverlayWindow).GetNestedType("DragMode", BindingFlags.NonPublic)!, "Move"));
        Call(window, "ApplyDrag", new Point(180, 170));
        Call(window, "OnMouseLeftButtonUp", window,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });
        var actual = Get<Rect>(window, "_selection");
        Assert.Equal(initial.Width, actual.Width);
        Assert.Equal(initial.Height, actual.Height);
    });

    private static void Run(Action<CaptureOverlayWindow> assertion)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            CaptureOverlayWindow? window = null;
            try
            {
                if (Application.Current is null)
                {
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.Resources["UiFont"] = new FontFamily("Segoe UI");
                    application.Resources["DataFont"] = new FontFamily("Consolas");
                }

                // Synthetic pixels only: never capture the desktop or write history.
                var image = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgra32,
                    null, new byte[800 * 600 * 4], 800 * 4);
                window = new CaptureOverlayWindow(new ScreenSnapshot(
                    new System.Drawing.Rectangle(-800, 0, 800, 600), image))
                {
                    Width = 800,
                    Height = 600
                };
                window.Measure(new Size(800, 600));
                window.Arrange(new Rect(0, 0, 800, 600));
                window.UpdateLayout();
                var surface = (FrameworkElement)window.FindName("Surface");
                surface.Measure(new Size(800, 600));
                surface.Arrange(new Rect(0, 0, 800, 600));
                assertion(window);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static T Get<T>(object target, string field) =>
        (T)target.GetType().GetField(field, InstanceFlags)!.GetValue(target)!;

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, InstanceFlags)!.SetValue(target, value);

    private static void Call(object target, string method, params object[] arguments) =>
        target.GetType().GetMethods(InstanceFlags | BindingFlags.DeclaredOnly)
            .Single(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length)
            .Invoke(target, arguments);
}
