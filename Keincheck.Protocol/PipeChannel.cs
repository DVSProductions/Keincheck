using System.Text.Json;

namespace Keincheck.Protocol;

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
    private ChannelLimits _limits;
    private int _disposed;

    /// <summary>
    /// Wraps <paramref name="stream"/>. When <paramref name="ownsStream"/> is true
    /// (the default) disposing the channel disposes the stream — for a TLS session that
    /// means <c>SslStream</c> → <c>NetworkStream</c> → socket, provided the
    /// <c>SslStream</c> was built with <c>leaveInnerStreamOpen: false</c>.
    /// </summary>
    /// <param name="limits">
    /// Framing bounds; <see cref="ChannelLimits.Default"/> when omitted. A remote listener
    /// passes <see cref="ChannelLimits.Handshake"/> and widens it after accepting the session.
    /// </param>
    /// <remarks>
    /// Kept as the exact pre-v2 signature. Adding an optional parameter to this constructor
    /// instead would have broken binary compatibility with the published package: an assembly
    /// compiled against 0.9.0 would fail with <c>MissingMethodException</c> at runtime.
    /// </remarks>
    public PipeChannel(Stream stream, bool ownsStream = true)
        : this(stream, ownsStream, null)
    {
    }

    /// <inheritdoc cref="PipeChannel(Stream, bool)"/>
    /// <param name="stream">The transport to wrap.</param>
    /// <param name="ownsStream">Whether disposing the channel disposes the stream.</param>
    /// <param name="limits">
    /// Framing bounds; <see cref="ChannelLimits.Default"/> when null. A remote listener passes
    /// <see cref="ChannelLimits.Handshake"/> and widens it after accepting the session.
    /// </param>
    public PipeChannel(Stream stream, bool ownsStream, ChannelLimits? limits)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
        _limits = limits ?? ChannelLimits.Default;
    }

    /// <summary>The underlying transport stream (e.g. for liveness checks).</summary>
    public Stream Stream => _stream;

    /// <summary>
    /// The framing bounds applied to this channel. Settable so a remote session can be
    /// widened from <see cref="ChannelLimits.Handshake"/> to <see cref="ChannelLimits.Default"/>
    /// at exactly the moment it becomes trusted, and not before.
    /// </summary>
    public ChannelLimits Limits
    {
        get => Volatile.Read(ref _limits);
        set => Volatile.Write(ref _limits, value ?? throw new ArgumentNullException(nameof(value)));
    }

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
        var limits = Limits;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(
                _stream, bytes,
                limits.MaxChunkPayload,
                compress: limits.AllowCompression,
                cancellationToken).ConfigureAwait(false);
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
        var limits = Limits;
        var bytes = await FrameCodec.TryReadAsync(
            _stream, limits.MaxChunkPayload, limits.MaxMessageSize, cancellationToken).ConfigureAwait(false);
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
