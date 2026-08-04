using System.IO.Pipelines;
using Keincheck.Hub;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Regression guard for the unbounded <c>_servers</c> growth that ballooned the Hub to
/// ~23 GB: <c>HandleListToolsAsync</c> used to <c>_servers.TryAdd(request.Server, 0)</c> on
/// every <c>tools/list</c>, but <c>request.Server</c> is a fresh per-request wrapper, so the
/// set grew by one entry per request forever (a feedback loop with list_changed). Session
/// tracking now happens once per session at the transport boundary, so the set stays bounded
/// to the number of <b>live</b> sessions and drains on disconnect.
/// </summary>
public sealed class HubSessionRegistryTests
{
    [Fact]
    public async Task Repeated_ToolsList_Registers_Session_Once_And_Clears_On_Disconnect()
    {
        var broker = new StubClientBroker();
        var options = new HubOptions { ServeMcpOverPipe = false, HttpPort = 0 };
        var hub = HubMcpServer.Start(broker, options);
        var listener = new HubPipeMcpListener(hub, options);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var serverTask = listener.RunMcpOverStreamAsync(
            input: clientToServer.Reader.AsStream(),
            output: serverToClient.Writer.AsStream(),
            ct: cts.Token);

        var transport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: null);

        var client = await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: null, cancellationToken: cts.Token);

        // Many tools/list calls on ONE session. Before the fix this added one entry each
        // (the per-request wrapper); now the single session is tracked exactly once.
        for (var i = 0; i < 10; i++)
            await client.ListToolsAsync(cancellationToken: cts.Token);

        Assert.Equal(1, hub.ConnectedSessionCount);

        // Disconnect: the session's stable server must be unregistered on teardown.
        await client.DisposeAsync();
        cts.Cancel();
        try { await serverTask; } catch { /* cancellation */ }

        Assert.Equal(0, hub.ConnectedSessionCount);

        await hub.DisposeAsync();
        cts.Dispose();
    }
}
