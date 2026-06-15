using System.Text.Json;

namespace AvaloniaMcp.Protocol;

/// <summary>
/// A message-oriented duplex channel over an arbitrary byte <see cref="Stream"/>
/// (typically a <see cref="System.IO.Pipes.NamedPipeServerStream"/> or
/// <see cref="System.IO.Pipes.NamedPipeClientStream"/>). It serializes a
/// <see cref="MessageEnvelope"/> with <see cref="ProtocolJson.Options"/> and frames
/// it with <see cref="FrameCodec"/>; reads do the reverse. Writes are serialized by
/// an internal lock so concurrent senders cannot interleave chunks on the wire.
/// </summary>
/// <remarks>
/// The channel does <b>not</b> own thread affinity or correlation/dispatch logic —
/// that belongs to the Client/Hub. It is a thin, transport-agnostic codec wrapper so
/// both ends share exactly one framing + (de)serialization path. Dispose to close
/// the underlying stream.
/// </remarks>
public sealed class PipeChannel : IAsyncDisposable, IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    /// <summary>
    /// Wraps <paramref name="stream"/>. When <paramref name="ownsStream"/> is true
    /// (the default) disposing the channel disposes the stream.
    /// </summary>
    public PipeChannel(Stream stream, bool ownsStream = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
    }

    /// <summary>The underlying transport stream (e.g. for liveness checks).</summary>
    public Stream Stream => _stream;

    /// <summary>True once the channel has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Serializes and frames <paramref name="envelope"/> to the wire. Concurrent
    /// callers are serialized so chunk sequences never interleave.
    /// </summary>
    public async Task SendAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_stream, bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Convenience: wrap <paramref name="message"/> in an envelope of
    /// <paramref name="kind"/> (optionally correlated) and send it.
    /// </summary>
    public Task SendAsync<T>(MessageKind kind, T message, string? correlationId = null,
        CancellationToken cancellationToken = default)
        => SendAsync(MessageEnvelope.Wrap(kind, message, correlationId), cancellationToken);

    /// <summary>
    /// Reads exactly one <see cref="MessageEnvelope"/>, or returns <c>null</c> on a
    /// clean end-of-stream between frames (the peer closed the connection).
    /// </summary>
    /// <exception cref="ProtocolException">The frame was malformed or truncated.</exception>
    public async Task<MessageEnvelope?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await FrameCodec.TryReadAsync(_stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return null;

        var envelope = JsonSerializer.Deserialize<MessageEnvelope>(bytes, ProtocolJson.Options);
        return envelope ?? throw new ProtocolException("Received a frame that deserialized to a null envelope.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _writeLock.Dispose();
        if (_ownsStream)
            _stream.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _writeLock.Dispose();
        if (_ownsStream)
            await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
