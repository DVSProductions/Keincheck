using Xunit;
using Xunit.Abstractions;

namespace Keincheck.E2E;

/// <summary>
/// The hub serves MCP over three transports. The stdio shim is the one Claude uses and
/// the main scenario drives it exhaustively; these two facts prove the other two work at
/// all, which nothing else does.
/// </summary>
[Collection(HubCollection.Name)]
public sealed class TransportSmokeTests(HubRig rig, ITestOutputHelper output)
{
    /// <summary>
    /// A bisect, not a duplicate: when the shim path fails this says immediately whether
    /// the hub's MCP endpoint is broken or the shim is.
    /// </summary>
    [E2EFact]
    public async Task Mcp_Over_The_Named_Pipe_Answers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var (client, channel) = await rig.ConnectViaPipeAsync(cts.Token);
        try
        {
            Assert.Equal("Keincheck.Hub", client.ServerInfo.Name);

            var names = (await client.ListToolsAsync(cancellationToken: cts.Token)).Select(t => t.Name).ToArray();
            Assert.Contains("hub_status", names);
            output.WriteLine($"pipe '{HubRig.McpPipeName}': {names.Length} tools");
        }
        finally
        {
            await client.DisposeAsync();
            await channel.DisposeAsync();
        }
    }

    /// <summary>
    /// The only coverage <c>HubMcpServer.StartHttp</c> has anywhere. Kestrel binding
    /// loopback:3100 and <c>MapMcp</c> wiring up are otherwise entirely unasserted, so a
    /// regression there would ship silently.
    /// </summary>
    [E2EFact]
    public async Task Mcp_Over_Loopback_Http_Answers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await using var client = await rig.ConnectViaHttpAsync(cts.Token);
        Assert.Equal("Keincheck.Hub", client.ServerInfo.Name);

        var names = (await client.ListToolsAsync(cancellationToken: cts.Token)).Select(t => t.Name).ToArray();
        Assert.Contains("hub_status", names);
        output.WriteLine($"http://127.0.0.1:{HubRig.HttpPort}: {names.Length} tools");
    }
}
