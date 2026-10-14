using System.Runtime.InteropServices;
using System.Text;

namespace AppRemote.Windows;

internal sealed record WindowEntry(nint Handle, string Title, uint ProcessId)
{
    public override string ToString() => $"{Title}  (PID {ProcessId})";
}

internal static class NativeWindows
{
    public static IReadOnlyList<WindowEntry> List(nint ownWindow)
    {
        var result = new List<WindowEntry>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == ownWindow || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length <= 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            if (string.IsNullOrWhiteSpace(title.ToString())) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId) return true;
            result.Add(new WindowEntry(hwnd, title.ToString(), pid));
            return true;
        }, 0);
        return result.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static bool IsSameWindow(WindowEntry entry)
    {
        if (!IsWindow(entry.Handle)) return false;
        GetWindowThreadProcessId(entry.Handle, out var pid);
        return pid == entry.ProcessId;
    }

    public static bool TryGetCaptureBounds(nint hwnd, out RECT rect)
    {
        const int extendedFrameBounds = 9;
        if (DwmGetWindowAttribute(hwnd, extendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0 &&
            rect.Right > rect.Left && rect.Bottom > rect.Top) return true;
        return GetWindowRect(hwnd, out rect);
    }

    public static bool TryGetInputBounds(nint hwnd, out RECT rect) => GetWindowRect(hwnd, out rect);

    public static bool Activate(nint hwnd)
    {
        if (GetForegroundWindow() == hwnd) return true;
        SetForegroundWindow(hwnd);
        return GetForegroundWindow() == hwnd;
    }

    public static bool IsForeground(nint hwnd) => GetForegroundWindow() == hwnd;

    public static bool MoveCursor(int x, int y) => SetCursorPos(x, y);

    public static bool IsValidWindow(nint hwnd) => IsWindow(hwnd);

    public static bool TryGetCursorPosition(out POINT point) => GetCursorPos(out point);

    public static bool TryGetClientTarget(nint root, int screenX, int screenY, out nint window, out POINT point)
    {
        window = root;
        point = new POINT { X = screenX, Y = screenY };
        if (!ScreenToClient(window, ref point) || !GetClientRect(window, out var client) ||
            point.X < 0 || point.Y < 0 || point.X >= client.Right || point.Y >= client.Bottom) return false;
        const uint skipInvisibleDisabledTransparent = 0x0001 | 0x0002 | 0x0004;
        for (var depth = 0; depth < 16; depth++)
        {
            var child = ChildWindowFromPointEx(window, point, skipInvisibleDisabledTransparent);
            if (child == 0 || child == window) break;
            window = child;
            point = new POINT { X = screenX, Y = screenY };
            if (!ScreenToClient(window, ref point)) return false;
        }
        return true;
    }

    public static bool TryScreenToClient(nint window, int screenX, int screenY, out POINT point)
    {
        point = new POINT { X = screenX, Y = screenY };
        return ScreenToClient(window, ref point);
    }

    public static bool PostWindowMessage(nint window, uint message, nuint wParam = 0, nint lParam = 0)
        => PostMessageW(window, message, wParam, lParam);

    public static nint PackPoint(int x, int y)
        => unchecked((nint)(uint)((ushort)x | ((uint)(ushort)y << 16)));

    public static void SendMouse(uint flags, uint data = 0)
    {
        var input = new INPUT { Type = 0, Data = new INPUTUNION { Mouse = new MOUSEINPUT { Flags = flags, MouseData = data } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void SendUnicode(char value)
    {
        var down = new INPUT { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { Scan = value, Flags = 0x0004 } } };
        var up = new INPUT { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { Scan = value, Flags = 0x0004 | 0x0002 } } };
        SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
    }

    public static void SendKey(ushort virtualKey)
    {
        var down = new INPUT { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey } } };
        var up = new INPUT { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey, Flags = 0x0002 } } };
        SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public nint ExtraInfo;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lparam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lparam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT rect, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint hwndParent, POINT point, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
