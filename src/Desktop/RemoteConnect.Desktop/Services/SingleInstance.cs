using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace RemoteConnect.Desktop.Services;

/// <summary>
/// One RemoteConnect per user session. A second launch (e.g. clicking the shortcut while the app sits in the tray)
/// asks the running instance to show its window and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Suffix = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "|" + AppPaths.ConfigDirectory)))[..16];

    private static readonly string MutexName = "RemoteConnect.Desktop." + Suffix;
    private static readonly string PipeName = "RemoteConnect.Activate." + Suffix;

    private readonly Mutex _mutex;
    private CancellationTokenSource? _cts;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero))
            {
                return new SingleInstance(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstance(mutex); // previous instance crashed; we now own it
        }

        mutex.Dispose();
        return null;
    }

    public static void SignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            client.WriteByte(1);
        }
        catch (Exception)
        {
            // The running instance will still be in the tray.
        }
    }

    public void ListenForActivation(Action onActivate)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var buffer = new byte[1];
                    _ = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
                    onActivate();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
    }
}
