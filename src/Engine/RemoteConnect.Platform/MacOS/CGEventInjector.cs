using System.Runtime.Versioning;
using RemoteConnect.Protocol;
using static RemoteConnect.Platform.MacOS.MacNative;

namespace RemoteConnect.Platform.MacOS;

/// <summary>
/// macOS input injection with Quartz events. Requires the Accessibility permission; without it macOS silently
/// drops the events. Modifier state is tracked and applied as event flags so shortcuts (⌘C, ⇧-click) work.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class CGEventInjector : IInputInjector
{
    private readonly IntPtr _source = CGEventSourceCreate(EventSourceStateHidSystem);
    private readonly object _sync = new();
    private readonly HashSet<ushort> _pressedKeys = new();
    private readonly HashSet<MouseButton> _pressedButtons = new();
    private ulong _flags;
    private int _scrollRemainderX;
    private int _scrollRemainderY;
    private DateTime _lastDownTime;
    private MouseButton _lastDownButton;
    private CGPoint _lastDownPoint;
    private int _clickCount;
    private bool _useScrollFallback;

    public void MovePointer(DisplayInfo display, double nx, double ny)
    {
        var p = ToPoint(display, nx, ny);
        int type;
        int button;
        lock (_sync)
        {
            (type, button) = _pressedButtons.Contains(MouseButton.Left) ? (EventLeftMouseDragged, MouseButtonLeft)
                : _pressedButtons.Contains(MouseButton.Right) ? (EventRightMouseDragged, MouseButtonRight)
                : _pressedButtons.Contains(MouseButton.Middle) ? (EventOtherMouseDragged, MouseButtonCenter)
                : (EventMouseMoved, MouseButtonLeft);
        }

        PostMouse(type, p, button, clickState: 0);
    }

    public void PointerButton(DisplayInfo display, double nx, double ny, MouseButton button, bool down)
    {
        var p = ToPoint(display, nx, ny);
        (int type, int cgButton) = (button, down) switch
        {
            (MouseButton.Left, true) => (EventLeftMouseDown, MouseButtonLeft),
            (MouseButton.Left, false) => (EventLeftMouseUp, MouseButtonLeft),
            (MouseButton.Right, true) => (EventRightMouseDown, MouseButtonRight),
            (MouseButton.Right, false) => (EventRightMouseUp, MouseButtonRight),
            (MouseButton.Middle, true) => (EventOtherMouseDown, MouseButtonCenter),
            (MouseButton.Middle, false) => (EventOtherMouseUp, MouseButtonCenter),
            _ => (-1, 0),
        };
        if (type < 0)
        {
            return;
        }

        int clickState;
        lock (_sync)
        {
            if (down)
            {
                // macOS needs an explicit click count for double/triple clicks.
                var now = DateTime.UtcNow;
                bool sameSpot = Math.Abs(p.X - _lastDownPoint.X) < 4 && Math.Abs(p.Y - _lastDownPoint.Y) < 4;
                _clickCount = button == _lastDownButton && sameSpot && (now - _lastDownTime).TotalMilliseconds < 500 ? _clickCount + 1 : 1;
                _lastDownTime = now;
                _lastDownButton = button;
                _lastDownPoint = p;
                _pressedButtons.Add(button);
            }
            else
            {
                _pressedButtons.Remove(button);
            }

            clickState = Math.Max(1, _clickCount);
        }

        PostMouse(type, p, cgButton, clickState);
    }

    public void Scroll(DisplayInfo display, double nx, double ny, int deltaX, int deltaY)
    {
        MovePointer(display, nx, ny);
        int lines, columns;
        lock (_sync)
        {
            _scrollRemainderY += deltaY;
            _scrollRemainderX += deltaX;
            lines = _scrollRemainderY / 120;
            columns = _scrollRemainderX / 120;
            _scrollRemainderY -= lines * 120;
            _scrollRemainderX -= columns * 120;
        }

        if (lines == 0 && columns == 0)
        {
            return;
        }

        IntPtr evt = IntPtr.Zero;
        if (!_useScrollFallback)
        {
            try
            {
                evt = CGEventCreateScrollWheelEvent2(_source, ScrollUnitLine, 2, lines, columns, 0);
            }
            catch (EntryPointNotFoundException)
            {
                _useScrollFallback = true; // macOS 12 and older
            }
        }

        if (evt == IntPtr.Zero)
        {
            evt = CGEventCreate(_source);
            if (evt == IntPtr.Zero)
            {
                return;
            }

            CGEventSetType(evt, EventScrollWheel);
            CGEventSetIntegerValueField(evt, FieldScrollWheelDeltaAxis1, lines);
            CGEventSetIntegerValueField(evt, FieldScrollWheelDeltaAxis2, columns);
        }

        Post(evt);
    }

    public void Key(ushort hidUsage, bool down)
    {
        if (!KeyMap.TryGet(hidUsage, out var map) || map.MacKey == KeyMap.NoMacKey)
        {
            return;
        }

        ulong flags;
        bool isModifier = KeyMap.IsModifier(hidUsage) || hidUsage == KeyMap.CapsLock;
        lock (_sync)
        {
            if (down) _pressedKeys.Add(hidUsage); else _pressedKeys.Remove(hidUsage);
            if (hidUsage == KeyMap.CapsLock)
            {
                if (down) _flags ^= FlagAlphaShift;
            }
            else if (KeyMap.IsModifier(hidUsage))
            {
                _flags = RecomputeModifierFlags(_flags & FlagAlphaShift);
            }

            flags = _flags;
        }

        IntPtr evt = CGEventCreateKeyboardEvent(_source, (ushort)map.MacKey, down);
        if (evt == IntPtr.Zero)
        {
            return;
        }

        if (isModifier)
        {
            CGEventSetType(evt, EventFlagsChanged);
        }

        CGEventSetFlags(evt, flags);
        Post(evt);
    }

    public void ReleaseAll()
    {
        ushort[] keys;
        MouseButton[] buttons;
        lock (_sync)
        {
            keys = _pressedKeys.ToArray();
            buttons = _pressedButtons.ToArray();
        }

        foreach (var k in keys)
        {
            Key(k, down: false);
        }

        if (buttons.Length > 0)
        {
            var loc = CurrentMouseLocation();
            foreach (var b in buttons)
            {
                int type = b switch { MouseButton.Left => EventLeftMouseUp, MouseButton.Right => EventRightMouseUp, _ => EventOtherMouseUp };
                int cg = b switch { MouseButton.Left => MouseButtonLeft, MouseButton.Right => MouseButtonRight, _ => MouseButtonCenter };
                PostMouse(type, loc, cg, 1);
            }
        }

        lock (_sync)
        {
            _pressedKeys.Clear();
            _pressedButtons.Clear();
            _flags &= FlagAlphaShift;
        }
    }

    public void Dispose()
    {
        ReleaseAll();
        if (_source != IntPtr.Zero)
        {
            CFRelease(_source);
        }
    }

    private ulong RecomputeModifierFlags(ulong baseFlags)
    {
        ulong f = baseFlags;
        if (_pressedKeys.Contains(KeyMap.LeftShift) || _pressedKeys.Contains(KeyMap.RightShift)) f |= FlagShift;
        if (_pressedKeys.Contains(KeyMap.LeftCtrl) || _pressedKeys.Contains(KeyMap.RightCtrl)) f |= FlagControl;
        if (_pressedKeys.Contains(KeyMap.LeftAlt) || _pressedKeys.Contains(KeyMap.RightAlt)) f |= FlagAlternate;
        if (_pressedKeys.Contains(KeyMap.LeftMeta) || _pressedKeys.Contains(KeyMap.RightMeta)) f |= FlagCommand;
        return f;
    }

    private void PostMouse(int type, CGPoint p, int button, int clickState)
    {
        IntPtr evt = CGEventCreateMouseEvent(_source, type, p, button);
        if (evt == IntPtr.Zero)
        {
            return;
        }

        if (clickState > 0)
        {
            CGEventSetIntegerValueField(evt, FieldMouseEventClickState, clickState);
        }

        ulong flags;
        lock (_sync)
        {
            flags = _flags;
        }

        CGEventSetFlags(evt, flags);
        Post(evt);
    }

    private static void Post(IntPtr evt)
    {
        try
        {
            CGEventPost(HidEventTap, evt);
        }
        finally
        {
            CFRelease(evt);
        }
    }

    private static CGPoint ToPoint(DisplayInfo display, double nx, double ny)
    {
        var (x, y) = FrameGeometry.ToAbsolute(display, nx, ny);
        return new CGPoint(x, y);
    }
}
