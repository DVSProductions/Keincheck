using System.Net;

namespace Keincheck.Remote;

/// <summary>Where a remote client dials: a host (name or literal address) and a port.</summary>
/// <remarks>
/// Frequently <c>127.0.0.1</c> — that is the SSH-tunnel shape, where the client dials a local
/// forwarded port and SSH carries it to the hub. The transport does not care which shape it
/// is: the TLS certificates establish identity either way, so the tunnelled and direct cases
/// are the same code path rather than two phases of a rollout.
/// </remarks>
public sealed record RemoteEndpoint
{
    /// <summary>The default port a hub listens on when none is specified.</summary>
    public const int DefaultPort = 7423;

    /// <summary>The host name or literal IP to dial.</summary>
    public required string Host { get; init; }

    /// <summary>The TCP port to dial.</summary>
    public required int Port { get; init; }

    /// <summary>Parses <c>host</c>, <c>host:port</c>, or <c>[v6]:port</c>.</summary>
    /// <exception cref="FormatException">The text is not a usable endpoint.</exception>
    public static RemoteEndpoint Parse(string text)
    {
        if (!TryParse(text, out var endpoint))
            throw new FormatException($"'{text}' is not a valid host[:port].");
        return endpoint!;
    }

    /// <inheritdoc cref="Parse"/>
    public static bool TryParse(string? text, out RemoteEndpoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        string host;
        var port = DefaultPort;

        if (text.StartsWith('['))
        {
            // Bracketed IPv6: [::1]:7423
            var close = text.IndexOf(']');
            if (close < 0)
                return false;
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length > 0 && (rest[0] != ':' || !int.TryParse(rest[1..], out port)))
                return false;
        }
        else
        {
            var colon = text.LastIndexOf(':');
            // A bare IPv6 literal has several colons and no port; only treat the last colon
            // as a separator when there is exactly one.
            if (colon >= 0 && text.IndexOf(':') == colon)
            {
                // colon == 0 is a bare ":port" with no host at all. It must FAIL rather than
                // fall through to the else branch, which would take the whole string as a host
                // name — ":9000" became Host=":9000", Port=7423 and reported success, and that
                // value is baked into every credential issued while it is configured.
                host = text[..colon];
                if (!int.TryParse(text[(colon + 1)..], out port))
                    return false;
            }
            else
            {
                host = text;
            }
        }

        if (host.Length == 0 || port is <= 0 or > 65535)
            return false;

        endpoint = new RemoteEndpoint { Host = host, Port = port };
        return true;
    }

    /// <summary>True when this endpoint names the loopback interface.</summary>
    public bool IsLoopback =>
        IPAddress.TryParse(Host, out var ip)
            ? IPAddress.IsLoopback(ip)
            : Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
