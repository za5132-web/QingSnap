using System.Windows;
using System.IO;
using System.Windows.Threading;

namespace QingSnap.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\QingSnap.7F29E134-6D9A-49EE-8CF5-CF40EEC3C9E1";
    private MainWindow? _hostWindow;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        _hostWindow = new MainWindow();
        MainWindow = _hostWindow;
        _hostWindow.Show();

        if (e.Args.Any(argument => argument.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.OpenSettingsWindow);
        }
        else if (e.Args.Any(argument => argument.Equals("--history", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.OpenHistoryWindow);
        }
        else if (e.Args.Any(argument => argument.Equals("--repeat", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.RepeatLastCapture);
        }
        else if (e.Args.Any(argument => argument.Equals("--pin-latest", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.PinLatestCapture);
        }
        else if (e.Args.Any(argument => argument.Equals("--ocr-latest", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.RecognizeLatestCapture);
        }
        else if (e.Args.Any(argument => argument.Equals("--long-capture-manual", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.StartManualLongCapture);
        }
        else if (e.Args.Any(argument => argument.Equals("--long-capture", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.StartLongCapture);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e) =>
        WriteCrashLog("Dispatcher", e.Exception);

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog("AppDomain", exception);
        }
    }

    private static void WriteCrashLog(string source, Exception exception)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "QingSnap-crash.log"),
                $"[{DateTime.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
