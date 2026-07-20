using System.IO.Pipelines;
using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Behavioural tests for the hub's <b>static tooling mode</b>
/// (<see cref="HubOptions.DynamicTooling"/> = false): the MCP tool list is fixed to the
/// meta-tools, the <c>listChanged</c> capability is not advertised, no
/// <c>tools/list_changed</c> notifications are emitted, and client tools stay reachable
/// through the <c>hub_call_tool</c> generic proxy after discovery via
/// <c>hub_list_client_tools</c>. Same in-memory duplex-stream harness as
/// <see cref="HubMetaToolTests"/>.
/// </summary>
public sealed class HubStaticToolingTests
{
    private static StubClientBroker BrokerWithClient(string id = "app1")
    {
        var broker = new StubClientBroker();
        broker.Upsert(new ClientInfo
        {
            ClientId = id,
            AppId = id,
            DisplayName = "Demo App",
            ProcessId = 4321,
            IsConnected = true,
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

    private static async Task<(McpClient client, HubMcpServer hub, CancellationTokenSource cts, Task serverTask)>
        ConnectAsync(IClientBroker broker, bool dynamicTooling)
    {
        var options = new HubOptions { ServeMcpOverPipe = false, HttpPort = 0, DynamicTooling = dynamicTooling };
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
    public async Task Static_Mode_Lists_Only_MetaTools_And_No_ListChanged_Capability()
    {
        var broker = BrokerWithClient();
        broker.ActiveClientId = "app1";
        var (client, hub, cts, serverTask) = await ConnectAsync(broker, dynamicTooling: false);
        try
        {
            // The listChanged capability must be off so non-dynamic agents never wait for it.
            Assert.NotEqual(true, client.ServerCapabilities?.Tools?.ListChanged);

            var names = (await client.ListToolsAsync(cancellationToken: cts.Token))
                .Select(t => t.Name).ToHashSet();

            Assert.Contains("hub_call_tool", names);
            Assert.Contains("hub_list_client_tools", names);
            Assert.Contains("hub_select_client", names);
            Assert.DoesNotContain("get_logical_tree", names); // client tools are NOT advertised
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task Static_Mode_Select_Emits_No_ListChanged_Notification()
    {
        var broker = BrokerWithClient();
        var (client, hub, cts, serverTask) = await ConnectAsync(broker, dynamicTooling: false);
        try
        {
            var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (client.RegisterNotificationHandler(
                NotificationMethods.ToolListChangedNotification, (_, _) =>
                {
                    got.TrySetResult();
                    return default;
                }))
            {
                var select = await client.CallToolAsync("hub_select_client",
                    new Dictionary<string, object?> { ["clientId"] = "app1" }!, cancellationToken: cts.Token);
                Assert.False(select.IsError ?? false);

                // Give any (wrongly) emitted notification a chance to arrive.
                var completed = await Task.WhenAny(got.Task, Task.Delay(1000, cts.Token));
                Assert.NotSame(got.Task, completed);
            }
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task Static_Mode_CallTool_Proxies_To_Active_Client()
    {
        var broker = BrokerWithClient();
        broker.ActiveClientId = "app1";
        string? invokedClient = null;
        string? invokedTool = null;
        string? forwardedArgs = null;
        broker.InvokeHandler = (clientId, tool, args, ct) =>
        {
            invokedClient = clientId;
            invokedTool = tool;
            forwardedArgs = args?.GetRawText();
            return Task.FromResult(EchoResult(clientId, tool));
        };

        var (client, hub, cts, serverTask) = await ConnectAsync(broker, dynamicTooling: false);
        try
        {
            var call = await client.CallToolAsync("hub_call_tool",
                new Dictionary<string, object?>
                {
                    ["tool"] = "get_logical_tree",
                    ["args"] = new Dictionary<string, object?> { ["maxDepth"] = 2 },
                }!, cancellationToken: cts.Token);

            Assert.False(call.IsError ?? false);
            Assert.Equal("app1", invokedClient);
            Assert.Equal("get_logical_tree", invokedTool);
            Assert.NotNull(forwardedArgs);
            Assert.Contains("maxDepth", forwardedArgs!);

            var text = Assert.IsType<TextContentBlock>(Assert.Single(call.Content)).Text;
            Assert.Contains("ran get_logical_tree on app1", text);
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task Static_Mode_CallTool_Honors_Client_Override()
    {
        var broker = BrokerWithClient();
        broker.Upsert(new ClientInfo { ClientId = "app2", IsConnected = true });
        broker.ActiveClientId = "app1";

        string? invokedClient = null;
        string? forwardedArgs = null;
        broker.InvokeHandler = (clientId, tool, args, ct) =>
        {
            invokedClient = clientId;
            forwardedArgs = args?.GetRawText();
            return Task.FromResult(EchoResult(clientId, tool));
        };

        var (client, hub, cts, serverTask) = await ConnectAsync(broker, dynamicTooling: false);
        try
        {
            var call = await client.CallToolAsync("hub_call_tool",
                new Dictionary<string, object?>
                {
                    ["tool"] = "get_logical_tree",
                    ["args"] = new Dictionary<string, object?> { ["maxDepth"] = 3 },
                    ["client"] = "app2",
                }!, cancellationToken: cts.Token);

            Assert.False(call.IsError ?? false);
            Assert.Equal("app2", invokedClient);                 // override won
            Assert.NotNull(forwardedArgs);
            Assert.DoesNotContain("\"client\"", forwardedArgs!); // override stripped
            Assert.Contains("maxDepth", forwardedArgs!);
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task CallTool_Without_Tool_Arg_Returns_Error()
    {
        var broker = BrokerWithClient();
        broker.ActiveClientId = "app1";
        var (client, hub, cts, serverTask) = await ConnectAsync(broker, dynamicTooling: false);
        try
        {
            var call = await client.CallToolAsync("hub_call_tool",
                new Dictionary<string, object?> { ["args"] = new { } }!, cancellationToken: cts.Token);

            Assert.True(call.IsError ?? false);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(call.Content)).Text;
            Assert.Contains("'tool'", text);
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task ListClientTools_Returns_Descriptors_Without_ListChanged()
    {
        var broker = BrokerWithClient();
        broker.ActiveClientId = "app1";
        var (client, hub, cts, serverTask) = await ConnectAsync(broker, dynamicTooling: false);
        try
        {
            // Omitting clientId defaults to the active client.
            var call = await client.CallToolAsync("hub_list_client_tools", arguments: null, cancellationToken: cts.Token);

            Assert.False(call.IsError ?? false);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(call.Content)).Text;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal("app1", root.GetProperty("clientId").GetString());

            var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
            Assert.Equal("get_logical_tree", tool.GetProperty("name").GetString());
            Assert.Equal("Dump the logical tree.", tool.GetProperty("description").GetString());
            Assert.True(tool.GetProperty("inputSchema").TryGetProperty("properties", out _));
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    [Fact]
    public async Task Dynamic_Mode_Default_Unchanged()
    {
        // The default stays dynamic: client tools are listed directly, listChanged is
        // advertised, and hub_call_tool is available there too.
        var broker = BrokerWithClient();
        broker.ActiveClientId = "app1";
        var (client, hub, cts, serverTask) = await ConnectAsync(broker, dynamicTooling: true);
        try
        {
            Assert.Equal(true, client.ServerCapabilities?.Tools?.ListChanged);

            var names = (await client.ListToolsAsync(cancellationToken: cts.Token))
                .Select(t => t.Name).ToHashSet();
            Assert.Contains("get_logical_tree", names);
            Assert.Contains("hub_call_tool", names);
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
