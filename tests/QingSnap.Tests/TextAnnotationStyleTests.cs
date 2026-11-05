using System.Reflection;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class TextAnnotationStyleTests
{
    [Fact]
    public void DefaultsPreserveLegacySemiBoldAndShadow() => RunSta(() =>
    {
        var (controller, layer) = CreateController();
        var label = AddText(controller, layer, "默认文字", new Point(30, 40));
        Assert.Equal(FontWeights.SemiBold, label.FontWeight);
        var effect = Assert.IsType<DropShadowEffect>(label.Effect);
        Assert.Equal(2, effect.BlurRadius);
        Assert.Equal(1, effect.ShadowDepth);
        Assert.Equal(0.9, effect.Opacity);
    });

    [Fact]
    public void NewEditorContentAndStylesCommitAsOneUndoOperation() => RunSta(() =>
    {
        var (controller, layer) = CreateController();
        controller.ActiveTool = CaptureAnnotationTool.Text;
        controller.Begin(new Point(30, 40));
        var editor = layer.Children.OfType<TextBox>().Single();
        editor.Text = "中文与 Latin";
        controller.SetTextBold(false);
        controller.SetTextShadow(false);
        Assert.Same(editor, layer.Children.OfType<TextBox>().Single());
        Assert.Equal("中文与 Latin", editor.Text);
        Assert.Equal(FontWeights.Normal, editor.FontWeight);
        Assert.Null(editor.Effect);
        controller.CommitText();
        Assert.Equal(FontWeights.Normal, layer.Children.OfType<TextBlock>().Single().FontWeight);
        Assert.Null(layer.Children.OfType<TextBlock>().Single().Effect);
        controller.Undo();
        Assert.False(controller.HasAnnotations);
        Assert.False(controller.CanUndo);
        Assert.True(controller.CurrentTextBold);
        Assert.True(controller.CurrentTextShadow);
        controller.Redo();
        Assert.Equal("中文与 Latin", layer.Children.OfType<TextBlock>().Single().Text);
        Assert.Equal(FontWeights.Normal, layer.Children.OfType<TextBlock>().Single().FontWeight);
        Assert.Null(layer.Children.OfType<TextBlock>().Single().Effect);
    });

    [Fact]
    public void SelectedAndMixedGroupStylesUndoIndependentlyWithoutChangingOtherAnnotations() => RunSta(() =>
    {
        var (controller, layer) = CreateController();
        AddText(controller, layer, "one", new Point(30, 40));
        AddText(controller, layer, "two", new Point(200, 100));
        controller.ActiveTool = CaptureAnnotationTool.Rectangle;
        controller.Begin(new Point(350, 200));
        controller.End(new Point(410, 260));
        Arrange(layer);
        controller.ActiveTool = CaptureAnnotationTool.Select;
        controller.Begin(new Point(1, 1));
        controller.End(new Point(480, 350));
        Assert.Equal(3, controller.SelectionCount);
        controller.SetTextBold(false);
        controller.SetTextShadow(false);
        Assert.All(layer.Children.OfType<TextBlock>(), text => Assert.Equal(FontWeights.Normal, text.FontWeight));
        Assert.All(layer.Children.OfType<TextBlock>(), text => Assert.Null(text.Effect));
        Assert.Contains(layer.Children.OfType<System.Windows.Shapes.Rectangle>(), rectangle => rectangle.Width == 60);
        controller.Undo();
        Assert.All(layer.Children.OfType<TextBlock>(), text => Assert.Equal(FontWeights.Normal, text.FontWeight));
        Assert.All(layer.Children.OfType<TextBlock>(), text => Assert.IsType<DropShadowEffect>(text.Effect));
        Assert.Equal(3, controller.SelectionCount);
        controller.Undo();
        Assert.All(layer.Children.OfType<TextBlock>(), text => Assert.Equal(FontWeights.SemiBold, text.FontWeight));
        controller.Redo();
        controller.Redo();
        Assert.All(layer.Children.OfType<TextBlock>(), text => Assert.Null(text.Effect));
        Assert.Equal(3, controller.SelectionCount);
    });

    [Fact]
    public void ReeditingInheritsStylesAndCombinesContentWithStyleUndo() => RunSta(() =>
    {
        var (controller, layer) = CreateController();
        controller.SetTextBold(false);
        controller.SetTextShadow(false);
        AddText(controller, layer, "before", new Point(30, 40));
        Arrange(layer);
        controller.ActiveTool = CaptureAnnotationTool.Select;
        Assert.True(controller.SelectAt(new Point(35, 45)));
        Assert.True(controller.BeginEditSelectedText());
        var editor = layer.Children.OfType<TextBox>().Single();
        Assert.Equal(FontWeights.Normal, editor.FontWeight);
        Assert.Null(editor.Effect);
        editor.Text = "after";
        controller.SetTextBold(true);
        controller.SetTextShadow(true);
        Assert.Same(editor, layer.Children.OfType<TextBox>().Single());
        controller.CommitText();
        var changed = layer.Children.OfType<TextBlock>().Single();
        Assert.Equal("after", changed.Text);
        Assert.Equal(FontWeights.SemiBold, changed.FontWeight);
        Assert.IsType<DropShadowEffect>(changed.Effect);
        controller.Undo();
        var original = layer.Children.OfType<TextBlock>().Single();
        Assert.Equal("before", original.Text);
        Assert.Equal(FontWeights.Normal, original.FontWeight);
        Assert.Null(original.Effect);
        Assert.Equal(Visibility.Visible, original.Visibility);
        controller.Redo();
        Assert.Equal("after", layer.Children.OfType<TextBlock>().Single().Text);
        Assert.Equal(FontWeights.SemiBold, layer.Children.OfType<TextBlock>().Single().FontWeight);
        Assert.IsType<DropShadowEffect>(layer.Children.OfType<TextBlock>().Single().Effect);
    });

    [Fact]
    public void CancelKeepsOriginalStylesAndStaleFocusEventsCannotCommitNextEditor() => RunSta(() =>
    {
        var (controller, layer) = CreateController();
        AddText(controller, layer, "original", new Point(30, 40));
        Arrange(layer);
        controller.ActiveTool = CaptureAnnotationTool.Select;
        controller.SelectAt(new Point(35, 45));
        controller.BeginEditSelectedText();
        var oldEditor = layer.Children.OfType<TextBox>().Single();
        oldEditor.Text = "discard";
        controller.SetTextBold(false);
        controller.SetTextShadow(false);
        Call(controller, "CancelText");
        var original = layer.Children.OfType<TextBlock>().Single();
        Assert.Equal("original", original.Text);
        Assert.Equal(FontWeights.SemiBold, original.FontWeight);
        Assert.IsType<DropShadowEffect>(original.Effect);
        Assert.Equal(Visibility.Visible, original.Visibility);
        Assert.True(controller.CurrentTextBold);
        Assert.True(controller.CurrentTextShadow);
        Assert.True(controller.BeginEditSelectedText());
        var nextEditor = layer.Children.OfType<TextBox>().Single();
        nextEditor.Text = "still editing";
        oldEditor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, oldEditor, nextEditor)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        Assert.Same(nextEditor, layer.Children.OfType<TextBox>().Single());
        Call(controller, "CancelText");
        controller.Undo();
        Assert.False(controller.HasAnnotations); // Cancel did not add history.
    });

    [Fact]
    public void CopyPastePreservesFontAndEffectAndNewOperationInvalidatesRedo() => RunSta(() =>
    {
        var (controller, layer) = CreateController();
        AddText(controller, layer, "copy", new Point(30, 40));
        Arrange(layer);
        controller.ActiveTool = CaptureAnnotationTool.Select;
        controller.SelectAt(new Point(35, 45));
        controller.SetTextBold(false);
        controller.SetTextShadow(false);
        controller.CopySelected();
        controller.PasteSelected();
        Assert.Equal(2, layer.Children.OfType<TextBlock>().Count());
        Assert.All(layer.Children.OfType<TextBlock>(), text => Assert.Equal(FontWeights.Normal, text.FontWeight));
        Assert.All(layer.Children.OfType<TextBlock>(), text => Assert.Null(text.Effect));
        controller.Undo();
        Assert.True(controller.CanRedo);
        controller.SetTextShadow(true);
        Assert.False(controller.CanRedo);
        Assert.IsType<DropShadowEffect>(layer.Children.OfType<TextBlock>().Single().Effect);
    });

    [Fact]
    public void CapturePaletteKeepsSelectionAndAppliesBothStyleActions() => RunSta(() =>
    {
        EnsureApplication();
        var image = CreateImage();
        var snapshot = new ScreenSnapshot(new System.Drawing.Rectangle(0, 0, 500, 400), image);
        var window = new CaptureOverlayWindow(snapshot, settings: new AppSettings());
        try
        {
            var controller = Get<CaptureAnnotationController>(window, "_annotationController");
            var layer = (Canvas)window.FindName("AnnotationLayer");
            controller.SetBounds(new Rect(0, 0, 500, 400), 500, 400);
            AddText(controller, layer, "selected", new Point(30, 40));
            Arrange(layer);
            controller.ActiveTool = CaptureAnnotationTool.Select;
            controller.SelectAt(new Point(35, 45));
            Call(window, "OnTextClick", window.FindName("TextButton"), new RoutedEventArgs());
            Assert.Equal(CaptureAnnotationTool.Select, controller.ActiveTool);
            Assert.Equal(1, controller.SelectionCount);
            var bold = (Button)window.FindName("TextBoldButton");
            var shadow = (Button)window.FindName("TextShadowButton");
            Assert.False(bold.Focusable);
            Assert.False(shadow.Focusable);
            bold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            shadow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(FontWeights.Normal, layer.Children.OfType<TextBlock>().Single().FontWeight);
            Assert.Null(layer.Children.OfType<TextBlock>().Single().Effect);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinToolbarStylesAndFullImageExportSurviveUndoRedo(bool longImage) => RunSta(() =>
    {
        EnsureApplication();
        var directory = Path.Combine(Path.GetTempPath(), "QingSnap-text-style-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettingsService(directory);
        using var clipboard = new ClipboardService();
        using var ocr = new OcrService(settings);
        var image = CreateImage(longImage ? 1400 : 400);
        var window = new StickyImageWindow(image, "synthetic-text-style", clipboard, ocr,
            new AppSettings(), new System.Drawing.Rectangle(0, 0, 500, 400));
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            Call(window, "OnLoaded", window, new RoutedEventArgs());
            Set(window, "_pinAnnotationToolbar", new Popup());
            Call(window, "BeginPinAnnotations");
            Get<Popup>(window, "_pinAnnotationToolbar").IsOpen = false;
            Set(window, "_pinAnnotationToolbar", null);
            Call(window, "EnsurePinAnnotationToolbar");
            var controller = Get<CaptureAnnotationController>(window, "_pinAnnotations");
            var layer = Get<Canvas>(window, "_pinAnnotationLayer");
            var top = longImage ? 1000 : 40;
            AddText(controller, layer, "Text 阴影", new Point(30, top));
            layer.Measure(new Size(500, image.PixelHeight));
            layer.Arrange(new Rect(0, 0, 500, image.PixelHeight));
            controller.ActiveTool = CaptureAnnotationTool.Select;
            controller.SelectAt(new Point(35, top + 5));
            Call(window, "RefreshPinAnnotationButtons");
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "_pinTextStyleRow").Visibility);
            var bold = Get<Button>(window, "_pinTextBoldButton");
            var shadow = Get<Button>(window, "_pinTextShadowButton");
            Assert.False(bold.Focusable);
            bold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            shadow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var plain = Pixels(window.CreatePinOutput());
            Assert.Equal(FontWeights.Normal, layer.Children.OfType<TextBlock>().Single().FontWeight);
            Assert.Null(layer.Children.OfType<TextBlock>().Single().Effect);
            controller.Undo();
            var withShadow = Pixels(window.CreatePinOutput());
            Assert.False(plain.SequenceEqual(withShadow));
            controller.Redo();
            Assert.Equal(plain, Pixels(window.CreatePinOutput()));
            Assert.True(Pixels(image).All(pixel => pixel == 255));
        }
        finally
        {
            window.Close();
            Directory.Delete(directory, recursive: true);
        }
    });

    private static (CaptureAnnotationController Controller, Canvas Layer) CreateController()
    {
        var layer = new Canvas();
        var image = CreateImage();
        var controller = new CaptureAnnotationController(layer,
            new ScreenSnapshot(new System.Drawing.Rectangle(0, 0, 500, 400), image), new AppSettings());
        controller.SetBounds(new Rect(0, 0, 500, 400), 500, 400);
        return (controller, layer);
    }

    private static TextBlock AddText(CaptureAnnotationController controller, Canvas layer, string text, Point point)
    {
        controller.ActiveTool = CaptureAnnotationTool.Text;
        controller.Begin(point);
        layer.Children.OfType<TextBox>().Single().Text = text;
        controller.CommitText();
        return layer.Children.OfType<TextBlock>().Last();
    }

    private static BitmapSource CreateImage(int height = 400)
    {
        var pixels = new byte[500 * height * 4];
        Array.Fill(pixels, (byte)255);
        var image = BitmapSource.Create(500, height, 96, 96, PixelFormats.Bgra32, null, pixels, 500 * 4);
        image.Freeze();
        return image;
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void Arrange(Canvas layer)
    {
        layer.Measure(new Size(500, 400));
        layer.Arrange(new Rect(0, 0, 500, 400));
        layer.UpdateLayout();
    }

    private static void EnsureApplication()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["UiFont"] = new FontFamily("Segoe UI");
        app.Resources["DataFont"] = new FontFamily("Consolas");
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Text style test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static void Call(object target, string name, params object[] args) => target.GetType()
        .GetMethods(Flags | BindingFlags.DeclaredOnly)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);
}
