using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class OcrLayoutWindowTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void EditorUsesFormattedTextWhilePreservingRawAndUserEdits(string theme) => Run((image, ocr, clipboard, settings) =>
    {
        using var themes = new ThemeService(Application.Current.Resources, Dispatcher.CurrentDispatcher, false);
        themes.Apply(theme);
        var recognized = OcrTextLayoutTests.Columns();
        string? savedIndex = null;
        var window = new OcrResultWindow("synthetic", image, ocr, clipboard, recognizedTextAvailable: text => savedIndex = text);
        try
        {
            Call(window, "ApplyResult", recognized);
            var editor = (TextBox)window.FindName("OcrTextBox");
            Assert.Equal(OcrTextLayoutService.Get(recognized).Text, editor.Text);
            Assert.Equal(recognized.Text, savedIndex);
            Assert.Same(recognized, Get<OcrRecognitionResult>(window, "_rawResult"));
            editor.Text = "用户手动校正后的文字";
            Assert.Equal("用户手动校正后的文字", editor.Text);
            Assert.Equal(recognized.Text, Get<OcrRecognitionResult>(window, "_rawResult").Text);
            var button = (Button)window.FindName("CopyAllButton");
            Assert.True(button.IsEnabled);
            var menu = button.ContextMenu!;
            menu.ApplyTemplate();
            var raw = (MenuItem)window.FindName("CopyRawMenuItem");
            Assert.True(raw.IsEnabled);
            raw.ApplyTemplate();
            Assert.NotNull(raw.Template.FindName("Chrome", raw));
            Assert.NotNull(menu.Template);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PinRangeSelectionFollowsColumnsWithoutSelectingOtherColumn() => Run((image, ocr, clipboard, settings) =>
    {
        var window = new StickyImageWindow(image, "synthetic", clipboard, ocr, settings);
        try
        {
            Call(window, "ApplyOcrResult", OcrTextLayoutTests.Columns());
            Call(window, "SelectWordRange", 0, 2);
            Assert.Equal(new[] { 0, 2 }, Get<HashSet<int>>(window, "_selectedWordIndices").Order());
            Assert.Equal(string.Join(Environment.NewLine, "左一", "左二"), Invoke(window, "BuildSelectedText", false));
            Call(window, "OnPinOcrMenuOpened", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, ((MenuItem)window.FindName("CopyRawSelectedOcrMenuItem")).Visibility);
            Call(window, "SelectWordRange", 2, 0);
            Assert.Equal(new[] { 0, 2 }, Get<HashSet<int>>(window, "_selectedWordIndices").Order());
            Call(window, "SelectWordRange", 0, 99999);
            Assert.Equal(new[] { 0, 2 }, Get<HashSet<int>>(window, "_selectedWordIndices").Order());
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PinRawMenuOnlyAppearsForActualTextSelection() => Run((image, ocr, clipboard, settings) =>
    {
        var window = new StickyImageWindow(image, "synthetic", clipboard, ocr, settings);
        try
        {
            Call(window, "OnPinOcrMenuOpened", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Collapsed, ((MenuItem)window.FindName("CopyRawSelectedOcrMenuItem")).Visibility);
            Call(window, "ApplyOcrResult", OcrTextLayoutTests.Columns());
            Call(window, "SelectWordRange", 0, 3);
            Assert.Equal(OcrTextLayoutTests.Columns().Text, Invoke(window, "BuildSelectedText", true));
            Assert.NotEqual(Invoke(window, "BuildSelectedText", true), Invoke(window, "BuildSelectedText", false));
            Call(window, "OnPinOcrMenuOpened", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, ((MenuItem)window.FindName("CopySelectedOcrMenuItem")).Visibility);
            Get<HashSet<int>>(window, "_selectedWordIndices").Clear();
            Call(window, "OnPinOcrMenuOpened", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Collapsed, ((MenuItem)window.FindName("CopySelectedOcrMenuItem")).Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LayoutAndRawCopyRemainCorrectInFixedViewport() => Run((image, ocr, clipboard, settings) =>
    {
        var window = new StickyImageWindow(image, "synthetic", clipboard, ocr, settings,
            new System.Drawing.Rectangle(-10000, -10000, 800, 600));
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            Call(window, "OnLoaded", window, new RoutedEventArgs());
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Call(window, "EnterFixedViewport");
            Call(window, "SetReaderScale", 2D, false);
            Call(window, "CompletePinMotion", true);
            Call(window, "ApplyOcrResult", OcrTextLayoutTests.Columns());
            Call(window, "SelectWordRange", 0, 2);
            Assert.Equal(string.Join(Environment.NewLine, "左一", "左二"), Invoke(window, "BuildSelectedText", false));
            Assert.Equal(string.Join(Environment.NewLine, "左一", "左二"), Invoke(window, "BuildSelectedText", true));
        }
        finally { window.Close(); }
    });

    private static void Run(Action<BitmapSource, OcrService, ClipboardService, AppSettings> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources["UiFont"] = new FontFamily("Segoe UI");
                application.Resources["DataFont"] = new FontFamily("Consolas");
                var service = new AppSettingsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "QingSnap-layout-window-tests", Guid.NewGuid().ToString("N")));
                using var clipboard = new ClipboardService();
                using var ocr = new OcrService(service);
                var image = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgra32,
                    null, new byte[800 * 600 * 4], 800 * 4);
                image.Freeze();
                action(image, ocr, clipboard, new AppSettings());
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType()
        .GetMethods(Flags | BindingFlags.DeclaredOnly).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args);
    private static void Call(object target, string name, params object[] args) => Invoke(target, name, args);
}
