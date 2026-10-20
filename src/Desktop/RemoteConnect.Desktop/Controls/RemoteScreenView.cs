using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RemoteConnect.Desktop.Services;
using RemoteConnect.Protocol;
using RemoteConnect.Session.Controller;
using MouseButton = RemoteConnect.Protocol.MouseButton;

namespace RemoteConnect.Desktop.Controls;

/// <summary>
/// Shows the remote screen scaled to fit (letterboxed) and forwards pointer, wheel and keyboard input with
/// coordinates normalized to the remote monitor, so DPI and resolution differences do not matter.
/// </summary>
public sealed class RemoteScreenView : Control
{
    public static readonly StyledProperty<ControllerSession?> SessionProperty =
        AvaloniaProperty.Register<RemoteScreenView, ControllerSession?>(nameof(Session));

    private readonly HashSet<ushort> _pressedKeys = new();
    private WriteableBitmap? _bitmap;
    private long _shownVersion = -1;
    private int _updateQueued;
    private ControllerSession? _attached;

    static RemoteScreenView()
    {
        FocusableProperty.OverrideDefaultValue<RemoteScreenView>(true);
        AffectsRender<RemoteScreenView>(SessionProperty);
    }

    public RemoteScreenView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.MediumQuality);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
    }

    public ControllerSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty)
        {
            if (_attached is not null)
            {
                _attached.FrameUpdated -= OnFrameUpdated;
            }

            _attached = change.GetNewValue<ControllerSession?>();
            if (_attached is not null)
            {
                _attached.FrameUpdated += OnFrameUpdated;
            }

            _shownVersion = -1;
        }
    }

    private void OnFrameUpdated()
    {
        // Coalesce: at most one pending UI update, however fast frames arrive.
        if (Interlocked.Exchange(ref _updateQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(CopyFrame, DispatcherPriority.Render);
        }
    }

    private void CopyFrame()
    {
        Interlocked.Exchange(ref _updateQueued, 0);
        var fb = _attached?.Framebuffer;
        if (fb is null)
        {
            return;
        }

        lock (fb.Sync)
        {
            if (fb.Width == 0 || fb.Version == _shownVersion)
            {
                return;
            }

            if (_bitmap is null || _bitmap.PixelSize.Width != fb.Width || _bitmap.PixelSize.Height != fb.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(fb.Width, fb.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }

            using var locked = _bitmap.Lock();
            if (locked.RowBytes == fb.Stride)
            {
                Marshal.Copy(fb.Pixels, 0, locked.Address, fb.Stride * fb.Height);
            }
            else
            {
                for (int y = 0; y < fb.Height; y++)
                {
                    Marshal.Copy(fb.Pixels, y * fb.Stride, locked.Address + (y * locked.RowBytes), fb.Stride);
                }
            }

            _shownVersion = fb.Version;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap is not null)
        {
            context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), DestinationRect());
        }
    }

    private Rect DestinationRect()
    {
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return default;
        }

        double scale = Math.Min(Bounds.Width / _bitmap.PixelSize.Width, Bounds.Height / _bitmap.PixelSize.Height);
        double w = _bitmap.PixelSize.Width * scale;
        double h = _bitmap.PixelSize.Height * scale;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    private bool TryNormalize(Point p, out double nx, out double ny)
    {
        var r = DestinationRect();
        nx = ny = 0;
        if (r.Width <= 0 || r.Height <= 0)
        {
            return false;
        }

        nx = Math.Clamp((p.X - r.X) / r.Width, 0, 1);
        ny = Math.Clamp((p.Y - r.Y) / r.Height, 0, 1);
        return true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Session is { } s && TryNormalize(e.GetPosition(this), out var nx, out var ny))
        {
            s.SendPointer(PointerAction.Move, MouseButton.None, nx, ny);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        var button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => MouseButton.Left,
            PointerUpdateKind.RightButtonPressed => MouseButton.Right,
            PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
            _ => MouseButton.None,
        };
        if (button != MouseButton.None && Session is { } s && TryNormalize(point.Position, out var nx, out var ny))
        {
            e.Pointer.Capture(this);
            s.SendPointer(PointerAction.Down, button, nx, ny);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetCurrentPoint(this);
        var button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => MouseButton.Left,
            PointerUpdateKind.RightButtonReleased => MouseButton.Right,
            PointerUpdateKind.MiddleButtonReleased => MouseButton.Middle,
            _ => MouseButton.None,
        };
        if (button != MouseButton.None && Session is { } s && TryNormalize(point.Position, out var nx, out var ny))
        {
            s.SendPointer(PointerAction.Up, button, nx, ny);
            e.Handled = true;
        }

        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed && !point.Properties.IsMiddleButtonPressed)
        {
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Session is { } s && TryNormalize(e.GetPosition(this), out var nx, out var ny))
        {
            // Avalonia: +Y = scroll up, +X = scroll left; the wire uses Windows wheel units (+X = right).
            s.SendScroll(nx, ny, (int)Math.Round(-e.Delta.X * 120), (int)Math.Round(e.Delta.Y * 120));
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Session is { } s && KeyMapper.TryGetHid(e.PhysicalKey, out var hid))
        {
            _pressedKeys.Add(hid);
            s.SendKey(hid, down: true);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (Session is { } s && KeyMapper.TryGetHid(e.PhysicalKey, out var hid))
        {
            _pressedKeys.Remove(hid);
            s.SendKey(hid, down: false);
            e.Handled = true;
            return;
        }

        base.OnKeyUp(e);
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        ReleaseKeys();
    }

    /// <summary>When focus leaves (Alt+Tab etc.) the key-up never reaches us; release on the remote side.</summary>
    public void ReleaseKeys()
    {
        if (Session is { } s)
        {
            foreach (var hid in _pressedKeys)
            {
                s.SendKey(hid, down: false);
            }
        }

        _pressedKeys.Clear();
    }

    /// <summary>Sends a key chord (e.g. Ctrl+Alt+Del is impossible; this is for shortcuts the local OS would intercept).</summary>
    public void SendChord(params ushort[] hids)
    {
        if (Session is not { } s)
        {
            return;
        }

        foreach (var h in hids)
        {
            s.SendKey(h, true);
        }

        foreach (var h in hids.Reverse())
        {
            s.SendKey(h, false);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_attached is not null)
        {
            _attached.FrameUpdated -= OnFrameUpdated;
        }

        _bitmap?.Dispose();
        _bitmap = null;
    }
}
