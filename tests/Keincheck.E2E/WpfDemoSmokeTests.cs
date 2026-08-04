using Xunit;
using Xunit.Abstractions;

namespace Keincheck.E2E;

/// <summary>
/// A thin smoke test for the WPF adapter: the second toolkit binding attaches to the same
/// hub, is addressable by the same selectors, and drives a real control.
/// </summary>
/// <remarks>
/// Deliberately shallow. The Avalonia demo carries the exhaustive scenario; what needs
/// proving here is only that <c>WpfUiAdapter</c> + <c>ApplicationClientExtensions</c> reach
/// the hub at all and dispatch onto the WPF UI thread — the toolkit-neutral spine above
/// them is the same code either way.
/// </remarks>
[Collection(HubCollection.Name)]
public sealed class WpfDemoSmokeTests(HubRig rig, ITestOutputHelper output)
{
    [E2EFact]
    public async Task Wpf_Demo_Attaches_And_Is_Drivable()
    {
        var exe = Environment.GetEnvironmentVariable(E2EEnvironment.WpfDemoExeVar);
        if (string.IsNullOrWhiteSpace(exe))
        {
            output.WriteLine($"{E2EEnvironment.WpfDemoExeVar} is not set; skipping the WPF adapter smoke.");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        await using var mcp = await rig.ConnectViaShimAsync(ct);

        var wpf = rig.Track(ManagedProcess.Start("wpfdemo", exe));

        var attached = McpJson.Ok(await mcp.CallToolAsync("hub_wait_for_client",
            new Dictionary<string, object?> { ["appId"] = "wpfdemo", ["timeoutMs"] = 90_000 },
            cancellationToken: ct));
        Assert.True(attached.Bool("connected") ?? false,
            $"the WPF demo never attached.\n{wpf.Describe()}");

        var clientId = attached.Str("clientId")!;
        Assert.Matches(@"^wpfdemo#\d+$", clientId);
        output.WriteLine($"WPF demo attached as {clientId}");

        // Two clients are now connected; target this one explicitly rather than relying on
        // whatever happens to be active.
        await mcp.CallToolAsync("hub_select_client",
            new Dictionary<string, object?> { ["clientId"] = clientId }, cancellationToken: ct);

        var windows = McpJson.Ok(await mcp.CallToolAsync("list_windows", null, cancellationToken: ct));
        Assert.NotEmpty(windows.Array("windows"));

        var hits = McpJson.Ok(await mcp.CallToolAsync("query_controls",
            new Dictionary<string, object?> { ["selector"] = "#Save" }, cancellationToken: ct));
        Assert.Equal(1, hits.Int("count"));

        var before = McpJson.Ok(await mcp.CallToolAsync("get_property",
            new Dictionary<string, object?> { ["selector"] = "#CountLabel", ["propertyName"] = "Text" },
            cancellationToken: ct));
        Assert.Equal("Saves: 0", before.Str("value"));

        // The write half. If WpfUiAdapter's automation path is incomplete this is where it
        // shows, which is the point of running it at all.
        var invoked = McpJson.Ok(await mcp.CallToolAsync("automation_action",
            new Dictionary<string, object?> { ["selector"] = "#Save", ["action"] = "Invoke" },
            cancellationToken: ct));
        Assert.True(invoked.Bool("ok") ?? false, $"invoking the WPF Save button failed: {invoked}");

        var waited = McpJson.Ok(await mcp.CallToolAsync("wait_for", new Dictionary<string, object?>
        {
            ["selector"] = "#CountLabel",
            ["propertyName"] = "Text",
            ["expected"] = "Saves: 1",
            ["timeoutMs"] = 10_000,
        }, cancellationToken: ct));
        Assert.True(waited.Bool("ok") ?? false, $"the WPF click had no observable effect: {waited}");

        // Leave the hub with one client, as the rest of the suite expects.
        wpf.Kill();
    }
}
