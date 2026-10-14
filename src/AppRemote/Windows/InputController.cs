using System.Text.Json;

namespace AppRemote.Windows;

internal sealed class InputController
{
    private const uint LeftDown = 0x0002, LeftUp = 0x0004;
    private const uint RightDown = 0x0008, RightUp = 0x0010, Wheel = 0x0800;
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmChar = 0x0102;
    private const uint WmMouseMove = 0x0200, WmLeftDown = 0x0201, WmLeftUp = 0x0202;
    private const uint WmRightDown = 0x0204, WmRightUp = 0x0205, WmMouseWheel = 0x020A;
    private readonly RemoteSession _session;
    private readonly object _gate = new();
    private bool _leftHeld;
    private bool _backgroundMode;
    private nint _backgroundHeldWindow;
    private nint _backgroundFocusWindow;
    private int _backgroundHeldX, _backgroundHeldY;
    private double _backgroundCursorX = 0.5, _backgroundCursorY = 0.5;

    public InputController(RemoteSession session)
    {
        _session = session;
        _session.Changed += Release;
    }

    public void SetBackgroundMode(bool enabled)
    {
        lock (_gate)
        {
            Release();
            _backgroundMode = enabled;
            _backgroundCursorX = _backgroundCursorY = 0.5;
        }
    }

    public void ResetBackgroundCursor()
    {
        lock (_gate) { _backgroundCursorX = 0.5; _backgroundCursorY = 0.5; }
    }

