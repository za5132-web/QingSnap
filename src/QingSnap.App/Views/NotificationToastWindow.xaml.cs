using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QingSnap.App.Infrastructure;
using QingSnap.App.Services;
using FormsCursor = System.Windows.Forms.Cursor;
using FormsScreen = System.Windows.Forms.Screen;

namespace QingSnap.App.Views;

public partial class NotificationToastWindow : Window
{
    private readonly DispatcherTimer _dismissTimer = new();
    private bool _closing;

    public NotificationToastWindow(string message, ToastNotificationKind kind)
    {
        InitializeComponent();
        MessageText.Text = message;
        SuccessGlyph.Visibility = kind == ToastNotificationKind.Success
            ? Visibility.Visible
            : Visibility.Collapsed;
        WarningGlyph.Visibility = kind == ToastNotificationKind.Warning
            ? Visibility.Visible
            : Visibility.Collapsed;
        CountdownGlyph.Visibility = kind == ToastNotificationKind.Countdown
            ? Visibility.Visible
            : Visibility.Collapsed;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, _) => _dismissTimer.Stop();
        _dismissTimer.Tick += (_, _) => BeginDismiss();
    }

    public void ShowFor(TimeSpan visibleFor)
    {
        _dismissTimer.Interval = visibleFor;
        Show();
    }

    public void CloseImmediately()
    {
        _dismissTimer.Stop();
        if (IsLoaded)
        {
            Close();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        extendedStyle |= NativeMethods.WsExNoActivate |
                         NativeMethods.WsExToolWindow |
                         NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new nint(extendedStyle));

        var screen = FormsScreen.FromPoint(FormsCursor.Position);
        var scale = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Round(Width * scale.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(Height * scale.DpiScaleY));
        var x = screen.WorkingArea.Left + ((screen.WorkingArea.Width - width) / 2);
        var y = screen.WorkingArea.Top + ((screen.WorkingArea.Height - height) / 2);
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            x,
            y,
            width,
            height,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpNoOwnerZOrder |
            NativeMethods.SwpShowWindow);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            ToastTranslate.Y = 0;
            _dismissTimer.Start();
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
        ToastTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-7, 0, TimeSpan.FromMilliseconds(210)) { EasingFunction = easing });
        _dismissTimer.Start();
    }

    private void BeginDismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _dismissTimer.Stop();
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
