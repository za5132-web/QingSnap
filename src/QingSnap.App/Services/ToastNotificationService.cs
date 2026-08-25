using QingSnap.App.Views;

namespace QingSnap.App.Services;

public sealed class ToastNotificationService : IDisposable
{
    private readonly object _sync = new();
    private NotificationToastWindow? _currentToast;
    private bool _disposed;

    public void ShowSuccess(string message) =>
        Show(message, ToastNotificationKind.Success, TimeSpan.FromMilliseconds(1650));

    public void ShowWarning(string message) =>
        Show(message, ToastNotificationKind.Warning, TimeSpan.FromMilliseconds(2800));

    public void ShowCountdown(int seconds) =>
        Show(
            $"{Math.Max(1, seconds)} 秒后截图",
            ToastNotificationKind.Countdown,
            TimeSpan.FromMilliseconds(Math.Max(650, (seconds * 1000) - 220)));

    public void Dispose()
    {
        _disposed = true;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            lock (_sync)
            {
                _currentToast?.CloseImmediately();
                _currentToast = null;
            }
        });
    }

    private void Show(string message, ToastNotificationKind kind, TimeSpan visibleFor)
    {
        if (_disposed)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ShowCore(message, kind, visibleFor));
    }

    private void ShowCore(string message, ToastNotificationKind kind, TimeSpan visibleFor)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _currentToast?.CloseImmediately();
            var toast = new NotificationToastWindow(message, kind);
            _currentToast = toast;
            toast.Closed += (_, _) =>
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_currentToast, toast))
                    {
                        _currentToast = null;
                    }
                }
            };
            toast.ShowFor(visibleFor);
        }
    }
}

public enum ToastNotificationKind
{
    Success,
    Warning,
    Countdown
}
