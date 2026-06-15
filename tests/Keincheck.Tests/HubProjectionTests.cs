using System.IO.Pipelines;
using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Projection tests for the Hub meta-tool client views, driven through a real
/// <see cref="McpClient"/> over an in-memory stream pair (the same harness shape as
/// <c>HubMetaToolTests</c>, so the SDK wiring is exercised end to end):
/// <list type="bullet">
///   <item>finding-2 Fix B: <c>hub_list_clients</c> surfaces <c>ownsWindows</c> so the AI
///         can pick the window-owning client without probing each with <c>list_windows</c>.</item>
///   <item>finding-3 hardening: the projected <c>connected</c> flag is recomputed from LIVE
///         membership (<see cref="IClientBroker.ListClients"/>), never the stored
///         <see cref="ClientInfo.IsConnected"/>, so a stale seen entry can never project as
///         connected when the live list is empty.</item>
/// </list>
/// </summary>
public sealed class HubProjectionTests
{
    private static async Task<(McpClient client, HubMcpServer hub, CancellationTokenSource cts, Task serverTask)>
        ConnectAsync(IClientBroker broker)
    {
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

        var client = await McpClient.CreateAsync(
            transport, clientOptions: null, loggerFactory: null, cancellationToken: cts.Token);

        return (client, hub, cts, serverTask);
    }

    private static async Task TeardownAsync(McpClient client, HubMcpServer hub, CancellationTokenSource cts, Task serverTask)
    {
        await client.DisposeAsync();
        cts.Cancel();
        try { await serverTask; } catch { /* cancellation */ }
        await hub.DisposeAsync();
        cts.Dispose();
    }

    private static JsonElement ViewArray(CallToolResult result)
    {
        Assert.False(result.IsError ?? false);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var root = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        return root;
    }

    // ---- finding-2 Fix B: ownsWindows in the view -------------------------

    [Fact]
    public async Task ListClients_View_Surfaces_OwnsWindows()
    {
        var broker = new StubClientBroker();
        broker.Upsert(new ClientInfo
        {
            ClientId = "ui#1", AppId = "ui", IsConnected = true, OwnsWindows = true,
        });
        broker.Upsert(new ClientInfo
        {
            ClientId = "worker#1", AppId = "worker", IsConnected = true, OwnsWindows = false,
        });

        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            var call = await client.CallToolAsync("hub_list_clients", arguments: null, cancellationToken: cts.Token);
            var view = ViewArray(call);
            var byId = view.EnumerateArray().ToDictionary(e => e.GetProperty("clientId").GetString()!);

            Assert.True(byId["ui#1"].GetProperty("ownsWindows").GetBoolean());
            Assert.False(byId["worker#1"].GetProperty("ownsWindows").GetBoolean());

            // Both are live, so both project connected.
            Assert.True(byId["ui#1"].GetProperty("connected").GetBoolean());
            Assert.True(byId["worker#1"].GetProperty("connected").GetBoolean());
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    // ---- finding-3 hardening: connected recomputed from live membership ----

    [Fact]
    public async Task KnownClients_View_Projects_Connected_False_When_Live_List_Empty()
    {
        // A broker whose KNOWN list carries a stale entry flagged IsConnected=true, but whose
        // LIVE list is empty. The hardened ToView must report connected=false regardless of the
        // stored flag, so the projection can never drift from reality.
        var broker = new DivergentBroker(
            live: Array.Empty<ClientInfo>(),
            known: new[]
            {
                new ClientInfo
                {
                    ClientId = "ghost#1", AppId = "ghost",
                    IsConnected = true, // deliberately stale-true
                    LastSeenUtc = DateTimeOffset.UtcNow,
                },
            });

        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            var call = await client.CallToolAsync("hub_list_known_clients", arguments: null, cancellationToken: cts.Token);
            var view = ViewArray(call);

            var entry = Assert.Single(view.EnumerateArray());
            Assert.Equal("ghost#1", entry.GetProperty("clientId").GetString());
            Assert.False(entry.GetProperty("connected").GetBoolean());
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    /// <summary>
    /// A broker whose live and known lists are supplied independently, so a test can force the
    /// drift scenario (a known entry whose stored IsConnected=true is NOT in the live set).
    /// Only the read members the projection touches are implemented.
    /// </summary>
    private sealed class DivergentBroker : IClientBroker
    {
        private readonly IReadOnlyList<ClientInfo> _live;
        private readonly IReadOnlyList<ClientInfo> _known;

        public DivergentBroker(IReadOnlyList<ClientInfo> live, IReadOnlyList<ClientInfo> known)
        {
            _live = live;
            _known = known;
        }

        public IReadOnlyList<ClientInfo> ListClients() => _live;
        public IReadOnlyList<ClientInfo> ListKnownClients() => _known;
        public ClientInfo? ClientStatus(string clientId)
            => _known.FirstOrDefault(c => c.ClientId == clientId);

        public string? ActiveClientId { get; set; }

        public Task<int> LaunchClientAsync(string clientId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<int> RestartClientAsync(string clientId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ClientInfo?> WaitForClientAsync(
            string? appIdOrClientId, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult<ClientInfo?>(null);
        public Task<ToolResultMessage> InvokeOnClientAsync(
            string clientId, string toolName, JsonElement? argumentsJson, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public event EventHandler<ClientInfo>? ClientConnected { add { } remove { } }
        public event EventHandler<ClientInfo>? ClientUpdated { add { } remove { } }
        public event EventHandler<ClientInfo>? ClientDown { add { } remove { } }
    }
}
