using Xunit;
using Xunit.Abstractions;

namespace Keincheck.E2E;

/// <summary>
/// Drives the app built from the packed NuGet packages, closing the gap
/// <c>docs/remote-design.md</c> lists as unproven: whether the packages actually restore —
/// and here, work — in a consuming project.
/// </summary>
/// <remarks>
/// Restoring and compiling is the cheap half and the CI job does that on its own. This is
/// the other half: a package that resolves but whose <c>UseMcpClient</c> glue does not reach
/// the hub would pass a build check and fail every real user.
/// </remarks>
[Collection(HubCollection.Name)]
public sealed class ConsumerAppTests(HubRig rig, ITestOutputHelper output)
{
    /// <summary>Set by the CI job to the consumer built out of the local package feed.</summary>
    public const string ConsumerExeVar = "KEINCHECK_E2E_CONSUMER_EXE";

    [E2EFact]
    public async Task Packaged_Consumer_Attaches_And_Is_Drivable()
    {
        var exe = Environment.GetEnvironmentVariable(ConsumerExeVar);
        if (string.IsNullOrWhiteSpace(exe))
        {
            output.WriteLine($"{ConsumerExeVar} is not set; skipping the packaged-consumer check.");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        await using var mcp = await rig.ConnectViaShimAsync(ct);

        var consumer = rig.Track(ManagedProcess.Start("consumer", exe));

        var attached = McpJson.Ok(await mcp.CallToolAsync("hub_wait_for_client",
            new Dictionary<string, object?> { ["appId"] = "consumer", ["timeoutMs"] = 90_000 },
            cancellationToken: ct));
        Assert.True(attached.Bool("connected") ?? false,
            $"the packaged consumer never attached.\n{consumer.Describe()}");

        var clientId = attached.Str("clientId")!;
        output.WriteLine($"packaged consumer attached as {clientId}");

        await mcp.CallToolAsync("hub_select_client",
            new Dictionary<string, object?> { ["clientId"] = clientId }, cancellationToken: ct);

        var hits = McpJson.Ok(await mcp.CallToolAsync("query_controls",
            new Dictionary<string, object?> { ["selector"] = "#ConsumerButton" }, cancellationToken: ct));
        Assert.Equal(1, hits.Int("count"));

        var invoked = McpJson.Ok(await mcp.CallToolAsync("automation_action",
            new Dictionary<string, object?> { ["selector"] = "#ConsumerButton", ["action"] = "Invoke" },
            cancellationToken: ct));
        Assert.True(invoked.Bool("ok") ?? false);

        var waited = McpJson.Ok(await mcp.CallToolAsync("wait_for", new Dictionary<string, object?>
        {
            ["selector"] = "#ConsumerLabel",
            ["propertyName"] = "Text",
            ["expected"] = "Presses: 1",
            ["timeoutMs"] = 10_000,
        }, cancellationToken: ct));
        Assert.True(waited.Bool("ok") ?? false,
            $"the packaged consumer's click had no observable effect: {waited}");

        consumer.Kill();
    }
}
