using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int RegionCaptureHotkeyId = 0x5101;
    private const int RepeatCaptureHotkeyId = 0x5102;
    private const int PinLatestHotkeyId = 0x5103;
    private const uint ModifierNoRepeat = 0x4000;

    private readonly nint _windowHandle;
    private readonly HwndSource _source;
    private readonly AppSettings _settings;
    private bool _registered;

    public GlobalHotkeyService(Window hostWindow, AppSettings settings)
    {
        _settings = settings;
        _windowHandle = new WindowInteropHelper(hostWindow).Handle;
        _source = HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException("无法创建快捷键消息窗口。");
        _source.AddHook(WndProc);
    }

    public event EventHandler? RegionCaptureRequested;
    public event EventHandler? RepeatCaptureRequested;
    public event EventHandler? PinLatestRequested;

    public void Register()
    {
        if (_registered)
        {
            return;
        }

        if (!TryParseGesture(_settings.CaptureHotkey, out var capture) ||
            !NativeMethods.RegisterHotKey(
                _windowHandle,
                RegionCaptureHotkeyId,
                capture.Modifiers | ModifierNoRepeat,
                capture.VirtualKey))
        {
            throw CreateRegistrationException(_settings.CaptureHotkey);
        }

        if (!TryParseGesture(_settings.RepeatHotkey, out var repeat) ||
            !NativeMethods.RegisterHotKey(
                _windowHandle,
                RepeatCaptureHotkeyId,
                repeat.Modifiers | ModifierNoRepeat,
                repeat.VirtualKey))
        {
            NativeMethods.UnregisterHotKey(_windowHandle, RegionCaptureHotkeyId);
            throw CreateRegistrationException(_settings.RepeatHotkey);
        }

        if (!TryParseGesture(_settings.PinHotkey, out var pin) ||
            !NativeMethods.RegisterHotKey(
                _windowHandle,
                PinLatestHotkeyId,
                pin.Modifiers | ModifierNoRepeat,
                pin.VirtualKey))
        {
            NativeMethods.UnregisterHotKey(_windowHandle, RegionCaptureHotkeyId);
            NativeMethods.UnregisterHotKey(_windowHandle, RepeatCaptureHotkeyId);
            throw CreateRegistrationException(_settings.PinHotkey);
        }

        _registered = true;
    }

    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, RegionCaptureHotkeyId);
            NativeMethods.UnregisterHotKey(_windowHandle, RepeatCaptureHotkeyId);
            NativeMethods.UnregisterHotKey(_windowHandle, PinLatestHotkeyId);
            _registered = false;
        }

        _source.RemoveHook(WndProc);
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotkey)
        {
            return nint.Zero;
        }

        switch (wParam.ToInt32())
        {
            case RegionCaptureHotkeyId:
                RegionCaptureRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
            case RepeatCaptureHotkeyId:
                RepeatCaptureRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
            case PinLatestHotkeyId:
                PinLatestRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
        }

        return nint.Zero;
    }

    private static InvalidOperationException CreateRegistrationException(string shortcut)
    {
        var errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        var detail = new Win32Exception(errorCode).Message;
        return new InvalidOperationException($"无法注册快捷键 {shortcut}，可能已被其他程序占用。{detail}");
    }

    public static bool IsValidGesture(string value) => TryParseGesture(value, out _);

    private static bool TryParseGesture(string? value, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        uint modifiers = 0;
        uint virtualKey = 0;
        foreach (var part in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0002;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0004;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0001;
            }
            else if (part.Length is 2 or 3 &&
                     part[0] is 'F' or 'f' &&
                     int.TryParse(part[1..], out var functionKey) &&
                     functionKey is >= 1 and <= 12)
            {
                virtualKey = (uint)(0x6F + functionKey);
            }
            else
            {
                return false;
            }
        }

        if (virtualKey == 0)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey);
        return true;
    }

    private readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey);
}
