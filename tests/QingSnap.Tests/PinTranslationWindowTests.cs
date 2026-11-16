using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
public sealed class PinTranslationWindowTests
{
    [Theory]
    [InlineData(false, 96)] [InlineData(false, 144)] [InlineData(false, 192)]
    [InlineData(true, 96)] [InlineData(true, 144)]
    public void TranslatesInPlaceAndTogglesWithoutNetworkOrOriginalMutation(bool longImage, int dpi) => Run(async () =>
    {
        using var fixture = new Fixture(longImage, dpi);
        var original = fixture.Window.CreatePinOutput();
        await fixture.Window.TranslatePinAsync(false);
        var document = Get<PinTranslationDocument>(fixture.Window, "_pinTranslationDocument");
        Assert.True(document.Overlay.IsFrozen);
        Assert.True(Get<bool>(fixture.Window, "_pinTranslationVisible"));
        Assert.Equal(Visibility.Visible, ((Image)fixture.Window.FindName("PinTranslationOverlay")).Visibility);
        Assert.Same(document.Overlay, ((Image)fixture.Window.FindName("LongPinTranslationOverlay")).Source);
        Assert.Same(original, fixture.Window.CreatePinOutput());
        var translated = fixture.Window.CreatePinOutput(includeTranslation: true);
        Assert.Equal(original.PixelWidth, translated.PixelWidth);
        Assert.Equal(original.PixelHeight, translated.PixelHeight);
        Assert.NotEqual(PinTranslationLayoutTests.Pixels(original), PinTranslationLayoutTests.Pixels(translated));
        Assert.Contains(Get<OcrRecognitionResult>(fixture.Window, "_ocrResult").Lines.SelectMany(line => line.Words), word => word.Text == "中");
        var requests = fixture.Handler.Count;
        Call(fixture.Window, "TogglePinTranslation");
        Assert.False(Get<bool>(fixture.Window, "_pinTranslationVisible"));
        Assert.Same(fixture.OcrResult, Get<OcrRecognitionResult>(fixture.Window, "_ocrResult"));
        Call(fixture.Window, "TogglePinTranslation");
        Assert.Same(document, Get<PinTranslationDocument>(fixture.Window, "_pinTranslationDocument"));
        Assert.Equal(requests, fixture.Handler.Count);
        Assert.False(((Image)fixture.Window.FindName("PinTranslationOverlay")).IsHitTestVisible);
        var thumbnail = fixture.Window.CreatePinOutput(true, true);
        Assert.InRange(thumbnail.PixelWidth, 1, 180);
        Assert.InRange(thumbnail.PixelHeight, 1, 130);
    });

