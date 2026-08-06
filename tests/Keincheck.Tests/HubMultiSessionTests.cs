using System.IO.Pipelines;
using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// The multi-agent guarantee: several AI agents share one hub (it is a per-user singleton),
/// so each MCP session must carry its own selection, its own advertised tool list and its own
/// recording. Before this, all three were hub-wide singletons — one agent calling
/// <c>hub_select_client</c> silently retargeted every other agent's next tool call, and two
/// agents recording produced one interleaved, unusable trace.
/// </summary>
/// <remarks>
/// Every test drives two real <see cref="McpClient"/>s over separate in-memory duplex stream
/// pairs into <b>one</b> <see cref="HubMcpServer"/>, which is exactly the production shape:
/// two <c>keincheck-connect</c> shims dialling the same pipe of the same hub.
/// </remarks>
public sealed class HubMultiSessionTests
{
    private static StubClientBroker BrokerWithTwoClients()
    {
        var broker = new StubClientBroker();
        broker.Upsert(ClientWithTool("app1", "get_app1_tree"));
        broker.Upsert(ClientWithTool("app2", "get_app2_tree"));
        return broker;
    }

    private static ClientInfo ClientWithTool(string id, string toolName) => new()
    {
        ClientId = id,
        AppId = id,
        DisplayName = $"Demo {id}",
        IsConnected = true,
        Tools = new[]
        {
            new ToolDescriptor
            {
                Name = toolName,
                Description = $"Dump {id}.",
                ReadOnly = true,
                InputSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone(),
            },
        },
    };

    /// <summary>One hub, and a factory that attaches as many independent agents as a test wants.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly HubPipeMcpListener _listener;
        private readonly List<(McpClient client, Task serverTask, Pipe toServer)> _sessions = new();

        public Rig(IClientBroker broker, bool dynamicTooling = true)
        {
            var options = new HubOptions
            {
                ServeMcpOverPipe = false,
                HttpPort = 0,
                DynamicTooling = dynamicTooling,
            };
            Hub = HubMcpServer.Start(broker, options);
            _listener = new HubPipeMcpListener(Hub, options);
        }

        public HubMcpServer Hub { get; }

        public CancellationTokenSource Cts { get; } = new(TimeSpan.FromSeconds(30));

        /// <summary>Attaches one more agent, identifying itself as <paramref name="agentName"/>.</summary>
        public async Task<McpClient> ConnectAsync(string agentName)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            var serverTask = _listener.RunMcpOverStreamAsync(
                input: clientToServer.Reader.AsStream(),
                output: serverToClient.Writer.AsStream(),
                ct: Cts.Token);

