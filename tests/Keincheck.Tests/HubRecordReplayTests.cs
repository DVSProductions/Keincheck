using System.IO.Pipelines;
using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Behavioural tests for the Phase-C Hub record/replay/guide surface: arming a recording
/// with <c>hub_record_start</c>, proxying a couple of UI tool calls through the hub to a
/// stub client, capturing them, stopping with <c>hub_record_stop</c>, and re-issuing them
/// with <c>hub_replay</c> — plus <c>hub_guide</c> returning a non-empty markdown briefing.
/// Each test drives a real <see cref="McpClient"/> against the hub over an in-memory duplex
/// stream pair (the named-pipe substitute), exercising the SDK wiring end to end with the
/// <see cref="StubClientBroker"/> standing in for live clients — no UI/Avalonia needed.
/// </summary>
/// <remarks>
/// Mirrors <see cref="HubMetaToolTests"/>' transport harness deliberately so the two can be
/// diffed: same connect/teardown plumbing, focused here on the recorder-backed meta-tools
/// that are routed in <see cref="HubMcpServer"/> ahead of the pure dispatcher.
/// </remarks>
public sealed class HubRecordReplayTests
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
                new ToolDescriptor
                {
                    Name = "query_controls",
                    Description = "Resolve a selector.",
                    InputSchema = JsonDocument.Parse(
                        """{"type":"object","properties":{"selector":{"type":"string"}}}""").RootElement.Clone(),
                },
            },
        });
        return broker;
    }

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

    // ---- hub_guide ---------------------------------------------------------

    [Fact]
    public async Task HubGuide_Returns_NonEmpty_Markdown()
    {
        var broker = BrokerWithClient();
        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            var call = await client.CallToolAsync(
                "hub_guide", new Dictionary<string, object?>(), cancellationToken: cts.Token);

            Assert.False(call.IsError ?? false);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(call.Content)).Text;
            Assert.False(string.IsNullOrWhiteSpace(text));
            // It is the broker onboarding doc: it must name the broker model and the flow.
            Assert.Contains("Keincheck Hub", text);
            Assert.Contains("hub_select_client", text);
            Assert.Contains("record", text, StringComparison.OrdinalIgnoreCase);
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    // ---- record -> proxied invokes -> stop ---------------------------------

    [Fact]
    public async Task Record_Captures_Only_Proxied_Steps_Then_Stop_Reports_Count()
    {
        var broker = BrokerWithClient();
        broker.ActiveClientId = "app1";

        var invokes = new List<(string client, string tool)>();
        broker.InvokeHandler = (clientId, tool, args, ct) =>
        {
            invokes.Add((clientId, tool));
            return Task.FromResult(EchoResult(clientId, tool));
        };

        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            // Arm recording — a meta-tool, itself never captured.
            var start = await client.CallToolAsync("hub_record_start",
                new Dictionary<string, object?> { ["name"] = "login-flow" }!, cancellationToken: cts.Token);
            Assert.False(start.IsError ?? false);

            // Two proxied UI tool calls — both flow through to the active client and capture.
            var c1 = await client.CallToolAsync("get_logical_tree",
                new Dictionary<string, object?> { ["maxDepth"] = 2 }!, cancellationToken: cts.Token);
            var c2 = await client.CallToolAsync("query_controls",
                new Dictionary<string, object?> { ["selector"] = "Button" }!, cancellationToken: cts.Token);
            Assert.False(c1.IsError ?? false);
            Assert.False(c2.IsError ?? false);

            // Status before stop: recording active, two steps buffered, name preserved.
            var status = await client.CallToolAsync("hub_record_status",
                new Dictionary<string, object?>(), cancellationToken: cts.Token);
            using (var sd = JsonDocument.Parse(StatusText(status)))
            {
                Assert.True(sd.RootElement.GetProperty("recording").GetBoolean());
                Assert.Equal(2, sd.RootElement.GetProperty("steps").GetInt32());
                Assert.Equal("login-flow", sd.RootElement.GetProperty("name").GetString());
            }

            // Stop returns the captured step count and disarms.
            var stop = await client.CallToolAsync("hub_record_stop",
                new Dictionary<string, object?>(), cancellationToken: cts.Token);
            using (var sd = JsonDocument.Parse(StatusText(stop)))
            {
                Assert.False(sd.RootElement.GetProperty("recording").GetBoolean());
                Assert.Equal(2, sd.RootElement.GetProperty("steps").GetInt32());
            }

            // The two captured invokes were the proxied UI tools, not the meta tools.
            Assert.Equal(2, invokes.Count);
            Assert.Equal(("app1", "get_logical_tree"), invokes[0]);
            Assert.Equal(("app1", "query_controls"), invokes[1]);
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    // ---- replay re-issues every captured step ------------------------------

    [Fact]
    public async Task Replay_ReIssues_Every_Recorded_Step_In_Order()
    {
        var broker = BrokerWithClient();
        broker.ActiveClientId = "app1";

        var invokes = new List<(string client, string tool, string? args)>();
        broker.InvokeHandler = (clientId, tool, args, ct) =>
        {
            invokes.Add((clientId, tool, args?.GetRawText()));
            return Task.FromResult(EchoResult(clientId, tool));
        };

        var (client, hub, cts, serverTask) = await ConnectAsync(broker);
        try
        {
            await client.CallToolAsync("hub_record_start",
                new Dictionary<string, object?>(), cancellationToken: cts.Token);
            await client.CallToolAsync("get_logical_tree",
                new Dictionary<string, object?> { ["maxDepth"] = 3 }!, cancellationToken: cts.Token);
            await client.CallToolAsync("query_controls",
                new Dictionary<string, object?> { ["selector"] = "TextBox" }!, cancellationToken: cts.Token);
            await client.CallToolAsync("hub_record_stop",
                new Dictionary<string, object?>(), cancellationToken: cts.Token);

            var recordedInvokeCount = invokes.Count; // 2 (the capture-time invokes)

            // Replay re-issues the two buffered steps to their original client, in order.
            var replay = await client.CallToolAsync("hub_replay",
                new Dictionary<string, object?> { ["stopOnError"] = true }!, cancellationToken: cts.Token);
            Assert.False(replay.IsError ?? false);

            using (var rd = JsonDocument.Parse(StatusText(replay)))
            {
                var r = rd.RootElement;
                Assert.Equal(2, r.GetProperty("replayed").GetInt32());
                Assert.Equal(2, r.GetProperty("ok").GetInt32());
                Assert.Equal(0, r.GetProperty("failed").GetInt32());
                Assert.Equal(0, r.GetProperty("skipped").GetInt32());
            }

            // The broker saw the two steps a SECOND time, same client/tool/args, in order.
            Assert.Equal(recordedInvokeCount + 2, invokes.Count);
            var replayed = invokes.Skip(recordedInvokeCount).ToList();
            Assert.Equal(("app1", "get_logical_tree"), (replayed[0].client, replayed[0].tool));
            Assert.Contains("maxDepth", replayed[0].args);
            Assert.Equal(("app1", "query_controls"), (replayed[1].client, replayed[1].tool));
            Assert.Contains("TextBox", replayed[1].args);
        }
        finally { await TeardownAsync(client, hub, cts, serverTask); }
    }

    // ---- helpers -----------------------------------------------------------

    private static string StatusText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

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
