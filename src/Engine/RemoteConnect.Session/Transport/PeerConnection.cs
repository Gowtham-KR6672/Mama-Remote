using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using RemoteConnect.Protocol;
using RemoteConnect.Security;

namespace RemoteConnect.Session.Transport;

public enum ConnectionPath
{
    Direct,
    Relay,
}

/// <summary>
/// An authenticated, encrypted (mutual TLS) message channel to one peer. Incoming messages are pumped into a
/// bounded queue by a single background reader; writes are serialized.
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    private readonly SslStream _ssl;
    private readonly IDisposable? _transport;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Frame> _inbox = Channel.CreateBounded<Frame>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private Task? _readLoop;
    private long _bytesSent;
    private long _bytesReceived;
    private int _disposed;

    internal PeerConnection(SslStream ssl, IDisposable? transport, X509Certificate2 remoteCertificate, string remoteEndpoint, ConnectionPath path)
    {
        _ssl = ssl;
        _transport = transport;
        RemoteCertificate = remoteCertificate;
        RemoteFingerprint = CertificateFingerprint.Compute(remoteCertificate);
        RemoteDeviceId = PublicCertificate.DeriveDeviceId(remoteCertificate);
        RemoteEndpoint = remoteEndpoint;
        Path = path;
    }

    public X509Certificate2 RemoteCertificate { get; }

    public string RemoteFingerprint { get; }

    /// <summary>Derived from the peer's TLS certificate — cannot be claimed without the private key.</summary>
    public string RemoteDeviceId { get; }

    public string RemoteEndpoint { get; }

    public ConnectionPath Path { get; }

    public string TlsProtocol => _ssl.SslProtocol.ToString();

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public Exception? Error { get; private set; }

    public bool IsClosed => _inbox.Reader.Completion.IsCompleted;

    public void StartReceiving() => _readLoop ??= Task.Run(ReadLoopAsync);

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await MessageFramer.ReadAsync(_ssl, _cts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                Interlocked.Add(ref _bytesReceived, frame.Value.Length + 5);
                await _inbox.Writer.WriteAsync(frame.Value, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = ex;
        }
        finally
        {
            _inbox.Writer.TryComplete();
        }
    }

    /// <summary>Waits for a message without consuming it (safe to abandon). False when the connection closed.</summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken ct) => _inbox.Reader.WaitToReadAsync(ct);

    public bool TryRead(out Frame frame) => _inbox.Reader.TryRead(out frame);

    /// <summary>Next message, or null when the connection has closed.</summary>
    public async ValueTask<Frame?> ReceiveAsync(CancellationToken ct)
    {
        while (await _inbox.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (_inbox.Reader.TryRead(out var frame))
            {
                return frame;
            }
        }

        return null;
    }

    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The connection token (not the caller's) guards the write itself: a half-written frame would corrupt the stream.
            await MessageFramer.WriteAsync(_ssl, type, payload, _cts.Token).ConfigureAwait(false);
            await _ssl.FlushAsync(_cts.Token).ConfigureAwait(false);
            Interlocked.Add(ref _bytesSent, payload.Length + 5);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task SendJsonAsync<T>(MessageType type, T message, CancellationToken ct = default)
        => SendAsync(type, MessageFramer.SerializeJson(message), ct);

    /// <summary>Sends a final message without waiting long; used right before closing.</summary>
    public async Task TrySendAsync(MessageType type, ReadOnlyMemory<byte> payload, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await SendAsync(type, payload, cts.Token).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Connection already gone.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _ssl.Dispose();
        }
        catch (Exception)
        {
        }

        _transport?.Dispose();
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _inbox.Writer.TryComplete();
        _cts.Dispose();
    }
}
