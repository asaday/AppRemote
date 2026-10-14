using System.Text.Json;

namespace AppRemote.Windows;

internal sealed class InputController
{
    private const uint LeftDown = 0x0002, LeftUp = 0x0004;
    private const uint RightDown = 0x0008, RightUp = 0x0010, Wheel = 0x0800;
    private readonly RemoteSession _session;
    private readonly object _gate = new();
    private bool _leftHeld;

    public InputController(RemoteSession session)
    {
        _session = session;
        _session.Changed += Release;
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
                    var virtualKey = key.GetString() switch
                    {
                        "Backspace" => (ushort)0x08,
                        "Enter" => (ushort)0x0D,
                        "Tab" => (ushort)0x09,
                        "Escape" => (ushort)0x1B,
                        "ArrowLeft" => (ushort)0x25,
                        "ArrowUp" => (ushort)0x26,
                        "ArrowRight" => (ushort)0x27,
                        "ArrowDown" => (ushort)0x28,
                        _ => (ushort)0
                    };
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
        }
    }

    private static bool TryPoint(JsonElement root, WindowEntry target, out int x, out int y)
    {
        x = y = 0;
        if (!root.TryGetProperty("x", out var xValue) || !root.TryGetProperty("y", out var yValue) ||
            !xValue.TryGetDouble(out var nx) || !yValue.TryGetDouble(out var ny) ||
            nx is < 0 or > 1 || ny is < 0 or > 1 || !double.IsFinite(nx) || !double.IsFinite(ny) ||
            !NativeWindows.TryGetCaptureBounds(target.Handle, out var rect)) return false;
        x = rect.Left + (int)Math.Round(nx * (rect.Right - rect.Left - 1));
        y = rect.Top + (int)Math.Round(ny * (rect.Bottom - rect.Top - 1));
        return true;
    }
}
