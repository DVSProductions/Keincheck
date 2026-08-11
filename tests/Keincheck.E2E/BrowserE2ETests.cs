using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Keincheck.E2E;

/// <summary>
/// A real browser, running the WebAssembly demo, attached over the WebSocket transport and
/// driven through MCP.
///
/// Nothing else proves any of this. The unit suite exercises the gate's decisions in-process,
/// and a curl probe proves the endpoint upgrades — neither can tell you whether a browser
/// completes the handshake, whether the adapter finds a root under a single-view lifetime, or
/// whether an <c>x:Name</c> selector resolves through WebAssembly. Every one of those broke at
/// least once while this transport was being written, and each break looked like success
/// somewhere else.
/// </summary>
[Collection(HubCollection.Name)]
public sealed class BrowserE2ETests(HubRig rig, ITestOutputHelper output)
{
    private const string AppId = "browserdemo";

    /// <summary>
    /// The whole chain in one test, on purpose. Attach, enumerate, read, act, and observe the
    /// app react — split across facts, each would re-pay a 30-second wasm cold boot, and a
    /// failure in the middle would leave the later ones to fail for a different reason.
    /// </summary>
    [BrowserE2EFact]
    public async Task A_Browser_App_Attaches_And_Is_Drivable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        await using var browser = await BrowserRig.StartAsync(E2EEnvironment.BrowserBundle!);

        var client = await rig.ConnectViaHttpAsync(cts.Token);
        try
        {
            // Snapshot BEFORE opening the page, and wait for an id that was not in it. Matching
            // on appId alone would happily bind to a browser tab someone left open from an
            // earlier run -- which is not a hypothetical: the first run of this test attached to
            // a stale client and read a counter of 2 from a page it had never touched.
            var before = await ClientIdsAsync(client, cts.Token);

            await using var page = await browser.OpenAsync();

            var clientId = await WaitForNewClientAsync(client, browser, before, cts.Token);
            output.WriteLine($"attached as {clientId}");

            await Select(client, clientId, cts.Token);

            // The transport must name itself. An agent reads this field to decide whether the
            // client is on this machine and whether launch/restart apply, so "unknown" is a
            // functional defect, not a cosmetic one -- and it is what a browser reported until
            // the enum member was added to the mapping.
            Assert.Equal("websocket", await TransportOf(client, clientId, cts.Token));

            // A single-view lifetime has no Window at all. The adapter has to resolve the root
            // through MainView's TopLevel instead, and when it silently does not, every
            // selector below fails with "matched no controls" and nothing says why.
            var windows = McpJson.Ok(await Call(client, "list_windows", new(), cts.Token));
            Assert.Equal(1, windows.Int("count"));

            Assert.Equal("Keincheck browser demo", await TextOf(client, "#Title", cts.Token));
            Assert.Equal("0", await TextOf(client, "#CounterText", cts.Token));

            // Acting, and then observing that the APP reacted. Asserting only that the tool
            // returned ok would pass against a click that arrived and did nothing.
            await Invoke(client, "#IncrementButton", cts.Token);
            await Invoke(client, "#IncrementButton", cts.Token);
            Assert.Equal("2", await TextOf(client, "#CounterText", cts.Token));

            await Call(client, "type_text", new()
            {
                ["selector"] = "#NameBox",
                ["text"] = "driven from a browser",
            }, cts.Token);
            Assert.Equal("You typed: driven from a browser", await TextOf(client, "#EchoText", cts.Token));

            await Call(client, "set_property", new()
            {
                ["selector"] = "#LevelSlider",
                ["propertyName"] = "Value",
                ["value"] = 73,
            }, cts.Token);
            Assert.Equal("73", await TextOf(client, "#LevelText", cts.Token));

            // A screenshot proves the surface actually rendered. A WebAssembly app whose
            // renderer never came up still answers every tool above from the visual tree, so
            // without this the suite would pass over a page that paints nothing.
            var shot = McpJson.Image(await Call(client, "screenshot_window", new(), cts.Token));
            Assert.NotNull(shot);
            // DecodedData is the PNG; Data is its base64 form, as HubEndToEndScenario notes.
            McpJson.Save("browser-demo.png", shot!.DecodedData.ToArray());
        }
        catch (Exception ex)
        {
            // The page console is the only place a WebAssembly startup failure is visible: on
            // the .NET side it surfaces as a client that simply never registers.
            throw new Xunit.Sdk.XunitException(
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}"
                + $"--- page console ---{Environment.NewLine}{browser.DescribeConsole()}");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// Polls the hub until the browser registers. The page finishing its load says nothing
    /// about attachment: a cold wasm boot downloads and JITs the runtime, then the app starts,
    /// then it connects.
    /// </summary>
    private static async Task<string> WaitForNewClientAsync(
        McpClient client, BrowserRig browser, IReadOnlySet<string> before, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            var clients = McpJson.Ok(await Call(client, "hub_list_clients", new(), ct));
            foreach (var c in clients.EnumerateArray())
            {
                var id = c.Str("clientId");
                if (c.Str("appId") == AppId && c.Bool("connected") == true
                    && id is not null && !before.Contains(id))
                {
                    return id;
                }
            }
            await Task.Delay(1000, ct);
        }

        throw new Xunit.Sdk.XunitException(
            $"No '{AppId}' client attached within the timeout. The page loaded, so either the "
            + $"app failed to start or the hub refused the handshake (origin {BrowserRig.Origin} "
            + $"must be allowlisted and the token must match).{Environment.NewLine}"
            + $"--- page console ---{Environment.NewLine}{browser.DescribeConsole()}");
    }

    /// <summary>The ids of every client the hub currently knows, for before/after comparison.</summary>
    private static async Task<HashSet<string>> ClientIdsAsync(McpClient client, CancellationToken ct)
    {
        var clients = McpJson.Ok(await Call(client, "hub_list_clients", new(), ct));
        return [.. clients.EnumerateArray().Select(c => c.Str("clientId")).OfType<string>()];
    }

    private static Task<CallToolResult> Call(
        McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
        => client.CallToolAsync(tool, args, cancellationToken: ct).AsTask();

    private static async Task Select(McpClient client, string clientId, CancellationToken ct)
        => McpJson.Ok(await Call(client, "hub_select_client", new() { ["clientId"] = clientId }, ct));

    private static async Task<string?> TransportOf(McpClient client, string clientId, CancellationToken ct)
    {
        var clients = McpJson.Ok(await Call(client, "hub_list_clients", new(), ct));
        foreach (var c in clients.EnumerateArray())
        {
            if (c.Str("clientId") == clientId)
                return c.Str("transport");
        }
        return null;
    }

    private static async Task<string?> TextOf(McpClient client, string selector, CancellationToken ct)
        => McpJson.Ok(await Call(client, "get_text", new() { ["selector"] = selector }, ct)).Str("text");

    private static async Task Invoke(McpClient client, string selector, CancellationToken ct)
        => McpJson.Ok(await Call(client, "automation_action", new()
        {
            ["selector"] = selector,
            ["action"] = "Invoke",
        }, ct));
}