            var transport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: null);

            var client = await McpClient.CreateAsync(
                transport,
                clientOptions: new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = agentName, Version = "1.0.0" },
                },
                loggerFactory: null,
                cancellationToken: Cts.Token);

            _sessions.Add((client, serverTask, clientToServer));
            return client;
        }

        /// <summary>
        /// Ends one agent's connection the way a real shim exiting does: close the client and
        /// complete the stream the hub reads, so the server session unwinds on EOF rather than
        /// waiting for the whole rig to be cancelled.
        /// </summary>
        public async Task DisconnectAsync(McpClient client)
        {
            var index = _sessions.FindIndex(s => s.client == client);
            Assert.True(index >= 0, "that client was not attached to this rig");
            var session = _sessions[index];
            _sessions.RemoveAt(index);

            try { await client.DisposeAsync(); } catch { /* torn down */ }
            await session.toServer.Writer.CompleteAsync();
            try { await session.serverTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* torn down */ }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (client, _, _) in _sessions)
            {
                try { await client.DisposeAsync(); } catch { /* torn down */ }
            }
            Cts.Cancel();
            foreach (var (_, serverTask, _) in _sessions)
            {
                try { await serverTask; } catch { /* cancellation */ }
            }
            await Hub.DisposeAsync();
            Cts.Dispose();
        }
    }

    /// <summary>
    /// Reads a nullable string field, tolerating the field being absent: the hub serializes
    /// with null-omitting options, so "no client selected" arrives as a missing property.
    /// </summary>
    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<JsonElement> CallJsonAsync(
        McpClient client, string tool, Dictionary<string, object?>? args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args!, cancellationToken: ct);
        Assert.False(result.IsError ?? false);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task SelectAsync(McpClient client, string clientId, CancellationToken ct) =>
        CallJsonAsync(client, "hub_select_client", new() { ["clientId"] = clientId }, ct);

    [Fact]
    public async Task Two_Agents_Select_Different_Clients_And_Route_Independently()
    {
        var broker = BrokerWithTwoClients();
        var invoked = new List<(string agentTool, string client)>();
        broker.InvokeHandler = (clientId, tool, _, _) =>
        {
            lock (invoked) invoked.Add((tool, clientId));
            return Task.FromResult(new ToolResultMessage { ClientId = clientId, ToolName = tool });
        };

        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("claude-code");
        var b = await rig.ConnectAsync("claude-code");

        await SelectAsync(a, "app1", rig.Cts.Token);
        await SelectAsync(b, "app2", rig.Cts.Token);

        // Agent A selected LAST-but-one; before per-session state, B's selection would have
        // retargeted A's call to app2 as well.
        await CallJsonAsync(a, "get_app1_tree", null, rig.Cts.Token);
        await CallJsonAsync(b, "get_app2_tree", null, rig.Cts.Token);

        Assert.Contains(("get_app1_tree", "app1"), invoked);
        Assert.Contains(("get_app2_tree", "app2"), invoked);
    }

    [Fact]
    public async Task Each_Agent_Sees_Only_Its_Own_Clients_Tools()
    {
        var broker = BrokerWithTwoClients();
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("claude-code");
        var b = await rig.ConnectAsync("kimi-code");

        await SelectAsync(a, "app1", rig.Cts.Token);
        await SelectAsync(b, "app2", rig.Cts.Token);

        var aTools = (await a.ListToolsAsync(cancellationToken: rig.Cts.Token)).Select(t => t.Name).ToHashSet();
        var bTools = (await b.ListToolsAsync(cancellationToken: rig.Cts.Token)).Select(t => t.Name).ToHashSet();

        Assert.Contains("get_app1_tree", aTools);
        Assert.DoesNotContain("get_app2_tree", aTools);

        Assert.Contains("get_app2_tree", bTools);
        Assert.DoesNotContain("get_app1_tree", bTools);
    }

    [Fact]
    public async Task HubStatus_Reports_The_Calling_Agents_Own_Selection_And_Label()
    {
        var broker = BrokerWithTwoClients();
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("claude-code");
        var b = await rig.ConnectAsync("claude-code");

        await SelectAsync(a, "app1", rig.Cts.Token);
        await SelectAsync(b, "app2", rig.Cts.Token);

        var statusA = await CallJsonAsync(a, "hub_status", null, rig.Cts.Token);
        var statusB = await CallJsonAsync(b, "hub_status", null, rig.Cts.Token);

        Assert.Equal("app1", statusA.GetProperty("activeClientId").GetString());
        Assert.Equal("app2", statusB.GetProperty("activeClientId").GetString());

        // Same reported client name, so the counter is what tells the two agents apart.
        Assert.Equal("claude-code-1", statusA.GetProperty("agent").GetString());
        Assert.Equal("claude-code-2", statusB.GetProperty("agent").GetString());
        Assert.Equal(2, statusA.GetProperty("agentSessions").GetInt32());
    }

    [Fact]
    public async Task Selecting_Notifies_Only_The_Selecting_Agent()
    {
        var broker = BrokerWithTwoClients();
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("agent-a");
        var b = await rig.ConnectAsync("agent-b");

        // Make both sessions known to the hub before watching for notifications.
        await a.ListToolsAsync(cancellationToken: rig.Cts.Token);
        await b.ListToolsAsync(cancellationToken: rig.Cts.Token);

        var aNotified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bNotified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (a.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification, (_, _) => { aNotified.TrySetResult(); return default; }))
        await using (b.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification, (_, _) => { bNotified.TrySetResult(); return default; }))
        {
            await SelectAsync(a, "app1", rig.Cts.Token);

            // A's catalog genuinely changed, so A must be told.
            var aDone = await Task.WhenAny(aNotified.Task, Task.Delay(5000, rig.Cts.Token));
            Assert.Same(aNotified.Task, aDone);

            // B's catalog did not change. Give a wrongly-broadcast notification time to land.
            var bDone = await Task.WhenAny(bNotified.Task, Task.Delay(1000, rig.Cts.Token));
            Assert.NotSame(bNotified.Task, bDone);
        }
    }

    [Fact]
    public async Task Recordings_Are_Isolated_Between_Agents()
    {
        var broker = BrokerWithTwoClients();
        broker.InvokeHandler = (clientId, tool, _, _) =>
            Task.FromResult(new ToolResultMessage { ClientId = clientId, ToolName = tool });

        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("agent-a");
        var b = await rig.ConnectAsync("agent-b");

        await SelectAsync(a, "app1", rig.Cts.Token);
        await SelectAsync(b, "app2", rig.Cts.Token);

        // A records two steps; B starts a recording of its own in the middle, which under the
        // old shared buffer would have cleared A's and then swallowed A's second call.
        await CallJsonAsync(a, "hub_record_start", new() { ["name"] = "a-flow" }, rig.Cts.Token);
        await CallJsonAsync(a, "get_app1_tree", null, rig.Cts.Token);

        await CallJsonAsync(b, "hub_record_start", new() { ["name"] = "b-flow" }, rig.Cts.Token);
        await CallJsonAsync(b, "get_app2_tree", null, rig.Cts.Token);

        await CallJsonAsync(a, "get_app1_tree", null, rig.Cts.Token);

        var aStatus = await CallJsonAsync(a, "hub_record_status", null, rig.Cts.Token);
        var bStatus = await CallJsonAsync(b, "hub_record_status", null, rig.Cts.Token);

        Assert.Equal("a-flow", aStatus.GetProperty("name").GetString());
        Assert.Equal(2, aStatus.GetProperty("steps").GetInt32());

        Assert.Equal("b-flow", bStatus.GetProperty("name").GetString());
        Assert.Equal(1, bStatus.GetProperty("steps").GetInt32());
    }

    [Fact]
    public async Task A_Fresh_Agent_Starts_From_The_Hub_Default_Selection()
    {
        var broker = BrokerWithTwoClients();
        broker.DefaultClientId = "app2";

        await using var rig = new Rig(broker);
        var late = await rig.ConnectAsync("claude-code");

        // The single-agent experience: connect and the client is already selected.
        var status = await CallJsonAsync(late, "hub_status", null, rig.Cts.Token);
        Assert.Equal("app2", status.GetProperty("activeClientId").GetString());

        var tools = (await late.ListToolsAsync(cancellationToken: rig.Cts.Token)).Select(t => t.Name).ToHashSet();
        Assert.Contains("get_app2_tree", tools);
    }

    [Fact]
    public async Task A_Client_Dropping_Only_Clears_The_Agents_That_Were_Driving_It()
    {
        var broker = BrokerWithTwoClients();
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("agent-a");
        var b = await rig.ConnectAsync("agent-b");

        await SelectAsync(a, "app1", rig.Cts.Token);
        await SelectAsync(b, "app2", rig.Cts.Token);

        broker.MarkDown("app1");

        var statusA = await CallJsonAsync(a, "hub_status", null, rig.Cts.Token);
        var statusB = await CallJsonAsync(b, "hub_status", null, rig.Cts.Token);

        Assert.Null(OptionalString(statusA, "activeClientId"));
        Assert.Equal("app2", OptionalString(statusB, "activeClientId"));
    }

    [Fact]
    public async Task Ending_A_Session_Discards_Its_Recording_And_Unregisters_It()
    {
        var broker = BrokerWithTwoClients();
        broker.InvokeHandler = (clientId, tool, _, _) =>
            Task.FromResult(new ToolResultMessage { ClientId = clientId, ToolName = tool });

        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("agent-a");
        var b = await rig.ConnectAsync("agent-b");

        await SelectAsync(a, "app1", rig.Cts.Token);
        await CallJsonAsync(a, "hub_record_start", new() { ["name"] = "a-flow" }, rig.Cts.Token);
        await CallJsonAsync(a, "get_app1_tree", null, rig.Cts.Token);

        Assert.Equal(2, rig.Hub.ConnectedSessionCount);

        await rig.DisconnectAsync(a);

        // The hub drops the session — and with it A's selection and its recording buffer.
        Assert.Equal(1, rig.Hub.ConnectedSessionCount);

        var bStatus = await CallJsonAsync(b, "hub_record_status", null, rig.Cts.Token);
        Assert.False(bStatus.GetProperty("recording").GetBoolean());
        Assert.Equal(0, bStatus.GetProperty("steps").GetInt32());
    }
}
