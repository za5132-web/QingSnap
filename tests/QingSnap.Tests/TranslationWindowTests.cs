using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class TranslationWindowTests
{
    [Theory]
    [InlineData("Dark", 1.0)] [InlineData("Dark", 1.5)] [InlineData("Dark", 2.0)]
    [InlineData("Light", 1.0)] [InlineData("Light", 1.5)] [InlineData("Light", 2.0)]
    public void NewWindowsUseCurrentThemeAndFitAtMinimumSize(string theme, double scale) => Run(async () =>
    {
        using var fixture = new Fixture();
        var translation = new TranslationWindow(fixture.Coordinator);
        var settings = new TranslationSettingsWindow(fixture.Coordinator);
        try
        {
            foreach (var window in new Window[] { translation, settings })
            {
                window.Resources.MergedDictionaries.Add(ThemeService.LoadDictionary(theme));
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(window.MinWidth, window.Height));
                root.Arrange(new Rect(0, 0, window.MinWidth, window.Height));
                root.UpdateLayout();
                var field = window.FindName(window is TranslationWindow ? "SourceBox" : "AppIdBox") as TextBox;
                Assert.NotNull(field);
                Assert.Equal(((SolidColorBrush)window.FindResource("ThemeFieldBrush")).Color, ((SolidColorBrush)field.Background).Color);
                var combo = (ComboBox)window.FindName("TargetCombo");
                combo.ApplyTemplate();
                Assert.NotNull(combo.Template.FindName("PART_Popup", combo));
                var button = (Button)window.FindName(window is TranslationWindow ? "TranslateButton" : "SaveButton");
                Assert.True(button.ActualWidth >= 80);
                Assert.True(button.ActualHeight >= 30);
                var bitmap = new RenderTargetBitmap((int)(window.MinWidth * scale), (int)(window.Height * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                Assert.Equal((int)(window.MinWidth * scale), bitmap.PixelWidth);
            }
            Assert.Equal(0, fixture.Handler.Count);
            await Task.CompletedTask;
        }
        finally { translation.Close(); settings.Close(); }
    });

    [Fact]
    public void OpeningAndSavingDisabledSettingsNeverAccessesNetwork() => Run(async () =>
    {
        using var fixture = new Fixture();
        var window = new TranslationSettingsWindow(fixture.Coordinator);
        try
        {
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(fixture.Settings.Current.TranslationEnabled);
            Assert.Equal(0, fixture.Handler.Count);
            await Task.CompletedTask;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TranslationUsesEditedTextAndTargetAndDoesNotWriteBackToOcrHistory() => Run(async () =>
    {
        using var fixture = new Fixture();
        fixture.Enable();
        var window = new TranslationWindow(fixture.Coordinator, "old selected text");
        try
        {
            ((TextBox)window.FindName("SourceBox")).Text = "edited source & 中文😀";
            ((ComboBox)window.FindName("TargetCombo")).SelectedValue = "en";
            await window.TranslateAsync();
            Assert.Equal("edited source & 中文😀", fixture.LastForm!["q"]);
            Assert.Equal("en", fixture.LastForm["to"]);
            Assert.Equal("synthetic translated result", ((TextBox)window.FindName("ResultBox")).Text);
            Assert.True(((Button)window.FindName("CopyButton")).IsEnabled);
            ((TextBox)window.FindName("SourceBox")).Text = "changed after result";
            Assert.Equal(string.Empty, ((TextBox)window.FindName("ResultBox")).Text);
            Assert.False(((Button)window.FindName("CopyButton")).IsEnabled);
            Assert.Equal(1, fixture.Handler.Count);
            Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, "History")));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ChangingInputOrClosingCancelsPendingTranslationAndCannotShowStaleResult() => Run(async () =>
    {
        using var fixture = new Fixture();
        fixture.Enable();
        fixture.DelayRequests = true;
        var window = new TranslationWindow(fixture.Coordinator, "first request");
        var source = (TextBox)window.FindName("SourceBox");
        var result = (TextBox)window.FindName("ResultBox");
        var first = window.TranslateAsync();
        await fixture.RequestStarted.Task;
        source.Text = "new input";
        await first;
        Assert.Equal(string.Empty, result.Text);
        Assert.True(((Button)window.FindName("TranslateButton")).IsEnabled);
        var second = window.TranslateAsync();
        window.Close();
        await second;
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(string.Empty, source.Text);
    });

    [Fact]
    public void OcrAndPinTranslationEntriesAreWiredWithoutChangingExistingShortcuts() => Run(async () =>
    {
        using var fixture = new Fixture();
        using var ocr = new OcrService(fixture.Settings);
        var image = BitmapSource.Create(10, 10, 96, 96, PixelFormats.Bgra32, null, new byte[400], 40);
        image.Freeze();
        var ocrWindow = new OcrResultWindow("synthetic.png", image, ocr, fixture.Clipboard,
            translation: fixture.Coordinator);
        var pin = new StickyImageWindow(image, "synthetic.png", fixture.Clipboard, ocr,
            fixture.Settings.Current, translation: fixture.Coordinator);
        try
        {
            Assert.Equal(Visibility.Visible, ((Button)ocrWindow.FindName("TranslateButton")).Visibility);
            Assert.NotNull(pin.FindName("TranslateSelectedOcrMenuItem"));
            Assert.Equal("F1", fixture.Settings.Current.CaptureHotkey);
            Assert.Equal("F3", fixture.Settings.Current.PinHotkey);
            Assert.Equal("Shift+F1", fixture.Settings.Current.RepeatHotkey);
            Assert.Equal(0, fixture.Handler.Count);
            await Task.CompletedTask;
        }
        finally { ocrWindow.Close(); pin.Close(); }
    });

    [Fact]
    public void RenderSyntheticPreviewWhenExplicitlyRequested() => Run(async () =>
    {
        var directory = Environment.GetEnvironmentVariable("QINGSNAP_TRANSLATION_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var fixture = new Fixture();
        foreach (var theme in new[] { "Dark", "Light" })
        {
            var window = new TranslationWindow(fixture.Coordinator, "Capture a thought. Keep it on your desktop.\n\nQingSnap lets you select text directly from a pinned image.");
            var settings = new TranslationSettingsWindow(fixture.Coordinator);
            try
            {
                ((TextBox)window.FindName("ResultBox")).Text = "截住灵感，让它留在桌面。\n\n轻截支持直接在贴图中选择文字。\n\n（此预览为内置模拟文字，未调用翻译接口。）";
                foreach (var item in new Window[] { window, settings })
                {
                    item.Resources.MergedDictionaries.Add(ThemeService.LoadDictionary(theme));
                    var root = (FrameworkElement)item.Content;
                    root.Measure(new Size(item.Width, item.Height));
                    root.Arrange(new Rect(0, 0, item.Width, item.Height));
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)item.Width, (int)item.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"{(item is TranslationWindow ? "translator" : "settings")}-{theme}.png"));
                    encoder.Save(stream);
                }
                await Task.CompletedTask;
            }
            finally { window.Close(); settings.Close(); }
        }
    });

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "QingSnap-translation-ui", Guid.NewGuid().ToString("N"));
        public AppSettingsService Settings { get; }
        public ClipboardService Clipboard { get; } = new();
        public BaiduTranslationTests.FakeHandler Handler { get; }
        public TranslationCoordinator Coordinator { get; }
        public Dictionary<string, string>? LastForm { get; private set; }
        public bool DelayRequests { get; set; }
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HttpClient _client;
        public Fixture()
        {
            Settings = new AppSettingsService(DirectoryPath);
            Handler = new BaiduTranslationTests.FakeHandler(async (request, token) =>
            {
                LastForm = BaiduTranslationTests.ParseForm(await request.Content!.ReadAsStringAsync(token));
                RequestStarted.TrySetResult();
                if (DelayRequests) await Task.Delay(Timeout.Infinite, token);
                return BaiduTranslationTests.Success("synthetic translated result");
            });
            _client = new HttpClient(Handler);
            Coordinator = new TranslationCoordinator(Settings, Clipboard, new BaiduTranslationService(_client, minimumInterval: TimeSpan.Zero));
        }
        public void Enable()
        {
            Coordinator.Credentials.Save(new("2015063000000001", "synthetic-key-not-real"));
            Settings.Save(Settings.Current with { TranslationEnabled = true, TranslationCloudConsent = true });
        }
        public void Dispose()
        {
            Coordinator.Dispose();
            _client.Dispose();
            Clipboard.Dispose();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

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
                    app.Resources["UiFont"] = new FontFamily("Segoe UI");
                    app.Resources["DataFont"] = new FontFamily("Consolas");
                }
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var task = action();
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
                Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Translation UI test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
