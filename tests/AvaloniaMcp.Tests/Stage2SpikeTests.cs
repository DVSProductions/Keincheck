using System.IO.Pipelines;
using System.Text.Json;
using AvaloniaMcp.Client;
using AvaloniaMcp.Hub;
using AvaloniaMcp.Protocol;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace AvaloniaMcp.Tests;

/// <summary>
/// Stage-2 de-risking spikes. These RUN the three uncertain MCP-SDK mechanics end to
/// end (not just compile them):
/// <list type="number">
///   <item>#1 Client tool build from Core methods + schema extract + programmatic invoke.</item>
///   <item>#2 Hub dynamic tools (list/call handlers + tools/list_changed) — exercised
///         through a real MCP stream session.</item>
///   <item>#3 Connect-style bridge — an MCP client talks to the hub's MCP server over
///         an arbitrary duplex stream (the named-pipe substitute).</item>
/// </list>
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class Stage2SpikeTests
{
    private readonly HeadlessSession _session;
    private readonly ITestOutputHelper _out;

    public Stage2SpikeTests(HeadlessSession session, ITestOutputHelper @out)
    {
        _session = session;
        _out = @out;
    }

    // ====================================================================== #1

    // Blocking .GetResult() is deliberate here: the Core tools marshal onto the
    // single headless dispatcher via UiDispatch.Run, so awaiting on that same thread
    // would deadlock. We run the invoke from the test (pool) thread and let it
    // dispatch. xUnit1031 does not apply to this dispatch pattern.
#pragma warning disable xUnit1031
    [Fact]
    public void Mechanic1_BuildsTools_ExtractsSchema_AndInvokes()
    {
        // Build the tool host over the running headless Application + Core spine.
        var app = _session.RunOnUiThread(() => Avalonia.Application.Current!);
        using var host = ClientToolHost.Build(app, new AvaloniaMcp.Core.McpServerOptions());

        // (a) Tools were materialized from the Core [McpServerTool] methods.
        Assert.NotEmpty(host.Tools);
        Assert.True(host.Tools.ContainsKey("list_windows"),
            "expected the Core 'list_windows' tool to be built");

        // (b) Each tool exposes a protocol schema we can map to a ToolDescriptor.
        var descriptors = host.Describe();
        var listWindows = Assert.Single(descriptors, d => d.Name == "list_windows");
        Assert.False(string.IsNullOrWhiteSpace(listWindows.Description));
        Assert.NotNull(listWindows.InputSchema);
        Assert.Equal(JsonValueKind.Object, listWindows.InputSchema!.Value.ValueKind);
        _out.WriteLine($"list_windows schema: {listWindows.InputSchema}");

        // A tool WITH parameters must carry a real properties object in its schema.
        var getTree = Assert.Single(descriptors, d => d.Name == "get_logical_tree");
        var schema = getTree.InputSchema!.Value;
        Assert.True(schema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("maxDepth", out _),
            "get_logical_tree schema should declare its 'maxDepth' parameter");

        // (c) Programmatic invoke with NO args returns well-formed MCP content.
        // NOTE: list_windows enumerates open top-levels via the desktop lifetime,
        // which the shared headless session intentionally does not install (see
        // SpineUiTests.Query_With_Null_Scope_Searches_Open_TopLevels) — so the count
        // is 0 here. The point of THIS spike is that the SDK built the tool, exposed
        // its schema, and INVOKED it returning MCP content (proven by valid JSON).
        var result = host.InvokeAsync("list_windows", argumentsJson: null).GetAwaiter().GetResult();
        Assert.False(result.IsError ?? false);
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        _out.WriteLine($"list_windows result: {block.Text}");
        using (var doc = JsonDocument.Parse(block.Text))
        {
            Assert.True(doc.RootElement.TryGetProperty("windows", out var windows));
            Assert.Equal(JsonValueKind.Array, windows.ValueKind);
        }

        // (d) Invoke a PARAMETERIZED tool with JSON args to prove argument binding.
        // get_logical_tree honors a scoped query; with no handle/selector it falls
        // back to all roots (also empty in headless), but invocation + arg binding is
        // what we are proving — a clean, non-error result with content blocks.
        var args = JsonDocument.Parse("""{"maxDepth":2}""").RootElement;
        var tree = host.InvokeAsync("get_logical_tree", args).GetAwaiter().GetResult();
        Assert.False(tree.IsError ?? false);
        Assert.NotEmpty(tree.Content);
        _out.WriteLine($"get_logical_tree result: {Assert.IsType<TextContentBlock>(tree.Content[0]).Text}");

        // (e) An UNKNOWN-tool invoke fails cleanly (KeyNotFoundException), proving the
        // host validates names rather than silently no-op'ing.
        Assert.Throws<KeyNotFoundException>(() =>
            host.InvokeAsync("not_a_real_tool", null).GetAwaiter().GetResult());
    }
#pragma warning restore xUnit1031

    // ====================================================================== #2 + #3

    [Fact]
    public async Task Mechanic2And3_HubDynamicTools_OverStream_EndToEnd()
    {
        // A stub broker with one connected client whose tool catalog we control.
        var broker = new StubClientBroker();
        var invoked = new TaskCompletionSource<(string tool, string? argsJson)>();

        broker.InvokeHandler = (clientId, toolName, args, ct) =>
        {
            invoked.TrySetResult((toolName, args?.GetRawText()));
            // Echo a simple text content result back as the client would.
            var content = JsonSerializer.SerializeToElement(
                new object[] { new { type = "text", text = $"ran {toolName} on {clientId}" } },
                ProtocolJson.Options);
            return Task.FromResult(new ToolResultMessage
            {
                ClientId = clientId,
                ToolName = toolName,
                IsError = false,
                Content = content,
            });
        };

        broker.Upsert(new ClientInfo
        {
            ClientId = "app1",
            DisplayName = "Demo App",
            ProcessId = 1234,
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
        broker.ActiveClientId = "app1";

        // Start the hub MCP server (Mechanic #2) but DON'T start its HTTP host here;
        // we only use its dynamic handler wiring over a stream (Mechanic #3).
        var options = new HubOptions { ServeMcpOverPipe = false };
        await using var hub = HubMcpServer.Start(broker, options);
        try
        {
            var listener = new HubPipeMcpListener(hub, options);

            // Wire a full-duplex in-memory link: a stand-in for the named pipe.
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            // Server side: run the hub's MCP server over the stream pair.
            using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var serverTask = listener.RunMcpOverStreamAsync(
                input: clientToServer.Reader.AsStream(),
                output: serverToClient.Writer.AsStream(),
                ct: serverCts.Token);

            // Client side: a real MCP client over the mirrored streams.
            var transport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: null);

            await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(
                transport, clientOptions: null, loggerFactory: null, cancellationToken: serverCts.Token);

            // initialize handshake succeeded -> we have server info.
            Assert.Equal(options.ServerName, client.ServerInfo.Name);

            // tools/list -> dynamic list incl. meta-tools AND the proxied app1 tool.
            var tools = await client.ListToolsAsync(cancellationToken: serverCts.Token);
            var names = tools.Select(t => t.Name).ToHashSet();
            _out.WriteLine("tools/list: " + string.Join(", ", names));
            Assert.Contains("hub_list_clients", names);
            Assert.Contains("hub_select_client", names);
            Assert.Contains("get_logical_tree", names); // proxied from active client

            // tools/call on a meta-tool reaches the hub handler.
            var listResult = await client.CallToolAsync(
                "hub_list_clients", arguments: null, cancellationToken: serverCts.Token);
            Assert.False(listResult.IsError ?? false);
            var listText = Assert.IsType<TextContentBlock>(Assert.Single(listResult.Content)).Text;
            Assert.Contains("app1", listText);

            // tools/call on the proxied tool reaches InvokeOnClientAsync.
            var callResult = await client.CallToolAsync(
                "get_logical_tree",
                arguments: new Dictionary<string, object?> { ["maxDepth"] = 2 }!,
                cancellationToken: serverCts.Token);
            Assert.False(callResult.IsError ?? false);
            var (tool, argsJson) = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("get_logical_tree", tool);
            Assert.Contains("maxDepth", argsJson);
            var echoed = Assert.IsType<TextContentBlock>(Assert.Single(callResult.Content)).Text;
            Assert.Contains("ran get_logical_tree on app1", echoed);

            serverCts.Cancel();
            try { await serverTask; } catch { /* cancellation */ }
        }
        finally
        {
            // hub disposed by await using
        }
    }
}
