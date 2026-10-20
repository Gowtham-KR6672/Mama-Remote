using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using RemoteConnect.Protocol;

namespace RemoteConnect.Platform.Windows;

/// <summary>
/// The desktop that currently receives keyboard and mouse input: "Default" for the signed-in user, "Winlogon" for the
/// sign-in, lock and UAC (secure desktop) screens. Only a LocalSystem process (the service-mode agent) may attach to
/// "Winlogon"; for a normal user process the secure desktop stays out of reach, which is the intended protection.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InputDesktop
{
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_CREATEWINDOW = 0x0002;
    private const uint DESKTOP_CREATEMENU = 0x0004;
    private const uint DESKTOP_HOOKCONTROL = 0x0008;
    private const uint DESKTOP_JOURNALPLAYBACK = 0x0020;
    private const uint DESKTOP_ENUMERATE = 0x0040;
    private const uint DESKTOP_WRITEOBJECTS = 0x0080;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const uint GENERIC_WRITE = 0x40000000;
    private const int UOI_NAME = 2;

    /// <summary>What capture (GDI reads) and SendInput (journal playback) need on the desktop.</summary>
    internal const uint Access = DESKTOP_READOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_CREATEMENU | DESKTOP_HOOKCONTROL
        | DESKTOP_JOURNALPLAYBACK | DESKTOP_ENUMERATE | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP | GENERIC_WRITE;

    /// <summary>Name of the input desktop, or null when this process may not open it.</summary>
    public static string? TryGetName()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return GetName(desktop);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    internal static string? GetName(IntPtr desktop)
    {
        var buffer = new byte[256];
        return GetUserObjectInformation(desktop, UOI_NAME, buffer, buffer.Length, out int needed) && needed >= 2
            ? Encoding.Unicode.GetString(buffer, 0, needed - 2) // minus the terminating NUL
            : null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetUserObjectInformationW")]
    private static extern bool GetUserObjectInformation(IntPtr obj, int index, [Out] byte[] info, int length, out int needed);
}

/// <summary>
/// A dedicated thread that attaches itself to the current input desktop before every piece of work. GDI capture and
/// SendInput act on the calling thread's desktop, so this is what lets the service-mode agent see and operate the
/// sign-in screen, and follow the switch to the user's desktop after sign-in. A dedicated thread is required because
/// <c>SetThreadDesktop</c> only works on a thread that owns no windows or hooks, and must not leak into the thread pool.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class InputDesktopThread : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private IntPtr _desktop;

    public InputDesktopThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>The desktop the thread is attached to (null until the first successful switch).</summary>
    public string? CurrentDesktop { get; private set; }

    public T Invoke<T>(Func<T> work)
    {
        if (Thread.CurrentThread == _thread)
        {
            return work();
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                try
                {
                    FollowInputDesktop();
                    done.SetResult(work());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(InputDesktopThread));
        }

        return done.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action work) => Invoke(() =>
    {
        work();
        return true;
    });

    /// <summary>Switches this thread to the input desktop if it changed. If it cannot be opened, the thread stays put.</summary>
    private void FollowInputDesktop()
    {
        var input = InputDesktop.OpenInputDesktop(0, false, InputDesktop.Access);
        if (input == IntPtr.Zero)
        {
            return;
        }

        var name = InputDesktop.GetName(input);
        if (_desktop != IntPtr.Zero && name == CurrentDesktop)
        {
            InputDesktop.CloseDesktop(input);
            return;
        }

        if (!InputDesktop.SetThreadDesktop(input))
        {
            InputDesktop.CloseDesktop(input);
            return;
        }

        // The previous handle can only be closed once the thread no longer uses it.
        if (_desktop != IntPtr.Zero)
        {
            InputDesktop.CloseDesktop(_desktop);
        }

        _desktop = input;
        CurrentDesktop = name;
    }

    private void Run()
    {
        foreach (var work in _work.GetConsumingEnumerable())
        {
            work();
        }
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        // The thread has exited (or is stuck in native code); either way its desktop handle is no longer switched to.
        if (_desktop != IntPtr.Zero && !_thread.IsAlive)
        {
            InputDesktop.CloseDesktop(_desktop);
            _desktop = IntPtr.Zero;
        }

        _work.Dispose();
    }
}

/// <summary>Screen capture that follows the input desktop (service mode).</summary>
[SupportedOSPlatform("windows")]
public sealed class InputDesktopCapturer : IScreenCapturer
{
    private readonly IScreenCapturer _inner;
    private readonly InputDesktopThread _thread = new("RemoteConnect capture (input desktop)");

    public InputDesktopCapturer(IScreenCapturer inner) => _inner = inner;

    public IReadOnlyList<DisplayInfo> GetDisplays() => _thread.Invoke(_inner.GetDisplays);

    public bool Capture(DisplayInfo display, FrameBuffer target, int maxWidth, int maxHeight)
        => _thread.Invoke(() => _inner.Capture(display, target, maxWidth, maxHeight));

    public void Dispose()
    {
        try
        {
            _thread.Invoke(_inner.Dispose);
        }
        finally
        {
            _thread.Dispose();
        }
    }
}

/// <summary>Input injection that follows the input desktop (service mode).</summary>
[SupportedOSPlatform("windows")]
public sealed class InputDesktopInjector : IInputInjector
{
    private readonly IInputInjector _inner;
    private readonly InputDesktopThread _thread = new("RemoteConnect input (input desktop)");

    public InputDesktopInjector(IInputInjector inner) => _inner = inner;

    public void MovePointer(DisplayInfo display, double nx, double ny) => _thread.Invoke(() => _inner.MovePointer(display, nx, ny));

    public void PointerButton(DisplayInfo display, double nx, double ny, MouseButton button, bool down)
        => _thread.Invoke(() => _inner.PointerButton(display, nx, ny, button, down));

    public void Scroll(DisplayInfo display, double nx, double ny, int deltaX, int deltaY)
        => _thread.Invoke(() => _inner.Scroll(display, nx, ny, deltaX, deltaY));

    public void Key(ushort hidUsage, bool down) => _thread.Invoke(() => _inner.Key(hidUsage, down));

    public void ReleaseAll() => _thread.Invoke(_inner.ReleaseAll);

    public void Dispose()
    {
        try
        {
            _thread.Invoke(_inner.Dispose);
        }
        finally
        {
            _thread.Dispose();
        }
    }
}
