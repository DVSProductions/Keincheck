using Keincheck.Client;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// The browser-facing connector's construction-time guards.
///
/// The connect path itself needs a live hub and is covered by the endpoint's own tests; what is
/// worth pinning here is what the constructor <i>refuses</i>, because those are the mistakes
/// that would otherwise ship quietly and send an app's screenshots over the wire in the clear.
/// </summary>
public sealed class WebSocketChannelConnectorTests
{
    private static WebSocketChannelConnector Connector(string uri, string? token = "t")
        => new(new Uri(uri), token);

    [Theory]
    [InlineData("ws://127.0.0.1:3100/ws")]
    [InlineData("ws://localhost:3100/ws")]
    [InlineData("ws://[::1]:3100/ws")]
    public void Plaintext_Is_Accepted_On_Loopback(string uri)
    {
        // Loopback never leaves the machine, so it needs no transport encryption -- this is the
        // ordinary case: a dev-server page talking to the hub on the same box.
        var connector = Connector(uri);

        Assert.Contains("websocket", connector.Describe());
    }

    [Theory]
    [InlineData("ws://192.168.1.50:3100/ws")]
    [InlineData("ws://hub.example.com/ws")]
    public void Plaintext_Is_Refused_Off_Loopback(string uri)
    {
        // There is no mutual TLS on this transport, so ws:// to a real host would carry the UI
        // tree, screenshots and the token itself in plaintext -- and silently.
        var ex = Assert.Throws<ArgumentException>(() => Connector(uri));

        Assert.Contains("wss://", ex.Message);
    }

    [Theory]
    [InlineData("wss://hub.example.com/ws")]
    [InlineData("wss://192.168.1.50:3100/ws")]
    public void Tls_Is_Accepted_Anywhere(string uri)
    {
        var connector = Connector(uri);

        Assert.Contains("wss", connector.Describe());
    }

    [Theory]
    [InlineData("http://localhost:3100/ws")]
    [InlineData("https://localhost:3100/ws")]
    [InlineData("file:///tmp/ws")]
    public void A_Non_WebSocket_Scheme_Is_Refused(string uri)
    {
        Assert.Throws<ArgumentException>(() => Connector(uri));
    }

    [Fact]
    public void Describe_Never_Leaks_The_Token()
    {
        // Describe() goes into logs and into every connect-failure message.
        const string secret = "super-secret-token-value";
        var connector = Connector("ws://127.0.0.1:3100/ws", secret);

        Assert.DoesNotContain(secret, connector.Describe());
    }
}
