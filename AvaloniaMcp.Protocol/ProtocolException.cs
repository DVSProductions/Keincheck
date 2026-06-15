namespace AvaloniaMcp.Protocol;

/// <summary>
/// Thrown when the broker wire protocol is violated: a truncated frame, a chunk
/// whose magic does not match, an over-large chunk/message, or a version
/// handshake mismatch. The broker should treat this as fatal for the offending
/// connection and tear it down.
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException) { }
}
