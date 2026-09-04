using System.Runtime.InteropServices;

namespace QingSnap.App.Infrastructure;

internal static class NativeMethods
{
    internal const int WmHotkey = 0x0312;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpNoOwnerZOrder = 0x0200;
    internal const uint SwpShowWindow = 0x0040;
    internal static readonly nint HwndTopmost = new(-1);
    internal const uint InputMouse = 0;
    internal const uint MouseEventWheel = 0x0800;
    internal const uint GetAncestorRoot = 2;
    internal const int GwlExStyle = -20;
    internal const long WsExTransparent = 0x00000020L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const int WmNcHitTest = 0x0084;
    internal const int DwmWindowAttributeCloak = 13;
    internal const uint IaceDefault = 0x0010;
    private const uint ChildWindowSkipInvisible = 0x0001;
    private const uint ChildWindowSkipDisabled = 0x0002;
    private const uint ChildWindowSkipTransparent = 0x0004;
    internal static readonly nint HtTransparent = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hWnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmFlush();

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmAssociateContextEx(nint hWnd, nint hImc, uint flags);

    internal static bool SetWindowCloaked(nint window, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(
            window,
            DwmWindowAttributeCloak,
            ref value,
            sizeof(int)) >= 0;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint hWnd, int index, nint newLong);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProcedure callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint ChildWindowFromPointEx(nint parent, NativePoint point, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint hWnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern nint SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    internal static bool SendMouseWheel(int delta)
    {
        const int standardWheelDelta = 120;
        var direction = Math.Sign(delta);
        var inputCount = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (double)standardWheelDelta));
        var inputs = new Input[inputCount];
        for (var index = 0; index < inputs.Length; index++)
        {
            inputs[index] = new Input
            {
                Type = InputMouse,
                Data = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        MouseData = unchecked((uint)(direction * standardWheelDelta)),
                        Flags = MouseEventWheel
                    }
                }
            };
        }

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    internal static bool ActivateWindow(nint window)
    {
        if (window == nint.Zero)
        {
            return false;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var targetThread = GetWindowThreadProcessId(window, out _);
        var attachedToForeground = currentThread != foregroundThread &&
                                   AttachThreadInput(currentThread, foregroundThread, true);
        var attachedToTarget = currentThread != targetThread && targetThread != foregroundThread &&
                               AttachThreadInput(currentThread, targetThread, true);

        try
        {
            BringWindowToTop(window);
            var activated = SetForegroundWindow(window);
            SetFocus(window);
            return activated;
        }
        finally
        {
            if (attachedToTarget)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedToForeground)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    internal static nint FindWindowAtPointExcludingProcess(NativePoint screenPoint, uint excludedProcessId)
    {
        nint root = nint.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !GetWindowRect(window, out var bounds) ||
                screenPoint.X < bounds.Left || screenPoint.X >= bounds.Right ||
                screenPoint.Y < bounds.Top || screenPoint.Y >= bounds.Bottom)
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var processId);
            if (processId == excludedProcessId)
            {
                return true;
            }

            root = window;
            return false;
        }, nint.Zero);

        if (root == nint.Zero)
        {
            return nint.Zero;
        }

        var current = root;
        for (var depth = 0; depth < 8; depth++)
        {
            var clientPoint = screenPoint;
            if (!ScreenToClient(current, ref clientPoint))
            {
                break;
            }

            var child = ChildWindowFromPointEx(
                current,
                clientPoint,
                ChildWindowSkipInvisible | ChildWindowSkipDisabled | ChildWindowSkipTransparent);
            if (child == nint.Zero || child == current)
            {
                break;
            }

            current = child;
        }

        return current;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal int X;
        internal int Y;

        internal NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    private delegate bool EnumWindowsProcedure(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal int Width => Right - Left;

        internal int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }
}
