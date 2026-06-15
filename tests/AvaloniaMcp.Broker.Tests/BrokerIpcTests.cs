using System.Text.Json;
using AvaloniaMcp.Protocol;
using Xunit;

namespace AvaloniaMcp.Broker.Tests;

/// <summary>
/// Stage-2 broker IPC integration tests. Each test spins up the broker pipe server and
/// a fake client <b>in-process</b> over the real <see cref="PipeChannel"/> /
/// <see cref="PipeTransport"/> and the Protocol DTOs, then asserts one slice of the
/// broker contract: register → catalog, invoke round-trip, drop → ClientDown, restart
/// reuses the identifier, and read-only rejection of a mutating tool.
/// </summary>
public sealed class BrokerIpcTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // A unique per-test pipe name so runs never collide with a stale instance, with the
    // hub's live control pipe, or with each other under parallel execution.
    private static string NewPipeName() => $"AvaloniaMcp.test.{Guid.NewGuid():N}";

    private static IReadOnlyList<ToolDescriptor> SampleTools() => new[]
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
            Name = "set_property",
            Description = "Mutate a control property.",
            InputSchema = JsonDocument.Parse(
                """{"type":"object","properties":{"value":{"type":"string"}}}""").RootElement.Clone(),
        },
    };

    /// <summary>Awaits the next broker event of the given kind, or fails on timeout.</summary>
    private static (Task<BrokerClientInfo> task, Action dispose) NextEvent(
        Action<EventHandler<BrokerClientInfo>> subscribe,
        Action<EventHandler<BrokerClientInfo>> unsubscribe)
    {
        var tcs = new TaskCompletionSource<BrokerClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, BrokerClientInfo info) => tcs.TrySetResult(info);
        subscribe(Handler);
        return (tcs.Task, () => unsubscribe(Handler));
    }

    // ====================================================================== (1)

    [Fact]
    public async Task Register_Then_Client_Appears_In_ListClients_With_Tools()
    {
        var pipe = NewPipeName();
        await using var broker = new PipeBrokerHarness(pipe);

        var (connected, unsub) = NextEvent(h => broker.ClientConnected += h, h => broker.ClientConnected -= h);
        try
        {
            await using var client = await FakeBrokerClient.ConnectAsync(
                pipe, "demo", SampleTools(), displayName: "Demo App", processId: 4321);

            var info = await connected.WaitAsync(Timeout);
            Assert.Equal("demo", info.ClientId);

            // The tool catalog arrives in a separate ToolList message; poll until the
            // registry has absorbed it (register and tool-list are two wire messages).
            var listed = await WaitForAsync(
                () => broker.ListClients().FirstOrDefault(c => c.ClientId == "demo"),
                c => c is { Tools.Count: > 0 });

            Assert.NotNull(listed);
            Assert.True(listed!.IsConnected);
            Assert.Equal("Demo App", listed.DisplayName);
            Assert.Equal(4321, listed.ProcessId);
            var toolNames = listed.Tools.Select(t => t.Name).ToHashSet();
            Assert.Contains("get_logical_tree", toolNames);
            Assert.Contains("set_property", toolNames);

            // ListKnownClients includes the live one too.
            Assert.Contains(broker.ListKnownClients(), c => c.ClientId == "demo");
        }
        finally { unsub(); }
    }

    // ====================================================================== (2)

    [Fact]
    public async Task InvokeOnClientAsync_RoundTrips_A_ToolResult()
    {
        var pipe = NewPipeName();
        await using var broker = new PipeBrokerHarness(pipe);

        // The fake client echoes the tool name + the arguments it received so we can
        // assert the call actually crossed the wire with its payload intact.
        await using var client = await FakeBrokerClient.ConnectAsync(
            pipe, "demo", SampleTools(),
            onInvoke: invoke => new ToolResultMessage
            {
                ClientId = "demo",
                ToolName = invoke.ToolName,
                IsError = false,
                Content = JsonSerializer.SerializeToElement(new object[]
                {
                    new { type = "text", text = $"ran {invoke.ToolName} args={invoke.Arguments?.GetRawText()}" },
                }, ProtocolJson.Options),
            });

        await WaitForConnectedAsync(broker, "demo");

        var args = JsonDocument.Parse("""{"maxDepth":3}""").RootElement;
        var result = await broker.InvokeOnClientAsync("demo", "get_logical_tree", args).WaitAsync(Timeout);

        Assert.False(result.IsError);
        Assert.Equal("get_logical_tree", result.ToolName);
        Assert.Equal("demo", result.ClientId);
        Assert.NotNull(result.Content);

        // Content is a JSON array of MCP content blocks; the text block carries our echo.
        var content = result.Content!.Value;
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var text = content[0].GetProperty("text").GetString();
        Assert.Contains("ran get_logical_tree", text);
        Assert.Contains("maxDepth", text); // the args round-tripped to the client verbatim
    }

    // ====================================================================== (3)

    [Fact]
    public async Task Client_Drop_Raises_ClientDown()
    {
        var pipe = NewPipeName();
        await using var broker = new PipeBrokerHarness(pipe);

        var (down, unsub) = NextEvent(h => broker.ClientDown += h, h => broker.ClientDown -= h);
        try
        {
            var client = await FakeBrokerClient.ConnectAsync(pipe, "demo", SampleTools());
            await WaitForConnectedAsync(broker, "demo");

            // Abrupt transport drop (no graceful ClientDown message) — the broker must
            // still detect the disconnect from the closed pipe and raise ClientDown.
            await client.DisposeAsync();

            var info = await down.WaitAsync(Timeout);
            Assert.Equal("demo", info.ClientId);
            Assert.False(info.IsConnected);

            // It leaves ListClients (live) but remains in ListKnownClients (history).
            Assert.DoesNotContain(broker.ListClients(), c => c.ClientId == "demo");
            Assert.Contains(broker.ListKnownClients(), c => c.ClientId == "demo");
        }
        finally { unsub(); }
    }

    // ====================================================================== (4)

    [Fact]
    public async Task Restart_Reserves_And_Reuses_The_Identifier()
    {
        var pipe = NewPipeName();
        await using var broker = new PipeBrokerHarness(pipe);

        // First incarnation registers, reports tools, then goes away gracefully.
        var first = await FakeBrokerClient.ConnectAsync(
            pipe, "demo", SampleTools(), displayName: "Demo App", processId: 1000);
        await WaitForConnectedAsync(broker, "demo");
        await first.DisconnectGracefullyAsync();

        await WaitForAsync(
            () => broker.ClientStatus("demo"),
            c => c is { IsConnected: false });

        // The id is retained as KNOWN while down (reserved for the restart).
        Assert.Contains(broker.ListKnownClients(), c => c.ClientId == "demo");
        Assert.DoesNotContain(broker.ListClients(), c => c.ClientId == "demo");

        // Restart: a new process reconnects under the SAME id and is reattached to the
        // same registry entry rather than spawning a duplicate.
        await using var second = await FakeBrokerClient.ConnectAsync(
            pipe, "demo", SampleTools(), displayName: "Demo App", processId: 2000);

        var restarted = await WaitForAsync(
            () => broker.ListClients().FirstOrDefault(c => c.ClientId == "demo"),
            c => c is { IsConnected: true, ProcessId: 2000 });

        Assert.NotNull(restarted);
        Assert.Equal("demo", restarted!.ClientId);
        Assert.Equal(2000, restarted.ProcessId); // the new instance, same identifier

        // Exactly one registry entry for the id — the identifier was reused, not duplicated.
        Assert.Single(broker.ListKnownClients(), c => c.ClientId == "demo");
    }

    // ====================================================================== (5)

    [Fact]
    public async Task ReadOnly_Client_Rejects_A_Mutating_Tool()
    {
        var pipe = NewPipeName();
        await using var broker = new PipeBrokerHarness(pipe);

        // The client invoke handler must never fire for a mutating tool on a read-only
        // client — the broker rejects it before it reaches the wire.
        var mutatingInvoked = false;
        await using var client = await FakeBrokerClient.ConnectAsync(
            pipe, "ro", SampleTools(),
            onInvoke: invoke =>
            {
                if (PipeBrokerHarness.IsMutatingTool(invoke.ToolName))
                    mutatingInvoked = true;
                return new ToolResultMessage
                {
                    ClientId = "ro",
                    ToolName = invoke.ToolName,
                    IsError = false,
                    Content = JsonSerializer.SerializeToElement(
                        new object[] { new { type = "text", text = "ok" } }, ProtocolJson.Options),
                };
            });

        await WaitForConnectedAsync(broker, "ro");
        broker.SetReadOnly("ro", true);

        // A mutating tool (set_property) is refused locally by the broker gate.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.InvokeOnClientAsync("ro", "set_property", null));
        Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(mutatingInvoked, "the mutating tool must not reach the read-only client");

        // A read-only tool (get_logical_tree) still round-trips successfully.
        var ok = await broker.InvokeOnClientAsync("ro", "get_logical_tree", null).WaitAsync(Timeout);
        Assert.False(ok.IsError);
        Assert.Equal("get_logical_tree", ok.ToolName);
    }

    // ====================================================================== helpers

    private static Task WaitForConnectedAsync(PipeBrokerHarness broker, string clientId) =>
        WaitForAsync(() => broker.ClientStatus(clientId), c => c is { IsConnected: true });

    /// <summary>Polls <paramref name="probe"/> until <paramref name="predicate"/> holds or times out.</summary>
    private static async Task<T> WaitForAsync<T>(Func<T> probe, Func<T, bool> predicate)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var value = probe();
            if (predicate(value))
                return value;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition not met within the broker IPC timeout.");
            await Task.Delay(15);
        }
    }
}
