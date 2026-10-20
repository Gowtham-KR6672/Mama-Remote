using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteConnect.Protocol;
using static RemoteConnect.Platform.Windows.Win32;

namespace RemoteConnect.Platform.Windows;

/// <summary>
/// Input injection with the documented SendInput API. Subject to UIPI: windows running elevated (and the secure
/// desktop) do not accept input from a non-elevated process — this is intentional and not bypassed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SendInputInjector : IInputInjector
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    private readonly HashSet<ushort> _pressedKeys = new();
    private readonly HashSet<MouseButton> _pressedButtons = new();
    private readonly object _sync = new();

    public void MovePointer(DisplayInfo display, double nx, double ny)
    {
        var (dx, dy) = ToVirtualDesk(display, nx, ny);
        Send(Mouse(dx, dy, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0));
    }

    public void PointerButton(DisplayInfo display, double nx, double ny, MouseButton button, bool down)
    {
        uint flag = (button, down) switch
        {
            (MouseButton.Left, true) => MOUSEEVENTF_LEFTDOWN,
            (MouseButton.Left, false) => MOUSEEVENTF_LEFTUP,
            (MouseButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (MouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            (MouseButton.Middle, true) => MOUSEEVENTF_MIDDLEDOWN,
            (MouseButton.Middle, false) => MOUSEEVENTF_MIDDLEUP,
            _ => 0,
        };
        if (flag == 0)
        {
            return;
        }

        var (dx, dy) = ToVirtualDesk(display, nx, ny);
        lock (_sync)
        {
            if (down) _pressedButtons.Add(button); else _pressedButtons.Remove(button);
        }

        Send(Mouse(dx, dy, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | flag, 0));
    }

    public void Scroll(DisplayInfo display, double nx, double ny, int deltaX, int deltaY)
    {
        var (dx, dy) = ToVirtualDesk(display, nx, ny);
        var inputs = new List<INPUT> { Mouse(dx, dy, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0) };
        if (deltaY != 0) inputs.Add(Mouse(0, 0, MOUSEEVENTF_WHEEL, deltaY));
        if (deltaX != 0) inputs.Add(Mouse(0, 0, MOUSEEVENTF_HWHEEL, deltaX));
        Send(inputs.ToArray());
    }

    public void Key(ushort hidUsage, bool down)
    {
        if (!KeyMap.TryGet(hidUsage, out var map))
        {
            return;
        }

        lock (_sync)
        {
            if (down) _pressedKeys.Add(hidUsage); else _pressedKeys.Remove(hidUsage);
        }

        Send(KeyInput(map, down));
    }

    public void ReleaseAll()
    {
        ushort[] keys;
        MouseButton[] buttons;
        lock (_sync)
        {
            keys = _pressedKeys.ToArray();
            buttons = _pressedButtons.ToArray();
            _pressedKeys.Clear();
            _pressedButtons.Clear();
        }

        foreach (var hid in keys)
        {
            if (KeyMap.TryGet(hid, out var map))
            {
                Send(KeyInput(map, down: false));
            }
        }

        foreach (var b in buttons)
        {
            uint flag = b switch
            {
                MouseButton.Left => MOUSEEVENTF_LEFTUP,
                MouseButton.Right => MOUSEEVENTF_RIGHTUP,
                MouseButton.Middle => MOUSEEVENTF_MIDDLEUP,
                _ => 0,
            };
            if (flag != 0)
            {
                Send(Mouse(0, 0, flag, 0));
            }
        }
    }

    public void Dispose() => ReleaseAll();

    /// <summary>Normalized display position → SendInput absolute coordinates over the whole virtual desktop.</summary>
    internal static (int X, int Y) ToVirtualDesk(DisplayInfo display, double nx, double ny)
    {
        var (px, py) = FrameGeometry.ToAbsolute(display, nx, ny);
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        return VirtualDeskMath.Normalize(px, py, vx, vy, vw, vh);
    }

    private static INPUT Mouse(int dx, int dy, uint flags, int data) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = data } },
    };

    private static INPUT KeyInput(KeyMapping map, bool down)
    {
        uint flags = down ? 0 : KEYEVENTF_KEYUP;
        if (map.WinVk != 0)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = map.WinVk, dwFlags = flags | (map.WinExtended ? KEYEVENTF_EXTENDEDKEY : 0) } },
            };
        }

        flags |= KEYEVENTF_SCANCODE;
        if (map.WinExtended)
        {
            flags |= KEYEVENTF_EXTENDEDKEY;
        }

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wScan = map.WinScan, dwFlags = flags } },
        };
    }

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length > 0)
        {
            SendInput((uint)inputs.Length, inputs, InputSize);
        }
    }
}

public static class VirtualDeskMath
{
    /// <summary>Maps an absolute virtual-desktop pixel to SendInput's 0..65535 range (handles negative origins).</summary>
    public static (int X, int Y) Normalize(double px, double py, int vx, int vy, int vw, int vh)
    {
        int nx = (int)Math.Round((px - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((py - vy) * 65535.0 / (vh - 1));
        return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
    }
}
