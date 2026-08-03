using Keincheck.Protocol;

namespace Keincheck.Client;

// Implements Keincheck.Protocol.IChannelConnector — the seam lives in the zero-dependency
// Protocol assembly so the hub can consume Keincheck.Remote without pulling in this one.

/// <summary>
/// The default connector: the local named pipe. Zero-config, fast, and reachable only by
/// the current user on this machine (<c>PipeOptions.CurrentUserOnly</c>).
/// </summary>
/// <remarks>
/// No handshake beyond the connect itself — the pipe's OS-level ACL <i>is</i> the
/// authentication, and the session begins at <see cref="MessageKind.Register"/> exactly as
/// it did before the connector seam existed. No read timeout either: a dead peer on a pipe
/// closes it, which surfaces as a clean EOF.
/// </remarks>
public sealed class PipeChannelConnector : IChannelConnector
{
    private readonly string? _pipeName;
    private readonly TimeSpan _connectTimeout;

    /// <param name="pipeName">The pipe to reach the hub on; <c>PipeNames.ControlPipe</c> when null.</param>
    /// <param name="connectTimeout">How long to keep retrying the connect before giving up.</param>
    public PipeChannelConnector(string? pipeName, TimeSpan connectTimeout)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout;
    }

    /// <inheritdoc/>
    public async Task<ChannelSession> ConnectAsync(ChannelConnectContext context, CancellationToken cancellationToken)
    {
        var channel = await PipeTransport
            .ConnectAsync(_pipeName, _connectTimeout, cancellationToken)
            .ConfigureAwait(false);

        return new ChannelSession { Channel = channel, ReadTimeout = null, Description = Describe() };
    }

    /// <inheritdoc/>
    public string Describe() => $"pipe '{_pipeName ?? PipeNames.ControlPipe}'";
}
