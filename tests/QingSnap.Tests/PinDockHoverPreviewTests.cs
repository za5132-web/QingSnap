using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class PinDockHoverPreviewTests
{
    [Fact]
    public void LegacyConfigurationDefaultsOffAndOpacityRoundTripsWithoutStartupSideEffects()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.False(legacy.PinDockHoverPreviewEnabled);
        Assert.Equal(80, legacy.PinDockHoverPreviewOpacity);
        var settings = legacy with { PinDockHoverPreviewEnabled = true, PinDockHoverPreviewOpacity = 55 };
        var result = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.True(result.PinDockHoverPreviewEnabled);
        Assert.Equal(55, AppSettingsService.Normalize(result).PinDockHoverPreviewOpacity);
        Assert.Equal("F1", result.CaptureHotkey);
        Assert.Equal("F3", result.PinHotkey);
    }

    [Theory]
    [InlineData(-50, 20)][InlineData(0, 20)][InlineData(20, 20)][InlineData(55, 55)][InlineData(300, 100)]
    public void InvalidOpacityIsClamped(int value, int expected) =>
        Assert.Equal(expected, AppSettingsService.Normalize(new AppSettings { PinDockHoverPreviewOpacity = value }).PinDockHoverPreviewOpacity);

    [Fact]
    public void RealSaveReloadPersistsPreviewWithoutChangingSystemStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "QingSnap-hover-save", Guid.NewGuid().ToString("N"));
        var calls = 0;
        try
        {
            var service = new AppSettingsService(directory, _ => calls++);
            service.Save(service.Current with { PinDockHoverPreviewEnabled = true, PinDockHoverPreviewOpacity = 45 });
            var reloaded = new AppSettingsService(directory, _ => throw new InvalidOperationException("Loading must not change startup."));
            Assert.True(reloaded.Current.PinDockHoverPreviewEnabled);
            Assert.Equal(45, reloaded.Current.PinDockHoverPreviewOpacity);
            Assert.Equal(1, calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void DisabledModeUsesOriginalRestoreAndDoesNotAllocatePreview() => Run(() =>
    {
        using var fixture = new Fixture(false);
        Call(fixture.Pin, "BeginDockHoverPreview", false);
        Assert.Null(Get<object?>(fixture.Pin, "_dockHoverPreview"));
        Call(fixture.Pin, "RestoreFromDock");
        Call(fixture.Pin, "CompletePinMotion", true);
        Assert.False(Get<bool>(fixture.Pin, "_isCollapsed"));
        Assert.Null(Get<object?>(fixture.Pin, "_dockHoverPreview"));
        AssertBounds(fixture.Expanded, fixture.Pin);
    });

    [Fact]
    public void PreviewLeavesDockAndRestorePositionUnchangedAndReturnsOnLeave() => Run(() =>
    {
        using var fixture = new Fixture(true);
        var preview = fixture.OpenPreview();
        Call(fixture.Pin, "CompletePinMotion", true);
        AssertBounds(fixture.Dock, fixture.Pin);
        AssertBounds(fixture.Expanded, preview);
        Assert.True(Get<bool>(fixture.Pin, "_isCollapsed"));
        Assert.Equal(Visibility.Visible, ((Border)fixture.Pin.FindName("CollapsedDock")).Visibility);
        Assert.Equal(0.8, preview.Opacity);
        Assert.False(preview.ShowActivated);
        Assert.False(preview.IsHitTestVisible);
        var handle = new WindowInteropHelper(preview).Handle;
        Assert.NotEqual(0, NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExTransparent);
        Call(fixture.Pin, "HideDockHoverPreview");
        Call(fixture.Pin, "CompletePinMotion", true);
        Assert.Null(Get<object?>(fixture.Pin, "_dockHoverPreview"));
        Assert.Null(preview.Content);
        AssertBounds(fixture.Dock, fixture.Pin);
        Assert.Equal(fixture.Expanded, Get<NativeMethods.NativeRectangle>(fixture.Pin, "_expandedBounds"));
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ClickPromotesCompletedOrInFlightPreviewWithoutRestartingOutwardMotion(bool inFlight) => Run(() =>
    {
        using var fixture = new Fixture(true);
        var preview = fixture.OpenPreview();
        if (!inFlight) Call(fixture.Pin, "CompletePinMotion", true);
        var version = Get<int>(fixture.Pin, "_pinMotionVersion");
        var frame = Get<object?>(fixture.Pin, "_pinMotionFrame");
        Call(fixture.Pin, "RestoreFromDock");
        if (inFlight && frame is not null)
        {
            Assert.Equal(version, Get<int>(fixture.Pin, "_pinMotionVersion"));
            Assert.Same(frame, Get<object?>(fixture.Pin, "_pinMotionFrame"));
        }
        Call(fixture.Pin, "CompletePinMotion", true);
        Assert.False(Get<bool>(fixture.Pin, "_isCollapsed"));
        Assert.Equal(Visibility.Collapsed, ((Border)fixture.Pin.FindName("CollapsedDock")).Visibility);
        AssertBounds(fixture.Expanded, fixture.Pin);
        Assert.Equal(1, preview.Opacity);
        Assert.Equal("None", Get<object>(fixture.Pin, "_pinMotionKind").ToString());
        Call(fixture.Pin, "FinishDockHoverHandoff");
        Assert.Null(Get<object?>(fixture.Pin, "_dockHoverPreview"));
        Assert.Null(Get<object?>(fixture.Pin, "_dockHoverHandoffRendering"));
    });

    [Fact]
    public void RapidLeaveReenterRetargetsCurrentPreviewWithoutMovingThumbnail() => Run(() =>
    {
        using var fixture = new Fixture(true);
        var preview = fixture.OpenPreview();
        Call(fixture.Pin, "CompletePinMotion", true);
        for (var i = 0; i < 12; i++)
        {
            Call(fixture.Pin, "HideDockHoverPreview");
            if (Get<object?>(fixture.Pin, "_dockHoverPreview") is null) preview = fixture.OpenPreview();
            else Call(fixture.Pin, "BeginDockHoverPreview", false);
            Call(fixture.Pin, "CompletePinMotion", true);
            AssertBounds(fixture.Dock, fixture.Pin);
            AssertBounds(fixture.Expanded, preview);
        }
        Assert.True(Get<bool>(fixture.Pin, "_dockHoverWanted"));
        Call(fixture.Pin, "DisposeDockHoverPreview");
        Assert.Null(Get<object?>(fixture.Pin, "_pinMotionFrame"));
    });

    [Fact]
    public void SettingsChangeOpacityAndDisableReleaseEverythingWithoutTouchingOriginalOpacity() => Run(() =>
    {
        using var fixture = new Fixture(true);
        fixture.Pin.Opacity = 0.95;
        var preview = fixture.OpenPreview();
        Call(fixture.Pin, "CompletePinMotion", true);
        fixture.SetSettings(new AppSettings { PinDockHoverPreviewEnabled = true, PinDockHoverPreviewOpacity = 35 });
        Assert.Equal(0.35, preview.Opacity);
        Assert.Equal(0.95, fixture.Pin.Opacity);
        fixture.SetSettings(new AppSettings { PinDockHoverPreviewEnabled = false });
        Assert.Null(Get<object?>(fixture.Pin, "_dockHoverPreview"));
        Assert.Null(Get<object?>(fixture.Pin, "_pinMotionFrame"));
        Assert.True(Get<bool>(fixture.Pin, "_isCollapsed"));
        AssertBounds(fixture.Dock, fixture.Pin);
    });

    [Fact]
    public void CloseDuringPreviewOrHandoffUnsubscribesAndReleasesImage() => Run(() =>
    {
        using var fixture = new Fixture(true);
        var preview = fixture.OpenPreview();
        Call(fixture.Pin, "RestoreFromDock");
        Call(fixture.Pin, "CompletePinMotion", true);
        fixture.Pin.Close();
        Assert.Null(preview.Content);
        Assert.Null(Get<object?>(fixture.Pin, "_dockHoverPreview"));
        Assert.Null(Get<object?>(fixture.Pin, "_pinMotionFrame"));
        Assert.Null(Get<object?>(fixture.Pin, "_dockHoverHandoffRendering"));
        var subscribers = Get<EventHandler?>(fixture.Settings, "SettingsChanged");
        Assert.DoesNotContain(subscribers?.GetInvocationList() ?? [], handler => ReferenceEquals(handler.Target, fixture.Pin));
    });

    [Fact]
    public void MCommitsPreviewAndDisablingDuringHandoffKeepsRestoredPin() => Run(() =>
    {
        using var fixture = new Fixture(true);
        fixture.OpenPreview();
        Call(fixture.Pin, "ToggleCollapsed");
        Call(fixture.Pin, "CompletePinMotion", true);
        Assert.False(Get<bool>(fixture.Pin, "_isCollapsed"));
        fixture.SetSettings(new AppSettings());
        Assert.Null(Get<object?>(fixture.Pin, "_dockHoverPreview"));
        AssertBounds(fixture.Expanded, fixture.Pin);
    });

    [Fact]
    public void LeaveBeforeFirstFrameCancelsWithoutShowingOrLeakingPreview() => Run(() =>
    {
        using var fixture = new Fixture(true);
        Call(fixture.Pin, "BeginDockHoverPreview", false);
        var preview = Get<PinDockHoverPreviewWindow>(fixture.Pin, "_dockHoverPreview");
        Assert.False(preview.IsReady);
        Call(fixture.Pin, "HideDockHoverPreview");
        Assert.Null(preview.Content);
        Assert.Null(Get<object?>(fixture.Pin, "_dockHoverPreview"));
        AssertBounds(fixture.Dock, fixture.Pin);
    });

    [Fact]
    public void MultiplePinsKeepIndependentPreviewAndClosingOneDoesNotAffectTheOther() => Run(() =>
    {
        using var first = new Fixture(true);
        using var second = new Fixture(true);
        var one = first.OpenPreview();
        var two = second.OpenPreview();
        Assert.NotSame(one, two);
        first.Pin.Close();
        Assert.Null(one.Content);
        Assert.NotNull(two.Content);
        AssertBounds(second.Dock, second.Pin);
    });

    [Fact]
    public void SnapshotContainsAnnotationsAndReaderCropRatherThanFullImage() => Run(() =>
    {
        using var fixture = new Fixture(true);
        var area = new NativeMethods.NativeRectangle { Right = 600, Bottom = 400 };
        var before = (BitmapSource)Call(fixture.Pin, "CreateDockHoverSnapshot", area)!;
        var canvas = Get<Canvas>(fixture.Pin, "_pinAnnotationLayer");
        var source = Get<BitmapSource>(fixture.Pin, "_image");
        var controller = new CaptureAnnotationController(canvas,
            new ScreenSnapshot(new System.Drawing.Rectangle(0, 0, 800, 600), source), new AppSettings());
        controller.SetBounds(new Rect(0, 0, 800, 600), 800, 600);
        controller.ActiveTool = CaptureAnnotationTool.Rectangle;
        controller.Begin(new Point(100, 100)); controller.End(new Point(300, 300));
        Set(fixture.Pin, "_pinAnnotations", controller);
        var annotated = (BitmapSource)Call(fixture.Pin, "CreateDockHoverSnapshot", area)!;
        Assert.NotEqual(Pixel(before, 166, 67), Pixel(annotated, 166, 67));
        Set(fixture.Pin, "_dockHoverSourceRect", new Rect(0, 300, 800, 300));
        var crop = (BitmapSource)Call(fixture.Pin, "CreateDockHoverSnapshot", area)!;
        Assert.NotEqual(Pixel(before, 300, 130), Pixel(crop, 300, 130));
    });

    [Theory]
    [InlineData(false, 96)][InlineData(true, 144)][InlineData(true, 192)]
    public void SnapshotIsBoundedAndRetainsReaderViewport(bool reader, int dpi) => Run(() =>
    {
        using var fixture = new Fixture(true, dpi);
        var expected = new Rect(20, 100, 300, 200);
        if (reader) Set(fixture.Pin, "_dockHoverSourceRect", expected);
        var bitmap = (BitmapSource)Call(fixture.Pin, "CreateDockHoverSnapshot",
            new NativeMethods.NativeRectangle { Left = -5000, Top = -3000, Right = 3000, Bottom = 2000 })!;
        Assert.True(bitmap.IsFrozen);
        Assert.InRange(bitmap.PixelWidth, 1, 2048);
        Assert.InRange(bitmap.PixelHeight, 1, 2048);
        if (reader) Assert.Equal(expected, Get<Rect>(fixture.Pin, "_dockHoverSourceRect"));
    });

    [Theory]
    [InlineData("Dark", 1)][InlineData("Light", 1)]
    [InlineData("Dark", 1.5)][InlineData("Light", 1.5)]
    [InlineData("Dark", 2)][InlineData("Light", 2)]
    public void SettingsSliderUsesThemeAndDisplaysPercentage(string theme, double scale) => Run(() =>
    {
        using var fixture = new Fixture(true);
        using var update = new UpdateService(fixture.DirectoryPath);
        var window = new SettingsWindow(fixture.Settings, fixture.Ocr, update, () => { }, () => [], _ => { });
        try
        {
            window.Resources.MergedDictionaries.Add(ThemeService.LoadDictionary(theme));
            var check = (CheckBox)window.FindName("PinHoverPreviewCheck");
            var slider = (Slider)window.FindName("PinHoverOpacitySlider");
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(720, 740)); root.Arrange(new Rect(0, 0, 720, 740)); root.UpdateLayout();
            slider.ApplyTemplate();
            Assert.True(check.IsChecked);
            Assert.NotNull(slider.Template.FindName("PART_Track", slider));
            Assert.Equal(20, slider.Minimum); Assert.Equal(100, slider.Maximum);
            slider.Value = 45;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(45, ((Track)slider.Template.FindName("PART_Track", slider)).Value);
            Assert.Equal("45%", ((TextBlock)window.FindName("PinHoverOpacityText")).Text);
            var card = (Border)window.FindName("PinHoverPreviewSection");
            Assert.True(card.ActualWidth > 500);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawRectangle(new VisualBrush(card) { Stretch = Stretch.Fill }, null,
                    new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth * scale),
                (int)Math.Ceiling(card.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var previewDirectory = Environment.GetEnvironmentVariable("PIN_HOVER_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(previewDirectory))
            {
                Directory.CreateDirectory(previewDirectory);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(previewDirectory, $"settings-pin-hover-{theme}-{scale:0.0}.png"));
                encoder.Save(stream);
            }
            check.IsChecked = false;
            Assert.False(slider.IsEnabled);
        }
        finally { window.Close(); }
    });

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "QingSnap-dock-preview-tests", Guid.NewGuid().ToString("N"));
        public AppSettingsService Settings { get; }
        public ClipboardService Clipboard { get; } = new();
        public OcrService Ocr { get; }
        public StickyImageWindow Pin { get; }
        public NativeMethods.NativeRectangle Dock { get; } = new() { Left = -10100, Top = -10100, Right = -10042, Bottom = -10056 };
        public NativeMethods.NativeRectangle Expanded { get; } = new() { Left = -10800, Top = -10500, Right = -10200, Bottom = -10100 };
        public Fixture(bool enabled, int dpi = 96)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, "settings.json"), JsonSerializer.Serialize(
                new AppSettings { PinDockHoverPreviewEnabled = enabled }));
            Settings = new AppSettingsService(DirectoryPath);
            Ocr = new OcrService(Settings);
            var pixels = new byte[800 * 600 * 4]; Array.Fill(pixels, (byte)255);
            for (var y = 300; y < 600; y++)
                for (var x = 0; x < 800; x++) { var i = (y * 800 + x) * 4; pixels[i + 1] = 80; pixels[i + 2] = 30; }
            var image = BitmapSource.Create(800, 600, dpi, dpi, PixelFormats.Bgra32, null, pixels, 800 * 4); image.Freeze();
            Pin = new StickyImageWindow(image, "synthetic-dock", Clipboard, Ocr, Settings.Current,
                settingsService: Settings);
            Pin.Resources.MergedDictionaries.Add(ThemeService.LoadDictionary("Dark"));
            var handle = new WindowInteropHelper(Pin).EnsureHandle();
            Set(Pin, "_expandedBounds", Expanded);
            Call(Pin, "PrepareCollapsedVisualState");
            NativeMethods.SetWindowPos(handle, 0, Dock.Left, Dock.Top, Dock.Width, Dock.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoOwnerZOrder);
        }
        public PinDockHoverPreviewWindow OpenPreview()
        {
            Call(Pin, "BeginDockHoverPreview", false);
            var preview = Get<PinDockHoverPreviewWindow>(Pin, "_dockHoverPreview");
            new WindowInteropHelper(preview).EnsureHandle();
            Call(preview, "OnFirstContentRendered", preview, EventArgs.Empty);
            return preview;
        }
        public void SetSettings(AppSettings settings)
        {
            typeof(AppSettingsService).GetProperty("Current")!.SetValue(Settings, settings);
            Call(Pin, "OnDockHoverSettingsChanged", Settings, EventArgs.Empty);
        }
        public void Dispose() { Pin.Close(); Ocr.Dispose(); Clipboard.Dispose(); Directory.Delete(DirectoryPath, true); }
    }
    private static void AssertBounds(NativeMethods.NativeRectangle expected, Window window)
    {
        Assert.True(NativeMethods.GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds));
        Assert.Equal(expected.Left, bounds.Left); Assert.Equal(expected.Top, bounds.Top);
        Assert.Equal(expected.Width, bounds.Width); Assert.Equal(expected.Height, bounds.Height);
    }
    private static uint Pixel(BitmapSource image, int x, int y)
    { var bytes = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0); return BitConverter.ToUInt32(bytes); }
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources["UiFont"] = new FontFamily("Segoe UI"); app.Resources["DataFont"] = new FontFamily("Consolas");
                action();
            }
            catch (Exception exception) { failure = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
