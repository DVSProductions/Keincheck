using System.IO.Pipelines;
using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Behavioural tests for the Phase-B Hub MCP surface added on top of the proven
/// Mechanic #2/#3 spike: the full meta-tool set, the per-call <c>client</c> override
/// on proxied tools, the structured "client is down" error pointing at
/// <c>hub_restart_client</c>, and the logging notification pushed on a client drop.
/// Each test drives a real <see cref="McpClient"/> against the hub over an in-memory
/// duplex stream pair (the named-pipe substitute), so the SDK wiring is exercised end
/// to end — no UI/Avalonia needed (the stub broker stands in for live clients).
/// </summary>
public sealed class HubMetaToolTests
{
    private static StubClientBroker BrokerWithClient(string id = "app1", bool connected = true)
    {
        var broker = new StubClientBroker();
        broker.Upsert(new ClientInfo
        {
            ClientId = id,
            AppId = id,
            DisplayName = "Demo App",
            ProcessId = 4321,
            IsConnected = connected,
            Tools = new[]
            {
                new ToolDescriptor
                {
                    Name = "get_logical_tree",
                    Description = "Dump the logical tree.",
                    InputSchema = JsonDocument.Parse(
                        """{"type":"object","properties":{"maxDepth":{"type":"integer"}}}""").RootElement.Clone(),
                },
            },
        });
        return broker;
    }

    /// <summary>Boots the hub over an in-memory stream and returns a connected MCP client.</summary>
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

    [Fact]
    public async Task Catalog_Advertises_All_Six_MetaTools()
    {
        var broker = BrokerWithClient();
        broker.ActiveClientId = "app1";
        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            var names = (await client.ListToolsAsync(cancellationToken: cts.Token))
                .Select(t => t.Name).ToHashSet();

            Assert.Contains("hub_list_clients", names);
            Assert.Contains("hub_list_known_clients", names);
            Assert.Contains("hub_launch_client", names);
            Assert.Contains("hub_restart_client", names);
            Assert.Contains("hub_select_client", names);
            Assert.Contains("hub_client_status", names);
            Assert.Contains("get_logical_tree", names); // proxied from active client
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task SelectClient_Then_Proxied_Call_Routes_To_Active()
    {
        var broker = BrokerWithClient();
        string? invokedClient = null;
        broker.InvokeHandler = (clientId, tool, args, ct) =>
        {
            invokedClient = clientId;
            return Task.FromResult(EchoResult(clientId, tool));
        };
        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            // No active client yet: the meta-tool selects one and the catalog grows.
            var select = await client.CallToolAsync("hub_select_client",
                new Dictionary<string, object?> { ["clientId"] = "app1" }!, cancellationToken: cts.Token);
            Assert.False(select.IsError ?? false);

            var call = await client.CallToolAsync("get_logical_tree",
                new Dictionary<string, object?> { ["maxDepth"] = 2 }!, cancellationToken: cts.Token);
            Assert.False(call.IsError ?? false);
            Assert.Equal("app1", invokedClient);
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task Proxied_Call_Honors_Client_Override_And_Strips_It()
    {
        var broker = BrokerWithClient();
        // Add a SECOND connected client and make the FIRST active.
        broker.Upsert(new ClientInfo
        {
            ClientId = "app2",
            IsConnected = true,
            Tools = new[]
            {
                new ToolDescriptor { Name = "get_logical_tree", InputSchema =
                    JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone() },
            },
        });
        broker.ActiveClientId = "app1";

        string? invokedClient = null;
        string? forwardedArgs = null;
        broker.InvokeHandler = (clientId, tool, args, ct) =>
        {
            invokedClient = clientId;
            forwardedArgs = args?.GetRawText();
            return Task.FromResult(EchoResult(clientId, tool));
        };

        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            // active is app1, but the call names app2 via the 'client' override arg.
            var call = await client.CallToolAsync("get_logical_tree",
                new Dictionary<string, object?> { ["client"] = "app2", ["maxDepth"] = 3 }!,
                cancellationToken: cts.Token);

            Assert.False(call.IsError ?? false);
            Assert.Equal("app2", invokedClient);                 // override won
            Assert.NotNull(forwardedArgs);
            Assert.DoesNotContain("\"client\"", forwardedArgs!); // override stripped from forwarded args
            Assert.Contains("maxDepth", forwardedArgs!);         // real args preserved
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task Call_To_Down_Client_Returns_Structured_Recovery_Error()
    {
        var broker = BrokerWithClient();
        broker.ActiveClientId = "app1";
        // Now mark it down AFTER selecting — the catalog still lists it from list_known.
        broker.MarkDown("app1");

        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            var call = await client.CallToolAsync("get_logical_tree",
                new Dictionary<string, object?> { ["maxDepth"] = 1 }!, cancellationToken: cts.Token);

            Assert.True(call.IsError ?? false);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(call.Content)).Text;
            Assert.Contains("hub_restart_client", text);
            Assert.Contains("app1", text);

            // Structured content carries a machine-readable recovery pointer.
            Assert.NotNull(call.StructuredContent);
            var sc = call.StructuredContent!.Value;
            Assert.Equal("client_unavailable", sc.GetProperty("error").GetString());
            Assert.Equal("hub_restart_client", sc.GetProperty("recovery").GetProperty("tool").GetString());
            Assert.Equal("app1",
                sc.GetProperty("recovery").GetProperty("arguments").GetProperty("clientId").GetString());
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task ClientStatus_For_Unknown_Id_Returns_Structured_Error()
    {
        var broker = BrokerWithClient();
        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            var call = await client.CallToolAsync("hub_client_status",
                new Dictionary<string, object?> { ["clientId"] = "ghost" }!, cancellationToken: cts.Token);

            Assert.True(call.IsError ?? false);
            Assert.NotNull(call.StructuredContent);
            Assert.Equal("ghost", call.StructuredContent!.Value.GetProperty("clientId").GetString());
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task ClientDown_Pushes_Logging_Notification_Naming_Restart()
    {
        var broker = BrokerWithClient();
        broker.ActiveClientId = "app1";
        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            // Force at least one tools/list so the hub registers this session's server.
            await client.ListToolsAsync(cancellationToken: cts.Token);

            var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (client.RegisterNotificationHandler(
                NotificationMethods.LoggingMessageNotification, (notification, ct) =>
                {
                    try
                    {
                        var p = JsonSerializer.Deserialize<LoggingMessageNotificationParams>(
                            notification.Params, ProtocolJson.Options);
                        if (p is not null)
                            got.TrySetResult(p.Data.GetRawText());
                    }
                    catch { /* ignore malformed */ }
                    return default;
                }))
            {
                // Drop the client: the hub should push notifications/message.
                broker.MarkDown("app1");

                var payload = await got.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
                Assert.Contains("client_down", payload);
                Assert.Contains("hub_restart_client", payload);
                Assert.Contains("app1", payload);
            }
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    private static ToolResultMessage EchoResult(string clientId, string toolName) => new()
    {
        ClientId = clientId,
        ToolName = toolName,
        IsError = false,
        Content = JsonSerializer.SerializeToElement(
            new object[] { new { type = "text", text = $"ran {toolName} on {clientId}" } },
            ProtocolJson.Options),
    };
}