    public bool Handle(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement)) return false;
        var type = typeElement.GetString();
        lock (_gate)
        {
            var target = _session.Selected;
            if (target is null || !NativeWindows.IsSameWindow(target)) return false;
            if (_backgroundMode) return HandleBackground(root, type, target);
            if (!NativeWindows.IsForeground(target.Handle) && !NativeWindows.Activate(target.Handle))
            {
                Release();
                return false;
            }

            switch (type)
            {
                case "down":
                case "move":
                case "up":
                case "right":
                    if (!TryPoint(root, target, out var x, out var y)) return false;
                    NativeWindows.MoveCursor(x, y);
                    if (type == "down" && !_leftHeld) { NativeWindows.SendMouse(LeftDown); _leftHeld = true; }
                    if (type == "up" && _leftHeld) { NativeWindows.SendMouse(LeftUp); _leftHeld = false; }
                    if (type == "right") { NativeWindows.SendMouse(RightDown); NativeWindows.SendMouse(RightUp); }
                    return true;
                case "cursorMove":
                    if (!root.TryGetProperty("dx", out var dxValue) || !root.TryGetProperty("dy", out var dyValue) ||
                        !dxValue.TryGetDouble(out var dx) || !dyValue.TryGetDouble(out var dy) ||
                        !double.IsFinite(dx) || !double.IsFinite(dy) ||
                        dx is < -1 or > 1 || dy is < -1 or > 1 ||
                        !TryCursorPoint(target, out var cursorX, out var cursorY, out var bounds)) return false;
                    NativeWindows.MoveCursor(
                        Math.Clamp(cursorX + (int)Math.Round(dx * (bounds.Right - bounds.Left)), bounds.Left, bounds.Right - 1),
                        Math.Clamp(cursorY + (int)Math.Round(dy * (bounds.Bottom - bounds.Top)), bounds.Top, bounds.Bottom - 1));
                    return true;
                case "cursorClick":
                case "cursorRight":
                    if (!TryCursorPoint(target, out var clickX, out var clickY, out _)) return false;
                    NativeWindows.MoveCursor(clickX, clickY);
                    if (type == "cursorClick")
                    {
                        NativeWindows.SendMouse(LeftDown);
                        NativeWindows.SendMouse(LeftUp);
                    }
                    else
                    {
                        NativeWindows.SendMouse(RightDown);
                        NativeWindows.SendMouse(RightUp);
                    }
                    return true;
                case "wheel":
                    if (!root.TryGetProperty("delta", out var deltaValue) || !deltaValue.TryGetInt32(out var delta)) return false;
                    NativeWindows.SendMouse(Wheel, unchecked((uint)Math.Clamp(delta, -600, 600)));
                    return true;
                case "text":
                    if (!root.TryGetProperty("value", out var value)) return false;
                    var text = value.GetString() ?? "";
                    if (text.Length > 256) return false;
                    foreach (var character in text) NativeWindows.SendUnicode(character);
                    return true;
                case "key":
                    if (!root.TryGetProperty("value", out var key)) return false;
                    var virtualKey = GetVirtualKey(key.GetString());
                    if (virtualKey == 0) return false;
                    NativeWindows.SendKey(virtualKey);
                    return true;
                default: return false;
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_leftHeld) { NativeWindows.SendMouse(LeftUp); _leftHeld = false; }
            if (_backgroundHeldWindow != 0)
            {
                NativeWindows.PostWindowMessage(_backgroundHeldWindow, WmLeftUp, 0,
                    NativeWindows.PackPoint(_backgroundHeldX, _backgroundHeldY));
                _backgroundHeldWindow = 0;
            }
            _backgroundFocusWindow = 0;
        }
    }

    private bool HandleBackground(JsonElement root, string? type, WindowEntry target)
    {
        switch (type)
        {
            case "down":
            case "move":
            case "up":
            case "right":
                return TryNormalizedPoint(root, out var nx, out var ny) &&
                    TryBackgroundPoint(target, nx, ny, out var x, out var y) &&
                    PostBackgroundPointer(target, type, x, y);
            case "cursorMove":
                if (!root.TryGetProperty("dx", out var dxValue) || !root.TryGetProperty("dy", out var dyValue) ||
                    !dxValue.TryGetDouble(out var dx) || !dyValue.TryGetDouble(out var dy) ||
                    !double.IsFinite(dx) || !double.IsFinite(dy) || dx is < -1 or > 1 || dy is < -1 or > 1)
                    return false;
                _backgroundCursorX = Math.Clamp(_backgroundCursorX + dx, 0, 1);
                _backgroundCursorY = Math.Clamp(_backgroundCursorY + dy, 0, 1);
                return TryBackgroundCursorPoint(target, out var cursorX, out var cursorY) &&
                    PostBackgroundPointer(target, "move", cursorX, cursorY);
            case "cursorClick":
            case "cursorRight":
                if (!TryBackgroundCursorPoint(target, out var clickX, out var clickY)) return false;
                return PostBackgroundPointer(target, type == "cursorClick" ? "click" : "right", clickX, clickY);
            case "wheel":
                if (!root.TryGetProperty("delta", out var deltaValue) || !deltaValue.TryGetInt32(out var delta) ||
                    !TryBackgroundCursorPoint(target, out var wheelX, out var wheelY) ||
                    !NativeWindows.TryGetClientTarget(target.Handle, wheelX, wheelY, out var wheelWindow, out _)) return false;
                var wheelData = unchecked((nuint)((uint)(ushort)Math.Clamp(delta, -600, 600) << 16));
                return NativeWindows.PostWindowMessage(wheelWindow, WmMouseWheel, wheelData,
                    NativeWindows.PackPoint(wheelX, wheelY));
            case "text":
                if (!root.TryGetProperty("value", out var value)) return false;
                var text = value.GetString() ?? "";
                if (text.Length > 256) return false;
                var textWindow = _backgroundFocusWindow != 0 && NativeWindows.IsValidWindow(_backgroundFocusWindow)
                    ? _backgroundFocusWindow : target.Handle;
                foreach (var character in text)
                    if (!NativeWindows.PostWindowMessage(textWindow, WmChar, character)) return false;
                return true;
            case "key":
                if (!root.TryGetProperty("value", out var key)) return false;
                var virtualKey = GetVirtualKey(key.GetString());
                if (virtualKey == 0) return false;
                var keyWindow = _backgroundFocusWindow != 0 && NativeWindows.IsValidWindow(_backgroundFocusWindow)
                    ? _backgroundFocusWindow : target.Handle;
                return NativeWindows.PostWindowMessage(keyWindow, WmKeyDown, virtualKey, 1) &&
                    NativeWindows.PostWindowMessage(keyWindow, WmKeyUp, virtualKey, unchecked((nint)0xC0000001));
            default: return false;
        }
    }

    private bool PostBackgroundPointer(WindowEntry target, string type, int screenX, int screenY)
    {
        nint window;
        NativeWindows.POINT point;
        if (_backgroundHeldWindow != 0 && type is ("down" or "move" or "up"))
        {
            window = _backgroundHeldWindow;
            if (!NativeWindows.TryScreenToClient(window, screenX, screenY, out point)) return false;
        }
        else if (!NativeWindows.TryGetClientTarget(target.Handle, screenX, screenY, out window, out point))
            return false;

        var packed = NativeWindows.PackPoint(point.X, point.Y);
        if (type == "up")
        {
            if (_backgroundHeldWindow == 0) return true;
            _backgroundHeldWindow = 0;
            return NativeWindows.PostWindowMessage(window, WmLeftUp, 0, packed);
        }
        var held = _backgroundHeldWindow != 0;
        if (!NativeWindows.PostWindowMessage(window, WmMouseMove, held ? 1u : 0u, packed)) return false;
        if (held)
        {
            _backgroundHeldX = point.X;
            _backgroundHeldY = point.Y;
        }
        if (type == "move") return true;
        if (type == "down")
        {
            if (held) return true;
            if (!NativeWindows.PostWindowMessage(window, WmLeftDown, 1, packed)) return false;
            _backgroundHeldWindow = window;
            _backgroundHeldX = point.X;
            _backgroundHeldY = point.Y;
            _backgroundFocusWindow = window;
            return true;
        }
        if (type == "click")
        {
            _backgroundFocusWindow = window;
            return NativeWindows.PostWindowMessage(window, WmLeftDown, 1, packed) &&
                   NativeWindows.PostWindowMessage(window, WmLeftUp, 0, packed);
        }
        if (type == "right")
            return NativeWindows.PostWindowMessage(window, WmRightDown, 2, packed) &&
                   NativeWindows.PostWindowMessage(window, WmRightUp, 0, packed);
        return false;
    }

    private bool TryBackgroundCursorPoint(WindowEntry target, out int x, out int y)
        => TryBackgroundPoint(target, _backgroundCursorX, _backgroundCursorY, out x, out y);

    private static bool TryBackgroundPoint(WindowEntry target, double nx, double ny, out int x, out int y)
    {
        x = y = 0;
        if (!NativeWindows.TryGetInputBounds(target.Handle, out var rect) ||
            rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;
        x = rect.Left + (int)Math.Round(nx * (rect.Right - rect.Left - 1));
        y = rect.Top + (int)Math.Round(ny * (rect.Bottom - rect.Top - 1));
        return true;
    }

    private static ushort GetVirtualKey(string? key) => key switch
    {
        "Backspace" => 0x08, "Enter" => 0x0D, "Tab" => 0x09, "Escape" => 0x1B,
        "ArrowLeft" => 0x25, "ArrowUp" => 0x26, "ArrowRight" => 0x27, "ArrowDown" => 0x28,
        _ => 0
    };

    private static bool TryPoint(JsonElement root, WindowEntry target, out int x, out int y)
    {
        x = y = 0;
        if (!TryNormalizedPoint(root, out var nx, out var ny) ||
            !NativeWindows.TryGetCaptureBounds(target.Handle, out var rect)) return false;
        x = rect.Left + (int)Math.Round(nx * (rect.Right - rect.Left - 1));
        y = rect.Top + (int)Math.Round(ny * (rect.Bottom - rect.Top - 1));
        return true;
    }

    private static bool TryNormalizedPoint(JsonElement root, out double nx, out double ny)
    {
        nx = ny = 0;
        return root.TryGetProperty("x", out var xValue) && root.TryGetProperty("y", out var yValue) &&
            xValue.TryGetDouble(out nx) && yValue.TryGetDouble(out ny) &&
            nx is >= 0 and <= 1 && ny is >= 0 and <= 1 &&
            double.IsFinite(nx) && double.IsFinite(ny);
    }

    private static bool TryCursorPoint(WindowEntry target, out int x, out int y, out NativeWindows.RECT rect)
    {
        x = y = 0;
        rect = default;
        if (!NativeWindows.TryGetCaptureBounds(target.Handle, out rect) ||
            rect.Right <= rect.Left || rect.Bottom <= rect.Top ||
            !NativeWindows.TryGetCursorPosition(out var cursor)) return false;
        x = cursor.X >= rect.Left && cursor.X < rect.Right ? cursor.X : (rect.Left + rect.Right) / 2;
        y = cursor.Y >= rect.Top && cursor.Y < rect.Bottom ? cursor.Y : (rect.Top + rect.Bottom) / 2;
        return true;
    }
}
