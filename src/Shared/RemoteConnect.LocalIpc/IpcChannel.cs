namespace RemoteConnect.LocalIpc;

/// <summary>Typed, validated messages over an already-authenticated stream (the service pipe).</summary>
public sealed class IpcChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IpcSender _self;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public IpcChannel(Stream stream, IpcSender self, int peerProcessId)
    {
        _stream = stream;
        _self = self;
        PeerProcessId = peerProcessId;
    }

    /// <summary>The verified process on the other end of the pipe.</summary>
    public int PeerProcessId { get; }

    public async Task SendAsync(IpcMessage message, CancellationToken ct)
    {
        var frame = IpcCodec.Encode(message, _self);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Next message, or null when the peer closed the pipe. Throws <see cref="IpcProtocolException"/> on a bad frame.</summary>
    public async Task<IpcMessage?> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[IpcCodec.HeaderSize];
        int read = await ReadFullyAsync(header, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < header.Length)
        {
            throw new IpcProtocolException("Truncated header.");
        }

        int length = IpcCodec.ReadHeader(header, out var type);
        var payload = new byte[length];
        if (await ReadFullyAsync(payload, ct).ConfigureAwait(false) < length)
        {
            throw new IpcProtocolException("Truncated message.");
        }

        var peer = _self == IpcSender.Service ? IpcSender.Agent : IpcSender.Service;
        return IpcCodec.Decode(type, payload, peer);
    }

    private async Task<int> ReadFullyAsync(byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
