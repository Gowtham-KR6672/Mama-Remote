using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteConnect.Platform;
using RemoteConnect.Protocol;
using RemoteConnect.Session.Controller;
using RemoteConnect.Session.Host;

namespace RemoteConnect.IntegrationTests;

/// <summary>Test-only synthetic screen (a moving bar) so sessions can run without touching the real desktop.</summary>
internal sealed class SyntheticCapturer : IScreenCapturer
{
    private int _frame;

    public IReadOnlyList<DisplayInfo> GetDisplays()
        => new[] { new DisplayInfo(0, "Synthetic", true, 0, 0, 640, 360, 640, 360) };

    public bool Capture(DisplayInfo display, FrameBuffer target, int maxWidth, int maxHeight)
    {
        target.EnsureSize(640, 360);
        int bar = (Interlocked.Increment(ref _frame) * 16) % 640;
        var px = target.Pixels;
        for (int y = 0; y < 360; y++)
        {
            for (int x = 0; x < 640; x++)
            {
                int o = (y * target.Stride) + (x * 4);
                bool inBar = x >= bar && x < bar + 32;
                px[o] = (byte)(inBar ? 255 : x / 3);
                px[o + 1] = (byte)(inBar ? 255 : y / 2);
                px[o + 2] = (byte)(inBar ? 255 : 80);
                px[o + 3] = 255;
            }
        }

        return true;
    }

    public void Dispose()
    {
    }
}

/// <summary>Test-only injector that records instead of moving the real mouse/keyboard.</summary>
internal sealed class RecordingInjector : IInputInjector
{
    public ConcurrentQueue<string> Events { get; } = new();

    public int ReleaseAllCalls;

    public void MovePointer(DisplayInfo display, double nx, double ny) => Events.Enqueue($"move {nx:0.00},{ny:0.00}");

    public void PointerButton(DisplayInfo display, double nx, double ny, MouseButton button, bool down)
        => Events.Enqueue($"{button} {(down ? "down" : "up")} {nx:0.00},{ny:0.00}");

    public void Scroll(DisplayInfo display, double nx, double ny, int deltaX, int deltaY) => Events.Enqueue($"scroll {deltaX},{deltaY}");

    public void Key(ushort hidUsage, bool down) => Events.Enqueue($"key {hidUsage:X2} {(down ? "down" : "up")}");

    public void ReleaseAll() => Interlocked.Increment(ref ReleaseAllCalls);

    public void Dispose()
    {
    }
}

internal sealed class ScriptedInteraction : IHostInteraction
{
    private readonly Func<IncomingRequest, CancellationToken, Task<ApprovalDecision>> _decide;

    public ScriptedInteraction(Func<IncomingRequest, CancellationToken, Task<ApprovalDecision>> decide) => _decide = decide;

    public static ScriptedInteraction Always(ApprovalDecision d) => new((_, _) => Task.FromResult(d));

    /// <summary>Nobody at the computer: the prompt is never answered.</summary>
    public static ScriptedInteraction NeverAnswers() => new(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return ApprovalDecision.Reject;
    });

    public ConcurrentQueue<IncomingRequest> Requests { get; } = new();

    public TaskCompletionSource<HostSession> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<DisconnectReason> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ApprovalDecision> RequestApprovalAsync(IncomingRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return _decide(request, ct);
    }

    public void OnSessionStarted(HostSession session) => Started.TrySetResult(session);

    public void OnSessionEnded(HostSession session, DisconnectReason reason, string? detail) => Ended.TrySetResult(reason);
}

internal sealed class MemoryKnownHosts : IKnownHostStore
{
    private readonly ConcurrentDictionary<string, string> _map = new();

    public string? GetFingerprint(string key) => _map.TryGetValue(key, out var v) ? v : null;

    public void Remember(string key, string fingerprint) => _map[key] = fingerprint;
}

/// <summary>Captures log lines so a failing integration test can show what each side did.</summary>
internal sealed class ListLogger(string prefix, ConcurrentQueue<string> sink) : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => sink.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {prefix} {logLevel}: {formatter(state, exception)} {exception?.GetType().Name} {exception?.Message}");
}

internal sealed class ListLoggerProvider(ConcurrentQueue<string> sink) : Microsoft.Extensions.Logging.ILoggerProvider
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new ListLogger(categoryName[(categoryName.LastIndexOf('.') + 1)..], sink);

    public void Dispose()
    {
    }
}

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(20);
        }
    }

    public static NullLogger Log => NullLogger.Instance;
}
