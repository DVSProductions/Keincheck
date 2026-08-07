using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Keincheck.E2E;

/// <summary>
/// Several AI agents against ONE real hub, over the real transports.
/// </summary>
/// <remarks>
/// <para>
/// This is the production shape the in-memory suites cannot reproduce: two separate
/// <c>keincheck-connect.exe</c> processes dialling the same pipe of the same hub — exactly
/// what two Claude Code windows do — plus the loopback HTTP endpoint, whose session
/// resolution takes a different code path from the pipe's (the HTTP host shares one service
/// container across sessions, so it relies on the ambient channel rather than per-session DI).
/// </para>
/// <para>
/// What has to hold: each agent's selection is its own, and only one agent at a time may
/// drive an app instance while all of them may read it.
/// </para>
/// </remarks>
[Collection(HubCollection.Name)]
public sealed class MultiAgentE2ETests(HubRig rig, ITestOutputHelper output)
{
    private static readonly TimeSpan Overall = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);

    private static string DemoExe =>
        Environment.GetEnvironmentVariable(E2EEnvironment.DemoExeVar)
        ?? throw new InvalidOperationException($"{E2EEnvironment.DemoExeVar} is not set.");

    private static async Task<CallToolResult> Call(
        McpClient mcp, string tool, Dictionary<string, object?>? args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        return await mcp.CallToolAsync(tool, args, cancellationToken: timeout.Token);
    }

    private void Log(string line) => output.WriteLine($"    {line}");

    /// <summary>
    /// Blocks until a second connected instance of the demo (one that is not
    /// <paramref name="firstId"/>) appears, and returns its hub id.
    /// </summary>
    private static async Task<string> WaitForSecondInstance(
        McpClient mcp, string firstId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var clients = McpJson.Ok(await Call(mcp, "hub_list_clients", null, ct));
            var other = clients.EnumerateArray()
                .Select(c => c.Str("clientId"))
                .FirstOrDefault(id => id is not null && id != firstId);
            if (other is not null)
                return other;

            await Task.Delay(250, ct);
        }
        return "";
    }

    [E2EFact]
    public async Task Two_Agents_Share_One_Hub_Without_Colliding()
    {
        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;

        // Two shim processes — the exact path two editor windows take.
        await using var agentA = await rig.ConnectViaShimAsync(ct);
        await using var agentB = await rig.ConnectViaShimAsync(ct);

        var statusA = McpJson.Ok(await Call(agentA, "hub_status", null, ct));
        var statusB = McpJson.Ok(await Call(agentB, "hub_status", null, ct));

        // Each session is tracked separately and knows it is not alone.
        Assert.True(statusA.Int("agentSessions") >= 2,
            "the hub should see both shim sessions at once.");
        var labelA = statusA.Str("agent")!;
        var labelB = statusB.Str("agent")!;
        Assert.NotEqual(labelA, labelB);
        Log($"agents: {labelA}, {labelB}");

        // Attach TWO instances of the demo — the shape a pair of agents each testing their
        // own build produces, and the only way to prove selections are genuinely independent
        // rather than coincidentally equal.
        var demo1 = rig.Track(ManagedProcess.Start("demo-a", DemoExe));
        var first = McpJson.Ok(await Call(agentA, "hub_wait_for_client",
            new() { ["appId"] = "demo", ["timeoutMs"] = 90_000 }, ct));
        Assert.True(first.Bool("connected") ?? false, $"the first demo never attached. {demo1.Describe()}");
        var clientId = first.Str("clientId")!;

        var demo2 = rig.Track(ManagedProcess.Start("demo-b", DemoExe));
        var second = await WaitForSecondInstance(agentA, clientId, ct);
        Assert.False(string.IsNullOrEmpty(second), $"the second demo never attached. {demo2.Describe()}");
        Log($"demos attached as {clientId} and {second}");

        // ---- selection is per agent -------------------------------------
        // Each agent picks a different instance. Before per-session state, whichever selected
        // last silently retargeted the other's next call.
        McpJson.Ok(await Call(agentA, "hub_select_client", new() { ["clientId"] = clientId }, ct));
        McpJson.Ok(await Call(agentB, "hub_select_client", new() { ["clientId"] = second }, ct));

        var afterA = McpJson.Ok(await Call(agentA, "hub_status", null, ct));
        var afterB = McpJson.Ok(await Call(agentB, "hub_status", null, ct));

        Assert.Equal(clientId, afterA.Str("activeClientId"));
        Assert.Equal(second, afterB.Str("activeClientId"));

        // ---- one driver, many readers -----------------------------------
        // A drives it, which claims it.
        var controls = McpJson.Ok(await Call(agentA, "query_controls",
            new() { ["selector"] = "Button" }, ct));
        Assert.NotEmpty(controls.Array("controls"));

        McpJson.Ok(await Call(agentA, "automation_action",
            new() { ["selector"] = "#CountButton", ["action"] = "Invoke" }, ct));

        // B may still READ it, targeting it explicitly for this one call.
        var readByB = await Call(agentB, "hub_call_tool", new()
        {
            ["tool"] = "query_controls",
            ["client"] = clientId,
            ["args"] = new Dictionary<string, object?> { ["selector"] = "Button" },
        }, ct);
        Assert.False(readByB.IsError ?? false,
            $"reading a claimed app must stay allowed: {McpJson.Text(readByB)}");

        // ...but B may NOT drive it.
        var writeByB = await Call(agentB, "hub_call_tool", new()
        {
            ["tool"] = "automation_action",
            ["client"] = clientId,
            ["args"] = new Dictionary<string, object?>
            {
                ["selector"] = "#CountButton",
                ["action"] = "Invoke",
            },
        }, ct);

        Assert.True(writeByB.IsError ?? false,
            "a second agent must not be able to drive an app the first is driving.");

        var structured = writeByB.StructuredContent!.Value;
        Assert.Equal("client_claimed", structured.GetProperty("error").GetString());
        Assert.Equal(labelA, structured.GetProperty("owner").GetProperty("session").GetString());
        Log($"contention reported correctly: held by {labelA}");

        // ---- releasing hands it over ------------------------------------
        McpJson.Ok(await Call(agentA, "hub_release_client", new() { ["clientId"] = clientId }, ct));

        var writeAfterRelease = await Call(agentB, "hub_call_tool", new()
        {
            ["tool"] = "automation_action",
            ["client"] = clientId,
            ["args"] = new Dictionary<string, object?>
            {
                ["selector"] = "#CountButton",
                ["action"] = "Invoke",
            },
        }, ct);

        Assert.False(writeAfterRelease.IsError ?? false,
            $"after release the app must be drivable by the other agent: {McpJson.Text(writeAfterRelease)}");

        // Tidy up so the shared hub is left free for the other facts in this collection.
        McpJson.Ok(await Call(agentB, "hub_release_client", null, ct));
    }

    [E2EFact]
    public async Task The_Http_Endpoint_Gets_Its_Own_Session_Too()
    {
        // The HTTP host shares one service container across sessions, so it resolves the
        // caller through the ambient channel instead of per-session DI. Nothing but a real
        // Kestrel round-trip exercises that branch.
        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;

        await using var overPipe = await rig.ConnectViaShimAsync(ct);
        await using var overHttp = await rig.ConnectViaHttpAsync(ct);

        var pipeStatus = McpJson.Ok(await Call(overPipe, "hub_status", null, ct));
        var httpStatus = McpJson.Ok(await Call(overHttp, "hub_status", null, ct));

        var pipeAgent = pipeStatus.Str("agent");
        var httpAgent = httpStatus.Str("agent");

        Assert.False(string.IsNullOrWhiteSpace(pipeAgent), "the pipe session was not identified.");
        Assert.False(string.IsNullOrWhiteSpace(httpAgent), "the HTTP session was not identified.");
        Assert.NotEqual(pipeAgent, httpAgent);
        Log($"pipe={pipeAgent} http={httpAgent}");

        // And the two do not share a selection: an HTTP client is an agent like any other.
        var demo = rig.Track(ManagedProcess.Start("demo-http", DemoExe));
        var waited = McpJson.Ok(await Call(overHttp, "hub_wait_for_client",
            new() { ["appId"] = "demo", ["timeoutMs"] = 90_000 }, ct));
        Assert.True(waited.Bool("connected") ?? false, $"the demo never attached. {demo.Describe()}");
        var clientId = waited.Str("clientId")!;

        // Compare the pipe session before and after rather than expecting a particular
        // value: this collection shares one hub, so other facts may have left apps attached
        // and this session may legitimately have adopted one already.
        var pipeBefore = McpJson.Ok(await Call(overPipe, "hub_status", null, ct)).Str("activeClientId");

        McpJson.Ok(await Call(overHttp, "hub_select_client", new() { ["clientId"] = clientId }, ct));

        Assert.Equal(clientId, McpJson.Ok(await Call(overHttp, "hub_status", null, ct)).Str("activeClientId"));

        var pipeAfter = McpJson.Ok(await Call(overPipe, "hub_status", null, ct)).Str("activeClientId");
        Assert.Equal(pipeBefore, pipeAfter);
    }
}