    [Fact]
    public void SelectedTextIsTheOnlyTextSentAndOnlyItsRegionChanges() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96);
        Get<HashSet<int>>(fixture.Window, "_selectedWordIndices").Add(100);
        await fixture.Window.TranslatePinAsync(true);
        Assert.Single(fixture.Sent);
        Assert.Equal("Second foreign paragraph", fixture.Sent[0]);
        var document = Get<PinTranslationDocument>(fixture.Window, "_pinTranslationDocument");
        Assert.Single(document.Patches);
        Assert.Equal(160, document.Patches[0].Bounds.Y);
        var output = fixture.Window.CreatePinOutput(includeTranslation: true);
        Assert.Equal(PinTranslationLayoutTests.Pixel(fixture.Image, 50, 50), PinTranslationLayoutTests.Pixel(output, 50, 50));
    });

    [Fact]
    public void CtrlTIsLocalAndDoesNotInterceptAnnotationTextOrOtherKeys() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96);
        await fixture.Window.TranslatePinAsync(false);
        Assert.True((bool)Call(fixture.Window, "HandlePinTranslationShortcut", Key.T, ModifierKeys.Control)!);
        Assert.False(Get<bool>(fixture.Window, "_pinTranslationVisible"));
        foreach (var key in new[] { Key.F1, Key.F2, Key.F3, Key.Escape, Key.Enter, Key.Z, Key.Y, Key.C, Key.V })
            Assert.False((bool)Call(fixture.Window, "HandlePinTranslationShortcut", key, ModifierKeys.Control)!);
        Assert.False((bool)Call(fixture.Window, "HandlePinTranslationShortcut", Key.T, ModifierKeys.None)!);
        Set(fixture.Window, "_isEditingPin", true);
        Assert.False((bool)Call(fixture.Window, "HandlePinTranslationShortcut", Key.T, ModifierKeys.Control)!);
        Set(fixture.Window, "_isEditingPin", false);
    });

    [Fact]
    public void CancelAndCloseDoNotApplyLateResponses() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96) { DelayRequests = true };
        var first = fixture.Window.TranslatePinAsync(false);
        await fixture.Started.Task;
        Call(fixture.Window, "TogglePinTranslation");
        await first;
        Assert.Null(Get<object?>(fixture.Window, "_pinTranslationDocument"));
        Assert.False(Get<bool>(fixture.Window, "_pinTranslationBusy"));
        var next = fixture.Window.TranslatePinAsync(false);
        fixture.Window.Close();
        await next;
        Assert.Null(((Image)fixture.Window.FindName("PinTranslationOverlay")).Source);
    });

    [Fact]
    public void MappingFailureLeavesOriginalAndNoTranslationWindowOpens() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96) { WrongMapping = true };
        await fixture.Window.TranslatePinAsync(false);
        Assert.Null(Get<object?>(fixture.Window, "_pinTranslationDocument"));
        Assert.Same(fixture.Image, fixture.Window.CreatePinOutput());
        Assert.Contains("不匹配", ((TextBlock)fixture.Window.FindName("FeedbackText")).Text);
        Assert.Empty(Get<List<Window>>(fixture.Coordinator, "_windows"));
    });

    [Fact]
    public void AnnotationEditsInvalidateTranslationAndSettingsDisableRestoresOriginal() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96);
        await fixture.Window.TranslatePinAsync(false);
        fixture.Settings.Save(fixture.Settings.Current with { TranslationEnabled = false });
        Assert.False(Get<bool>(fixture.Window, "_pinTranslationVisible"));
        Assert.Same(fixture.OcrResult, Get<OcrRecognitionResult>(fixture.Window, "_ocrResult"));
        Call(fixture.Window, "InvalidatePinRecognition");
        Assert.Null(Get<object?>(fixture.Window, "_pinTranslationDocument"));
        Assert.Null(Get<object?>(fixture.Window, "_ocrResult"));
        Assert.Null(((Image)fixture.Window.FindName("LongPinTranslationOverlay")).Source);
    });

    [Fact]
    public void ReaderOverviewAndFixedViewportUseTheSameTranslationLayer() => Run(async () =>
    {
        using var fixture = new Fixture(true, 96);
        new WindowInteropHelper(fixture.Window).EnsureHandle();
        Call(fixture.Window, "OnLoaded", fixture.Window, new RoutedEventArgs());
        await fixture.Window.TranslatePinAsync(false);
        var drawing = ((Image)fixture.Window.FindName("LongPinTranslationOverlay")).Source;
        var output = PinTranslationLayoutTests.Pixels(fixture.Window.CreatePinOutput(includeTranslation: true));
        Call(fixture.Window, "EnterOverviewMode");
        Assert.Same(drawing, ((Image)fixture.Window.FindName("PinTranslationOverlay")).Source);
        Call(fixture.Window, "EnterReaderMode", false);
        Assert.Same(drawing, ((Image)fixture.Window.FindName("LongPinTranslationOverlay")).Source);
        Assert.Equal(output, PinTranslationLayoutTests.Pixels(fixture.Window.CreatePinOutput(includeTranslation: true)));
        Assert.Equal(1, fixture.Handler.Count);
    });

    [Fact]
    public void DisabledTranslationAndMissingOcrFailInlineWithoutRequestsOrPopup() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96);
        fixture.Settings.Save(fixture.Settings.Current with { TranslationEnabled = false });
        await fixture.Window.TranslatePinAsync(false);
        Assert.Equal(0, fixture.Handler.Count);
        fixture.Settings.Save(fixture.Settings.Current with { TranslationEnabled = true });
        Set(fixture.Window, "_ocrResult", null);
        await fixture.Window.TranslatePinAsync(false);
        Assert.Contains("OCR", ((TextBlock)fixture.Window.FindName("FeedbackText")).Text);
        Assert.Equal(0, fixture.Handler.Count);
        Assert.Empty(Get<List<Window>>(fixture.Coordinator, "_windows"));
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DockAndRestoreRetainsTranslationAndThumbnailWithoutNewRequests(bool longImage) => Run(async () =>
    {
        using var fixture = new Fixture(longImage, 96);
        new WindowInteropHelper(fixture.Window).EnsureHandle();
        Call(fixture.Window, "OnLoaded", fixture.Window, new RoutedEventArgs());
        await fixture.Window.TranslatePinAsync(false);
        var document = Get<PinTranslationDocument>(fixture.Window, "_pinTranslationDocument");
        var thumbnail = ((Image)fixture.Window.FindName("CollapsedThumbnail")).Source;
        Call(fixture.Window, "ToggleCollapsed");
        Call(fixture.Window, "CompletePinMotion", true);
        Assert.True(Get<bool>(fixture.Window, "_isCollapsed"));
        Call(fixture.Window, "RestoreFromDock");
        Call(fixture.Window, "CompletePinMotion", true);
        Assert.False(Get<bool>(fixture.Window, "_isCollapsed"));
        Assert.True(Get<bool>(fixture.Window, "_pinTranslationVisible"));
        Assert.Same(document, Get<PinTranslationDocument>(fixture.Window, "_pinTranslationDocument"));
        Assert.Same(thumbnail, ((Image)fixture.Window.FindName("CollapsedThumbnail")).Source);
        Assert.Equal(1, fixture.Handler.Count);
    });

    [Fact]
    public void InvalidationStopsPendingWorkAndClearsPersistentProgress() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96) { DelayRequests = true };
        var request = fixture.Window.TranslatePinAsync(false);
        await fixture.Started.Task;
        Call(fixture.Window, "InvalidatePinRecognition");
        await request;
        Assert.False(Get<bool>(fixture.Window, "_pinTranslationBusy"));
        Assert.Equal(Visibility.Collapsed, ((Border)fixture.Window.FindName("FeedbackBadge")).Visibility);
        Assert.Null(Get<object?>(fixture.Window, "_pinTranslationDocument"));
    });

    [Fact]
    public void CompletedPreloadCanTranslateWithoutRequiringAnotherOcrInstallation() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96);
        Set(fixture.Window, "_ocrResult", null);
        Set(fixture.Window, "_ocrPreloadTask", Task.FromResult(fixture.OcrResult));
        Assert.False(fixture.Ocr.IsOcrAvailable);
        await fixture.Window.TranslatePinAsync(false);
        Assert.True(Get<bool>(fixture.Window, "_pinTranslationVisible"));
        Call(fixture.Window, "ApplyOcrResult", fixture.OcrResult);
        Assert.NotSame(fixture.OcrResult, Get<OcrRecognitionResult>(fixture.Window, "_ocrResult"));
        Assert.Equal(1, fixture.Handler.Count);
    });

    [Fact]
    public void TargetLanguageMenuOnlySelectsAndDoesNotSend() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96);
        var menu = (MenuItem)fixture.Window.FindName("TranslateTargetMenuItem");
        Assert.Equal(TranslationLanguages.All.Count, menu.Items.Count);
        Assert.Single(menu.Items.OfType<MenuItem>(), item => item.IsChecked);
        var english = menu.Items.OfType<MenuItem>().Single(item => (string)item.Tag == "en");
        english.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("en", Get<string>(fixture.Window, "_pinTranslationTargetLanguage"));
        Assert.True(english.IsChecked);
        Assert.Single(menu.Items.OfType<MenuItem>(), item => item.IsChecked);
        Assert.Contains("英语", ((MenuItem)fixture.Window.FindName("TranslatePinMenuItem")).Header.ToString());
        Assert.Equal(0, fixture.Handler.Count);
        Assert.Equal("zh", fixture.Settings.Current.TranslationTargetLanguage);
        await Task.CompletedTask;
    });

    [Fact]
    public void SwitchingTargetsReusesTwoLanguageCachesWithoutCrossLanguageResults() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96);
        await fixture.Window.TranslatePinAsync(false);
        var chinese = Get<PinTranslationDocument>(fixture.Window, "_pinTranslationDocument");
        Call(fixture.Window, "SelectPinTranslationTarget", "en", true);
        Assert.False(Get<bool>(fixture.Window, "_pinTranslationVisible"));
        Assert.Null(Get<object?>(fixture.Window, "_pinTranslationDocument"));
        Assert.Equal(1, fixture.Handler.Count);
        await fixture.Window.TranslatePinAsync(false);
        var english = Get<PinTranslationDocument>(fixture.Window, "_pinTranslationDocument");
        Assert.Equal("en", english.TargetLanguage);
        Assert.Contains("This is a translated sentence.", english.Text);
        Assert.Contains("This is a translated sentence.", OcrTextSelectionBuilder.Build(english.SelectionResult,
            english.SelectionResult.Lines.SelectMany(line => line.Words).Select(word => word.Index).ToHashSet()));
        Assert.Equal(new[] { "zh", "en" }, fixture.SentTargets);
        Assert.Contains("这是原有的中文", fixture.Sent.Last());
        Call(fixture.Window, "SelectPinTranslationTarget", "zh", true);
        await fixture.Window.TranslatePinAsync(false);
        Assert.Same(chinese, Get<PinTranslationDocument>(fixture.Window, "_pinTranslationDocument"));
        Assert.Equal(2, fixture.Handler.Count);
        Assert.Equal(2, Get<PinTranslationCache>(fixture.Window, "_pinTranslationCache").Count);
    });

    [Fact]
    public void LanguageChangeCancelsInFlightWorkAndNeverAppliesOldTarget() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96) { DelayRequests = true };
        var pending = fixture.Window.TranslatePinAsync(false);
        await fixture.Started.Task;
        Call(fixture.Window, "SelectPinTranslationTarget", "en", true);
        await pending;
        Assert.False(Get<bool>(fixture.Window, "_pinTranslationBusy"));
        Assert.Null(Get<object?>(fixture.Window, "_pinTranslationDocument"));
        fixture.DelayRequests = false;
        await fixture.Window.TranslatePinAsync(false);
        Assert.Equal("en", Get<PinTranslationDocument>(fixture.Window, "_pinTranslationDocument").TargetLanguage);
    });

    [Fact]
    public void DefaultLanguageSettingUpdatesUnmodifiedPinsButKeepsLocalChoice() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96);
        fixture.Settings.Save(fixture.Settings.Current with { TranslationTargetLanguage = "en" });
        Assert.Equal("en", Get<string>(fixture.Window, "_pinTranslationTargetLanguage"));
        Call(fixture.Window, "SelectPinTranslationTarget", "jp", true);
        fixture.Settings.Save(fixture.Settings.Current with { TranslationTargetLanguage = "fra" });
        Assert.Equal("jp", Get<string>(fixture.Window, "_pinTranslationTargetLanguage"));
        Assert.Equal(0, fixture.Handler.Count);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData("Dark")][InlineData("Light")]
    public void LanguageSubmenuUsesExistingThemeAndCustomTemplate(string theme) => Run(async () =>
    {
        // The application loads its palette before creating pin/menu controls.
        var palette = ThemeService.LoadDictionary(theme);
        Application.Current.Resources.MergedDictionaries.Add(palette);
        try
        {
            using var fixture = new Fixture(false, 96);
            fixture.Window.Resources.MergedDictionaries.Add(ThemeService.LoadDictionary(theme));
            var menu = (MenuItem)fixture.Window.FindName("TranslateTargetMenuItem");
            menu.ApplyTemplate();
            Assert.True(menu.HasItems);
            var popup = (System.Windows.Controls.Primitives.Popup)menu.Template.FindName("PART_Popup", menu);
            Assert.NotNull(popup);
            // Generate the submenu's visual tree, as opening it would do.
            var submenu = (FrameworkElement)popup.Child;
            submenu.Resources.MergedDictionaries.Add(ThemeService.LoadDictionary(theme));
            submenu.Measure(new Size(200, 400));
            submenu.Arrange(new Rect(0, 0, 200, 400));
            submenu.UpdateLayout();
            Assert.Equal(Visibility.Visible, ((System.Windows.Shapes.Path)menu.Template.FindName("SubmenuArrow", menu)).Visibility);
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                item.ApplyTemplate();
                Assert.True(item.OverridesDefaultStyle);
                Assert.Equal(((SolidColorBrush)fixture.Window.FindResource(item.IsChecked ? "ThemeAccentBrush" : "ThemeTextBrush")).Color,
                    ((SolidColorBrush)item.Foreground).Color);
            }
            Assert.Equal(0, fixture.Handler.Count);
            await Task.CompletedTask;
        }
        finally { Application.Current.Resources.MergedDictionaries.Remove(palette); }
    });

    [Fact]
    public void ClosingUnsubscribesSettingsAndDropsOverlayReferences() => Run(async () =>
    {
        using var fixture = new Fixture(false, 96);
        await fixture.Window.TranslatePinAsync(false);
        var subscriptions = Get<EventHandler>(fixture.Settings, "SettingsChanged");
        Assert.Contains(subscriptions.GetInvocationList(), item => ReferenceEquals(item.Target, fixture.Window));
        fixture.Window.Close();
        var remaining = Get<EventHandler?>(fixture.Settings, "SettingsChanged");
        Assert.DoesNotContain(remaining?.GetInvocationList() ?? [], item => ReferenceEquals(item.Target, fixture.Window));
        Assert.Null(Get<object?>(fixture.Window, "_pinTranslationDocument"));
        Assert.Null(((Image)fixture.Window.FindName("PinTranslationOverlay")).Source);
    });

    [Fact]
    public void RenderSyntheticBeforeAndAfterPreviewWhenRequested() => Run(async () =>
    {
        var directory = Environment.GetEnvironmentVariable("QINGSNAP_INLINE_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var fixture = new Fixture(false, 96);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(fixture.Image, new Rect(0, 0, 800, 600));
            context.DrawText(Text("Plan your next idea", 28), new Point(40, 43));
            context.DrawText(Text("Second foreign paragraph", 22), new Point(40, 158));
            context.DrawText(Text("这是原有的中文，不需要翻译。", 22), new Point(40, 290));
        }
        var original = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
        original.Render(visual); original.Freeze();
        var blocks = PinTranslationLayout.Plan(fixture.OcrResult);
        var translated = PinTranslationLayout.Render(original, fixture.OcrResult, blocks,
            [new(0, "构思你的下一个灵感"), new(1, "第二段外语文字")]);
        var output = PinTranslationLayoutTests.Compose(original, translated);
        var comparison = new DrawingVisual();
        using (var context = comparison.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1640, 660));
            context.DrawText(Text("原图", 24), new Point(20, 12));
            context.DrawText(Text("原位翻译 · 内置模拟译文，未调用百度", 24), new Point(840, 12));
            context.DrawImage(original, new Rect(0, 60, 800, 600));
            context.DrawImage(output, new Rect(840, 60, 800, 600));
        }
        var bitmap = new RenderTargetBitmap(1640, 660, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(comparison);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, "pin-inline-translation-demo.png")); encoder.Save(stream);
        await Task.CompletedTask;
    });

    [Fact]
    public void RenderSyntheticMultilingualPreviewWhenRequested() => Run(async () =>
    {
        var directory = Environment.GetEnvironmentVariable("QINGSNAP_MULTILANG_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var palette = ThemeService.LoadDictionary("Dark");
        Application.Current.Resources.MergedDictionaries.Add(palette);
        try
        {
            using var fixture = new Fixture(false, 96);
            Call(fixture.Window, "SelectPinTranslationTarget", "en", true);
            var menu = (MenuItem)fixture.Window.FindName("TranslateTargetMenuItem");
            menu.ApplyTemplate();
            var popup = (System.Windows.Controls.Primitives.Popup)menu.Template.FindName("PART_Popup", menu);
            var menuVisual = (FrameworkElement)popup.Child;
            menuVisual.Resources.MergedDictionaries.Add(ThemeService.LoadDictionary("Dark"));
            menuVisual.Measure(new Size(220, 342));
            menuVisual.Arrange(new Rect(0, 0, 220, 342));
            menuVisual.UpdateLayout();
            var menuBitmap = new RenderTargetBitmap(220, 342, 96, 96, PixelFormats.Pbgra32);
            menuBitmap.Render(menuVisual);

            (BitmapSource Original, BitmapSource Translated) Sample(string first, string second, string target,
                string translatedFirst, string translatedSecond)
            {
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 500, 200));
                    context.DrawText(Text(first, 26), new Point(25, 40));
                    context.DrawText(Text(second, 20), new Point(25, 110));
                }
                var original = new RenderTargetBitmap(500, 200, 96, 96, PixelFormats.Pbgra32);
                original.Render(visual); original.Freeze();
                var ocr = PinTranslationLayoutTests.Ocr(500, 200,
                    PinTranslationLayoutTests.Line(0, first, new(25, 40, 450, 48)),
                    PinTranslationLayoutTests.Line(1, second, new(25, 110, 450, 35)));
                var result = PinTranslationLayout.Render(original, ocr, PinTranslationLayout.Plan(ocr, targetLanguage: target),
                    [new(0, translatedFirst), new(1, translatedSecond)], targetLanguage: target);
                Assert.All(result.Patches, patch => Assert.False(patch.IsTruncated));
                return (original, PinTranslationLayoutTests.Compose(original, result));
            }
            var chinese = Sample("Plan your next idea", "Keep useful images on your desktop.", "zh",
                "构思你的下一个灵感", "把有用的图片留在桌面。");
            var english = Sample("截图里的文字，也能中英互译", "选择目标语言后，按 Ctrl+T 开始翻译。", "en",
                "Translate the text in your screenshots.", "Choose a language, then press Ctrl+T.");
            var canvas = new DrawingVisual();
            using (var context = canvas.RenderOpen())
            {
                var accent = (Brush)palette["ThemeAccentBrush"];
                var normal = (Brush)palette["ThemeTextBrush"];
                var muted = (Brush)palette["ThemeMutedTextBrush"];
                void Label(string text, double size, double x, double y, Brush brush)
                { var label = Text(text, size); label.SetForegroundBrush(brush); context.DrawText(label, new Point(x, y)); }
                context.DrawRectangle((Brush)palette["ThemeWindowBrush"], null, new Rect(0, 0, 1200, 720));
                Label("QingSnap · 多语种原位翻译", 28, 28, 20, accent);
                Label("原文自动识别 · 选择目标语言后主动翻译 · 原图保留", 15, 28, 65, muted);
                Label("英语 → 简体中文", 21, 28, 112, normal);
                Label("原图", 13, 28, 148, muted); Label("原位译图", 13, 475, 148, accent);
                context.DrawImage(chinese.Original, new Rect(28, 176, 425, 170));
                context.DrawImage(chinese.Translated, new Rect(475, 176, 425, 170));
                Label("简体中文 → 英语", 21, 28, 378, normal);
                Label("原图", 13, 28, 414, muted); Label("原位译图", 13, 475, 414, accent);
                context.DrawImage(english.Original, new Rect(28, 442, 425, 170));
                context.DrawImage(english.Translated, new Rect(475, 442, 425, 170));
                Label("贴图右键 · 目标语言", 16, 940, 112, normal);
                context.DrawImage(menuBitmap, new Rect(940, 154, 220, 342));
                Label("选择语言本身不发送请求", 13, 940, 518, muted);
                Label("Ctrl+T 翻译 / 切换原图", 13, 940, 548, accent);
                Label("9 种目标语言：简体中文 · 英语 · 繁体中文 · 日语 · 韩语 · 法语 · 德语 · 俄语 · 西班牙语", 15, 28, 648, normal);
                Label("内置合成示意 / 模拟译文；未调用真实 OCR、百度接口或剪贴板", 12, 28, 684, muted);
            }
            var bitmap = new RenderTargetBitmap(1200, 720, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, "pin-multilingual-translation-demo.png"));
            encoder.Save(stream);
            Assert.Equal(0, fixture.Handler.Count);
            await Task.CompletedTask;
        }
        finally { Application.Current.Resources.MergedDictionaries.Remove(palette); }
    });

    private static FormattedText Text(string text, double size) => new(text, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
        FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brushes.Black, 1);

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "QingSnap-inline-translation", Guid.NewGuid().ToString("N"));
        public AppSettingsService Settings { get; }
        public ClipboardService Clipboard { get; } = new();
        public OcrService Ocr { get; }
        public TranslationCoordinator Coordinator { get; }
        public StickyImageWindow Window { get; }
        public BitmapSource Image { get; }
        public OcrRecognitionResult OcrResult { get; }
        public BaiduTranslationTests.FakeHandler Handler { get; }
        private readonly HttpClient _client;
        public List<string> Sent { get; } = [];
        public List<string> SentTargets { get; } = [];
        public bool DelayRequests { get; set; }
        public bool WrongMapping { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Fixture(bool longImage, int dpi)
        {
            Settings = new AppSettingsService(_directory);
            Settings.Save(Settings.Current with { TranslationEnabled = true, TranslationCloudConsent = true });
            Handler = new BaiduTranslationTests.FakeHandler(async (request, token) =>
            {
                var form = BaiduTranslationTests.ParseForm(await request.Content!.ReadAsStringAsync(token));
                var source = form["q"];
                Sent.Add(source);
                SentTargets.Add(form["to"]);
                Started.TrySetResult();
                if (DelayRequests) await Task.Delay(Timeout.Infinite, token);
                return BaiduTranslationSegmentTests.Response(source.Split('\n')
                    .Select(line => (WrongMapping ? "unknown region" : line,
                        form["to"] == "en" ? "This is a translated sentence." : "中文译文示例")).ToArray());
            });
            _client = new HttpClient(Handler);
            Coordinator = new TranslationCoordinator(Settings, Clipboard, new BaiduTranslationService(_client, minimumInterval: TimeSpan.Zero));
            Coordinator.Credentials.Save(new("2015063000000001", "synthetic-key-only"));
            Ocr = new OcrService(Settings);
            var height = longImage ? 1600 : 600;
            Image = PinTranslationLayoutTests.Image(800, height, 255, dpi);
            OcrResult = PinTranslationLayoutTests.Ocr(800, height,
                PinTranslationLayoutTests.Line(0, "Plan your next idea", new(40, 45, 620, 40)),
                PinTranslationLayoutTests.Line(1, "Second foreign paragraph", new(40, 160, 650, 34)),
                PinTranslationLayoutTests.Line(2, "这是原有的中文，不需要翻译。", new(40, 290, 650, 34)));
            Window = new StickyImageWindow(Image, "synthetic-inline", Clipboard, Ocr, Settings.Current,
                new System.Drawing.Rectangle(-900, 100, 800, 600), translation: Coordinator);
            Window.Resources.MergedDictionaries.Add(ThemeService.LoadDictionary("Dark"));
            Call(Window, "ApplyOcrResult", OcrResult);
        }
        public void Dispose()
        { Window.Close(); Coordinator.Dispose(); Ocr.Dispose(); Clipboard.Dispose(); _client.Dispose(); Directory.Delete(_directory, true); }
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Flags)!.GetValue(target)!;
    private static void Set(object target, string field, object? value) => target.GetType().GetField(field, Flags)!.SetValue(target, value);
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Flags)!.Invoke(target, args);

    private static void Run(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources["UiFont"] = new FontFamily("Segoe UI"); app.Resources["DataFont"] = new FontFamily("Consolas");
                }
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var task = action();
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
                Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Inline translation UI test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
