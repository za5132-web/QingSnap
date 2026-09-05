using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyIdBase = 0x5100;
    private const uint ModifierNoRepeat = 0x4000;

    private readonly nint _windowHandle;
    private readonly HwndSource _source;
    private readonly IReadOnlyList<HotkeyBinding> _bindings;
    private readonly Dictionary<int, HotkeyBinding> _registeredBindings = [];
    private bool _isSuspended;
    private bool _disposed;

    public GlobalHotkeyService(Window hostWindow, IReadOnlyList<HotkeyBinding> bindings)
    {
        _bindings = bindings;
        _windowHandle = new WindowInteropHelper(hostWindow).Handle;
        _source = HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException("无法创建快捷键消息窗口。");
        _source.AddHook(WndProc);
    }

    public event EventHandler<HotkeyActionEventArgs>? ActionRequested;

    public IReadOnlyList<HotkeyRegistrationFailure> Register()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var failures = new List<HotkeyRegistrationFailure>();
        var gestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in _bindings.Where(binding => binding.IsEnabled))
        {
            if (!HotkeyGestureParser.TryNormalize(binding.Gesture, out var normalized) ||
                !HotkeyGestureParser.TryParse(normalized, out var gesture))
            {
                failures.Add(new HotkeyRegistrationFailure(
                    binding.Action,
                    binding.Gesture,
                    "快捷键格式无效"));
                continue;
            }

            if (!gestures.Add(normalized))
            {
                failures.Add(new HotkeyRegistrationFailure(
                    binding.Action,
                    normalized,
                    "与另一个已启用的 QingSnap 快捷键重复"));
                continue;
            }

            var hotkeyId = GetHotkeyId(binding.Action);
            if (!NativeMethods.RegisterHotKey(
                    _windowHandle,
                    hotkeyId,
                    gesture.Modifiers | ModifierNoRepeat,
                    gesture.VirtualKey))
            {
                var errorCode = Marshal.GetLastWin32Error();
                var detail = new Win32Exception(errorCode).Message;
                failures.Add(new HotkeyRegistrationFailure(
                    binding.Action,
                    normalized,
                    $"可能已被其他软件占用（{detail}）"));
                continue;
            }

            _registeredBindings[hotkeyId] = binding with { Gesture = normalized };
        }

        return failures;
    }

    public void SetSuspended(bool suspended) => _isSuspended = suspended;

    public bool IsRegistered(HotkeyAction action) =>
        _registeredBindings.Values.Any(binding => binding.Action == action);

    public static bool IsValidGesture(string? value) => HotkeyGestureParser.IsValid(value);

    public static bool TryNormalizeGesture(string? value, out string normalized) =>
        HotkeyGestureParser.TryNormalize(value, out normalized);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var hotkeyId in _registeredBindings.Keys)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, hotkeyId);
        }

        _registeredBindings.Clear();
        _source.RemoveHook(WndProc);
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotkey ||
            !_registeredBindings.TryGetValue(wParam.ToInt32(), out var binding))
        {
            return nint.Zero;
        }

        handled = true;
        if (_isSuspended && binding.Action != HotkeyAction.ToggleGlobalHotkeys)
        {
            return nint.Zero;
        }

        ActionRequested?.Invoke(this, new HotkeyActionEventArgs(binding.Action));
        return nint.Zero;
    }

    private static int GetHotkeyId(HotkeyAction action) => HotkeyIdBase + (int)action + 1;
}

public sealed class HotkeyActionEventArgs(HotkeyAction action) : EventArgs
{
    public HotkeyAction Action { get; } = action;
}

public sealed record HotkeyRegistrationFailure(
    HotkeyAction Action,
    string Gesture,
    string Reason)
{
    public string DisplayText => $"{HotkeyCatalog.GetDisplayName(Action)}（{Gesture}）：{Reason}";
}
