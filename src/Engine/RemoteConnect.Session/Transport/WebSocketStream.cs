using System.Net.WebSockets;

namespace RemoteConnect.Session.Transport;

/// <summary>
/// Byte-stream view of a binary WebSocket so TLS can run end-to-end through the relay (the relay only ever sees
/// TLS records). One reader and one writer may be active at the same time, which is what SslStream does.
/// </summary>
internal sealed class WebSocketStream : Stream
{
    private readonly WebSocket _socket;
    private int _disposed;

    public WebSocketStream(WebSocket socket) => _socket = socket;

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // SslStream issues zero-byte "wait for data" reads before renting a buffer. A WebSocket receive into an
        // empty buffer would never yield data (and loop forever), so answer immediately; the real read follows.
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseSent))
            {
                return 0;
            }

            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return 0;
            }

            if (result.Count > 0)
            {
                return result.Count;
            }
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", cts.Token).GetAwaiter().GetResult();
                }
            }
            catch (Exception)
            {
                // Best effort close.
            }

            _socket.Dispose();
        }

        base.Dispose(disposing);
    }
}
