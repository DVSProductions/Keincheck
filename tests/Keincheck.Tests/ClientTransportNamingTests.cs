using Keincheck.Hub;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Every <see cref="ClientTransport"/> must have a name in the client listing an AI reads.
///
/// This is not cosmetic. The model decides what it may do from that field: "pipe" means the
/// same machine, anything else does not, and <c>hub_launch_client</c> is unavailable for some
/// of them. A transport that reports "unknown" tells it nothing at all — which is exactly what
/// the WebSocket transport did after being added to the enum but not to the mapping, and it
/// went unnoticed until a real browser attached and showed up as unknown.
/// </summary>
public sealed class ClientTransportNamingTests
{
    [Theory]
    [InlineData(ClientTransport.Pipe, "pipe")]
    [InlineData(ClientTransport.Tcp, "tcp")]
    [InlineData(ClientTransport.Relay, "relay")]
    [InlineData(ClientTransport.WebSocket, "websocket")]
    public void Each_Transport_Has_Its_Own_Name(ClientTransport transport, string expected)
    {
        Assert.Equal(expected, HubMetaTools.DescribeTransport(transport));
    }

    [Fact]
    public void No_Declared_Transport_Reports_Unknown()
    {
        // Guards the next one added: an enum member with no mapping falls to "unknown", which
        // reads as "the hub does not know what this is" rather than as a missing case.
        foreach (ClientTransport transport in Enum.GetValues<ClientTransport>())
        {
            Assert.NotEqual("unknown", HubMetaTools.DescribeTransport(transport));
        }
    }
}
