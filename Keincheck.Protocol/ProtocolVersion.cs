namespace Keincheck.Protocol;

/// <summary>
/// The wire-protocol version handshake for the Keincheck broker IPC channel.
/// A client (an embedded Keincheck instance inside a host app) and the hub
/// (the broker process) MUST agree on <see cref="Current"/> before exchanging
/// any other message; a mismatch aborts the connection.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>
    /// The current protocol version. Bump this whenever the DTO shapes, the
    /// framing format, or the handshake semantics change incompatibly.
    /// </summary>
    /// <remarks>
    /// v2 adds the remote-session handshake (<see cref="MessageKind.Hello"/> /
    /// <see cref="MessageKind.Welcome"/> / <see cref="MessageKind.Rejected"/>), credential
    /// enrollment over the control pipe, Brotli frame compression, and
    /// <see cref="ToolDescriptor.ReadOnly"/>. All of it is additive: a v1 client that
    /// registers on the pipe without ever sending a Hello behaves exactly as before, which
    /// is why <see cref="Minimum"/> stays at 1.
    /// <para>
    /// <see cref="RegisterMessage.LaunchToken"/> was added later, and deliberately needed no
    /// bump: it is one optional field on a message both sides already exchange. A client that
    /// predates it simply never sends it, and the hub falls back to matching a launch on
    /// process id — which is what it did before the field existed.
    /// </para>
    /// </remarks>
    public const int Current = 2;

    /// <summary>
    /// The lowest protocol version this build can still speak. The hub accepts a
    /// client whose advertised version is in
    /// <c>[<see cref="Minimum"/>, <see cref="Current"/>]</c>.
    /// </summary>
    public const int Minimum = 1;

    /// <summary>
    /// A short magic token sent at the very start of a connection so a peer can
    /// quickly reject a stream that is not an Keincheck broker channel.
    /// </summary>
    public const string Magic = "AMCP";

    /// <summary>
    /// Returns <c>true</c> if a peer advertising <paramref name="version"/> is
    /// compatible with this build (i.e. within the supported range).
    /// </summary>
    public static bool IsCompatible(int version) =>
        version >= Minimum && version <= Current;
}
