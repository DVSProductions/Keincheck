using System.Net.WebSockets;

namespace Keincheck.Protocol;

/// <summary>
/// Presents a <see cref="WebSocket"/> as a byte <see cref="Stream"/>, so a WebSocket session
/// can be handed to <see cref="PipeChannel"/> and reuse the one framing and serialization path
/// every other transport already uses.
/// </summary>
/// <remarks>
/// <para>
/// This is the same shape <c>Keincheck.Remote</c> uses for TLS (<c>SslStream</c> over
/// <c>NetworkStream</c> over a socket): the channel does not care what the bytes travel on.
/// Reusing <see cref="FrameCodec"/> rather than mapping one protocol message onto one WebSocket
/// message is deliberate — the codec already handles the chunking that a 4 MB screenshot needs,
/// and a second framing scheme would be a second place for the two ends to disagree.
/// </para>
/// <para>
/// Takes the <see cref="WebSocket"/> base type, not <see cref="ClientWebSocket"/>, so the same
/// adapter serves the client side and the hub's accept side.
/// </para>
/// <para>
/// <b>Async only.</b> The synchronous <see cref="Read(byte[], int, int)"/> and
/// <see cref="Write(byte[], int, int)"/> throw: browser WebAssembly is single-threaded and
/// cannot block on a socket at all, so a synchronous path would deadlock the one platform this
/// stream exists to serve.
/// </para>
/// </remarks>
public sealed class WebSocketStream : Stream
{
    private readonly WebSocket _socket;
    private readonly bool _ownsSocket;

    // Bytes received from the peer that the reader has not consumed yet. A WebSocket delivers
    // messages, a Stream hands out whatever the caller asks for, so a single receive routinely
    // satisfies several reads.
    private byte[] _pending = [];
    private int _pendingOffset;
    private int _pendingCount;

    private bool _peerClosed;

    /// <param name="socket">The connected WebSocket. Must already be <see cref="WebSocketState.Open"/>.</param>
    /// <param name="ownsSocket">
    /// When true (the default) disposing the stream closes the WebSocket. The hub's accept side
    /// passes false, because ASP.NET Core owns the socket for the lifetime of the request.
    /// </param>
    public WebSocketStream(WebSocket socket, bool ownsSocket = true)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
        _ownsSocket = ownsSocket;
    }

    /// <summary>The underlying socket, for callers that need its close status.</summary>
    public WebSocket Socket => _socket;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;

        // Drain whatever the last receive left over before going back to the socket.
        if (_pendingCount == 0)
        {
            if (_peerClosed)
                return 0;

            if (!await ReceiveIntoPendingAsync(buffer.Length, cancellationToken).ConfigureAwait(false))
                return 0;
        }

        var n = Math.Min(_pendingCount, buffer.Length);
        _pending.AsSpan(_pendingOffset, n).CopyTo(buffer.Span);
        _pendingOffset += n;
        _pendingCount -= n;
        return n;
    }

    /// <summary>
    /// Pulls one WebSocket frame into the pending buffer. Returns false at end of stream.
    /// </summary>
    private async ValueTask<bool> ReceiveIntoPendingAsync(int hint, CancellationToken cancellationToken)
    {
        // Grow to at least the caller's ask so a large read is usually one receive, but keep a
        // floor so a byte-at-a-time reader does not thrash the allocator.
        var size = Math.Max(hint, 8 * 1024);
        if (_pending.Length < size)
            _pending = new byte[size];

        _pendingOffset = 0;
        _pendingCount = 0;

        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await _socket
                    .ReceiveAsync(_pending.AsMemory(0, _pending.Length), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                // An abrupt disconnect is EOF to a stream reader, which is what every caller
                // above already knows how to handle. Anything else is a real fault.
                if (ex.WebSocketErrorCode is WebSocketError.ConnectionClosedPrematurely)
                {
                    _peerClosed = true;
                    return false;
                }
                throw;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _peerClosed = true;
                return false;
            }

            // A zero-length binary frame carries nothing; wait for the next one rather than
            // reporting EOF, which would tear down a perfectly healthy session.
            if (result.Count == 0)
                continue;

            _pendingCount = result.Count;
            return true;
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return;

        // endOfMessage: true per write. PipeChannel serializes its writes behind a lock and
        // FrameCodec carries the real message boundaries, so each send is self-contained and
        // the peer never has to reassemble a partial protocol frame from WebSocket state.
        await _socket
            .SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { /* nothing is buffered on the write side */ }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("WebSocketStream is async-only; use ReadAsync.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("WebSocketStream is async-only; use WriteAsync.");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (_ownsSocket)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket
                        .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort courtesy close; the dispose below is what actually frees it.
            }
            _socket.Dispose();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsSocket)
            _socket.Dispose();
        base.Dispose(disposing);
    }
}
