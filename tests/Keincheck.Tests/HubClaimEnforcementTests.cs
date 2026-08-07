using System.IO.Pipelines;
using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// The one-driver-per-app rule as an agent actually experiences it: two real MCP sessions
/// against one hub, both pointed at the same app instance. Reading stays open to both;
/// driving belongs to whoever claimed it, and the refusal has to tell the loser what to do
/// instead.
/// </summary>
public sealed class HubClaimEnforcementTests
{
    private const string ReadTool = "get_logical_tree";
    private const string WriteTool = "click_at";

    private static StubClientBroker BrokerWith(params string[] clientIds)
    {
        var broker = new StubClientBroker();
        foreach (var id in clientIds)
        {
            broker.Upsert(new ClientInfo
            {
                ClientId = id,
                // Same app id on purpose: these are sibling instances, e.g. one build per
                // git worktree, which is what makes "use a free sibling" useful advice.
                AppId = "myapp",
                DisplayName = "Demo App",
                IsConnected = true,
                Tools = new[]
                {
                    new ToolDescriptor { Name = ReadTool, ReadOnly = true, InputSchema = EmptySchema() },
                    new ToolDescriptor { Name = WriteTool, ReadOnly = false, InputSchema = EmptySchema() },
                },
            });
        }

        broker.InvokeHandler = (clientId, tool, _, _) =>
            Task.FromResult(new ToolResultMessage { ClientId = clientId, ToolName = tool });
        return broker;
    }

