using System.Windows;
using QingSnap.App.Models;
using QingSnap.App.Views;

namespace QingSnap.App.Services;

public sealed class TranslationCoordinator : IDisposable
{
    private readonly List<Window> _windows = [];
    private bool _disposed;

    public TranslationCoordinator(AppSettingsService settings, ClipboardService clipboard)
        : this(settings, clipboard, new BaiduTranslationService()) { }

    internal TranslationCoordinator(AppSettingsService settings, ClipboardService clipboard, BaiduTranslationService client)
    {
        Settings = settings;
        Clipboard = clipboard;
        Credentials = new TranslationCredentialStore(settings.DataDirectory);
        Client = client;
    }

    public AppSettingsService Settings { get; }
    public ClipboardService Clipboard { get; }
    public TranslationCredentialStore Credentials { get; }
    public BaiduTranslationService Client { get; }

    public void Open(string? selectedText = null)
    {
        if (_disposed) return;
        Show(new TranslationWindow(this, selectedText));
    }

    public void OpenSettings(Window? owner = null)
    {
        if (_disposed) return;
        if (_windows.OfType<TranslationSettingsWindow>().FirstOrDefault() is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = new TranslationSettingsWindow(this);
        if (owner?.IsVisible == true) window.Owner = owner;
        Show(window);
    }

    private void Show(Window window)
    {
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        window.Show();
        window.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var window in _windows.ToArray()) window.Close();
        _windows.Clear();
        Client.Dispose();
    }
}