    private static JsonElement EmptySchema() =>
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    private sealed class Rig : IAsyncDisposable
    {
        private readonly HubPipeMcpListener _listener;
        private readonly List<(McpClient client, Task serverTask, Pipe toServer)> _sessions = new();

        public Rig(IClientBroker broker, HubOptions? options = null, ClientClaimRegistry? claims = null)
        {
            options ??= new HubOptions { ServeMcpOverPipe = false, HttpPort = 0 };
            Hub = HubMcpServer.Start(broker, options, claims);
            _listener = new HubPipeMcpListener(Hub, options);
        }

        public HubMcpServer Hub { get; }
        public CancellationTokenSource Cts { get; } = new(TimeSpan.FromSeconds(30));

        public async Task<McpClient> ConnectAsync(string agentName)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var serverTask = _listener.RunMcpOverStreamAsync(
                toServer.Reader.AsStream(), toClient.Writer.AsStream(), Cts.Token);

            var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), null),
                new McpClientOptions { ClientInfo = new Implementation { Name = agentName, Version = "1.0.0" } },
                loggerFactory: null,
                cancellationToken: Cts.Token);

            _sessions.Add((client, serverTask, toServer));
            return client;
        }

        /// <summary>Ends one agent's connection the way an exiting shim does (close, then EOF).</summary>
        public async Task DisconnectAsync(McpClient client)
        {
            var index = _sessions.FindIndex(s => s.client == client);
            Assert.True(index >= 0, "that client was not attached to this rig");
            var session = _sessions[index];
            _sessions.RemoveAt(index);

            try { await client.DisposeAsync(); } catch { }
            await session.toServer.Writer.CompleteAsync();
            try { await session.serverTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (client, _, _) in _sessions)
            {
                try { await client.DisposeAsync(); } catch { }
            }
            Cts.Cancel();
            foreach (var (_, task, _) in _sessions)
            {
                try { await task; } catch { }
            }
            await Hub.DisposeAsync();
            Cts.Dispose();
        }
    }

    private static async Task<CallToolResult> CallAsync(
        McpClient client, string tool, Dictionary<string, object?>? args, CancellationToken ct)
        => await client.CallToolAsync(tool, args!, cancellationToken: ct);

    private static async Task<JsonElement> OkJsonAsync(
        McpClient client, string tool, Dictionary<string, object?>? args, CancellationToken ct)
    {
        var result = await CallAsync(client, tool, args, ct);
        Assert.False(result.IsError ?? false);
        return JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text).RootElement.Clone();
    }

    private static Task SelectAsync(McpClient client, string clientId, CancellationToken ct) =>
        OkJsonAsync(client, "hub_select_client", new() { ["clientId"] = clientId }, ct);

    [Fact]
    public async Task A_Second_Agents_Write_Is_Refused_With_A_Usable_Recovery()
    {
        var broker = BrokerWith("myapp#1", "myapp#2");
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("claude-code");
        var b = await rig.ConnectAsync("claude-code");

        await SelectAsync(a, "myapp#1", rig.Cts.Token);
        await SelectAsync(b, "myapp#1", rig.Cts.Token);

        // A drives first, so A owns myapp#1.
        Assert.False((await CallAsync(a, WriteTool, null, rig.Cts.Token)).IsError ?? false);

        var refused = await CallAsync(b, WriteTool, null, rig.Cts.Token);
        Assert.True(refused.IsError);

        var structured = refused.StructuredContent!.Value;
        Assert.Equal("client_claimed", structured.GetProperty("error").GetString());
        Assert.Equal("myapp#1", structured.GetProperty("clientId").GetString());
        Assert.Equal("claude-code-1", structured.GetProperty("owner").GetProperty("session").GetString());

        // The genuinely useful part: there is a free sibling instance to use instead.
        var free = structured.GetProperty("unclaimedInstances").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("myapp#2", free);

        var recovery = structured.GetProperty("recovery").EnumerateArray()
            .Select(e => e.GetProperty("tool").GetString()).ToList();
        Assert.Contains("hub_launch_client", recovery);
        Assert.Contains("hub_claim_client", recovery);
    }

    [Fact]
    public async Task Reading_A_Claimed_App_Is_Always_Allowed()
    {
        var broker = BrokerWith("myapp#1");
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("agent-a");
        var b = await rig.ConnectAsync("agent-b");

        await SelectAsync(a, "myapp#1", rig.Cts.Token);
        await SelectAsync(b, "myapp#1", rig.Cts.Token);

        await CallAsync(a, WriteTool, null, rig.Cts.Token); // A claims it

        Assert.False((await CallAsync(b, ReadTool, null, rig.Cts.Token)).IsError ?? false);
    }

    [Fact]
    public async Task Each_Agent_Drives_Its_Own_Instance_Without_Contending()
    {
        var broker = BrokerWith("myapp#1", "myapp#2", "myapp#3");
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("claude-code");
        var b = await rig.ConnectAsync("claude-code");
        var c = await rig.ConnectAsync("claude-code");

        await SelectAsync(a, "myapp#1", rig.Cts.Token);
        await SelectAsync(b, "myapp#2", rig.Cts.Token);
        await SelectAsync(c, "myapp#3", rig.Cts.Token);

        // Three worktree builds of one app, three agents, no contention at all.
        Assert.False((await CallAsync(a, WriteTool, null, rig.Cts.Token)).IsError ?? false);
        Assert.False((await CallAsync(b, WriteTool, null, rig.Cts.Token)).IsError ?? false);
        Assert.False((await CallAsync(c, WriteTool, null, rig.Cts.Token)).IsError ?? false);

        // ...but each is still fenced off from the others.
        Assert.True((await CallAsync(a, WriteTool, new() { ["client"] = "myapp#2" }, rig.Cts.Token)).IsError);
    }

    [Fact]
    public async Task Releasing_Hands_The_App_To_The_Waiting_Agent()
    {
        var broker = BrokerWith("myapp#1");
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("agent-a");
        var b = await rig.ConnectAsync("agent-b");

        await SelectAsync(a, "myapp#1", rig.Cts.Token);
        await SelectAsync(b, "myapp#1", rig.Cts.Token);

        await CallAsync(a, WriteTool, null, rig.Cts.Token);
        Assert.True((await CallAsync(b, WriteTool, null, rig.Cts.Token)).IsError);

        await OkJsonAsync(a, "hub_release_client", new() { ["clientId"] = "myapp#1" }, rig.Cts.Token);

        Assert.False((await CallAsync(b, WriteTool, null, rig.Cts.Token)).IsError ?? false);
    }

    [Fact]
    public async Task Force_Claim_Takes_Over_From_A_Stuck_Agent()
    {
        var broker = BrokerWith("myapp#1");
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("agent-a");
        var b = await rig.ConnectAsync("agent-b");

        await SelectAsync(a, "myapp#1", rig.Cts.Token);
        await SelectAsync(b, "myapp#1", rig.Cts.Token);
        await CallAsync(a, WriteTool, null, rig.Cts.Token);

        // Without force it is refused; with force B takes over.
        Assert.True((await CallAsync(b, "hub_claim_client",
            new() { ["clientId"] = "myapp#1" }, rig.Cts.Token)).IsError);

        await OkJsonAsync(b, "hub_claim_client",
            new() { ["clientId"] = "myapp#1", ["force"] = true }, rig.Cts.Token);

        Assert.False((await CallAsync(b, WriteTool, null, rig.Cts.Token)).IsError ?? false);
        Assert.True((await CallAsync(a, WriteTool, null, rig.Cts.Token)).IsError);
    }

    [Fact]
    public async Task A_Disconnecting_Agent_Frees_What_It_Was_Driving()
    {
        var broker = BrokerWith("myapp#1");
        var claims = new ClientClaimRegistry(TimeSpan.FromMinutes(15));
        await using var rig = new Rig(broker, claims: claims);
        var a = await rig.ConnectAsync("agent-a");
        var b = await rig.ConnectAsync("agent-b");

        await SelectAsync(a, "myapp#1", rig.Cts.Token);
        await SelectAsync(b, "myapp#1", rig.Cts.Token);
        await CallAsync(a, WriteTool, null, rig.Cts.Token);

        Assert.Equal("agent-a-1", claims.Get("myapp#1")?.SessionLabel);
        Assert.True((await CallAsync(b, WriteTool, null, rig.Cts.Token)).IsError);

        // An agent that goes away must not hold the app hostage until the idle timeout.
        await rig.DisconnectAsync(a);

        Assert.Null(claims.Get("myapp#1"));
        Assert.False((await CallAsync(b, WriteTool, null, rig.Cts.Token)).IsError ?? false);
    }

    [Fact]
    public async Task Claims_Can_Be_Turned_Off_Entirely()
    {
        var broker = BrokerWith("myapp#1");
        var options = new HubOptions
        {
            ServeMcpOverPipe = false,
            HttpPort = 0,
            EnforceWriteClaims = false,
        };
        await using var rig = new Rig(broker, options);
        var a = await rig.ConnectAsync("agent-a");
        var b = await rig.ConnectAsync("agent-b");

        await SelectAsync(a, "myapp#1", rig.Cts.Token);
        await SelectAsync(b, "myapp#1", rig.Cts.Token);

        // The rollback lever: back to the old free-for-all.
        Assert.False((await CallAsync(a, WriteTool, null, rig.Cts.Token)).IsError ?? false);
        Assert.False((await CallAsync(b, WriteTool, null, rig.Cts.Token)).IsError ?? false);
    }

    [Fact]
    public async Task Read_Only_Clients_Are_Never_Claimed()
    {
        // A read-only client refuses mutating tools anyway; claiming one would let an agent
        // lock an app it is not even allowed to drive.
        var broker = BrokerWith("myapp#1");
        var existing = broker.ClientStatus("myapp#1")!;
        broker.Upsert(existing with { ReadOnly = true });

        var claims = new ClientClaimRegistry(TimeSpan.FromMinutes(15));
        await using var rig = new Rig(broker, claims: claims);
        var a = await rig.ConnectAsync("agent-a");
        await SelectAsync(a, "myapp#1", rig.Cts.Token);

        await CallAsync(a, WriteTool, null, rig.Cts.Token);

        Assert.Null(claims.Get("myapp#1"));
    }

    [Fact]
    public async Task HubStatus_Shows_Who_Is_Driving_What()
    {
        var broker = BrokerWith("myapp#1");
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("agent-a");
        var b = await rig.ConnectAsync("agent-b");

        await SelectAsync(a, "myapp#1", rig.Cts.Token);
        await CallAsync(a, WriteTool, null, rig.Cts.Token);

        var fromB = await OkJsonAsync(b, "hub_status", null, rig.Cts.Token);
        var claim = Assert.Single(fromB.GetProperty("claims").EnumerateArray());

        Assert.Equal("myapp#1", claim.GetProperty("clientId").GetString());
        Assert.Equal("agent-a-1", claim.GetProperty("agent").GetString());
        Assert.False(claim.GetProperty("mine").GetBoolean());
    }

    [Fact]
    public async Task Client_Listings_Mark_The_Instance_You_Own()
    {
        var broker = BrokerWith("myapp#1", "myapp#2");
        await using var rig = new Rig(broker);
        var a = await rig.ConnectAsync("agent-a");

        await SelectAsync(a, "myapp#1", rig.Cts.Token);
        await CallAsync(a, WriteTool, null, rig.Cts.Token);

        var listed = await OkJsonAsync(a, "hub_list_clients", null, rig.Cts.Token);
        var rows = listed.EnumerateArray().ToDictionary(e => e.GetProperty("clientId").GetString()!);

        Assert.True(rows["myapp#1"].GetProperty("mine").GetBoolean());
        Assert.Equal("agent-a-1", rows["myapp#1"].GetProperty("claimedBy").GetString());
        Assert.False(rows["myapp#2"].GetProperty("mine").GetBoolean());
    }
}
