using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Keincheck.E2E;

/// <summary>
/// The multi-agent rules against a real hub, real shim processes and real app instances —
/// with the emphasis on the paths that <i>fail</i>: contention, refusal, recovery from an
/// agent that vanished, and requests the hub must reject before it starts anything.
/// </summary>
/// <remarks>
/// <para>
/// The worktree case is modelled with directory junctions rather than copies. A worktree
/// build differs from its sibling only in <i>path</i> — same executable name, different
/// directory — and a junction reproduces exactly that for no bytes and no wait. It also
/// keeps the launch-path guard genuinely in play: the hub compares file names, so a junction
/// exercises the real comparison instead of side-stepping it by reusing one path.
/// </para>
/// <para>
/// Every fact leaves the shared hub as it found it — claims released, instances it started
/// killed and confirmed gone — and does so in a <c>finally</c>, because a fact that fails
/// half-way must not bequeath its running apps to the next one and bury the real cause under
/// a cascade of unrelated failures.
/// </para>
/// </remarks>
[Collection(HubCollection.Name)]
public sealed class WorktreeAndContentionE2ETests(HubRig rig, ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Overall = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);

    private readonly List<string> _junctions = [];

    private static string DemoExe =>
        Environment.GetEnvironmentVariable(E2EEnvironment.DemoExeVar)
        ?? throw new InvalidOperationException($"{E2EEnvironment.DemoExeVar} is not set.");

    private void Log(string line) => output.WriteLine($"    {line}");

    // ---- fixtures ---------------------------------------------------------

    /// <summary>
    /// Creates a junction that stands in for another worktree's build directory, and returns
    /// the demo executable's path inside it — a different directory, the same file name.
    /// </summary>
    private string WorktreeExe(string label)
    {
        var target = Path.GetDirectoryName(DemoExe)!;
        // Deliberately under the local temp root: the real build output lives in a synced
        // folder, and materialising copies there would hand the sync client a gigabyte of
        // duplicated native binaries.
        var link = Path.Combine(Path.GetTempPath(), $"keincheck-worktree-{label}-{Guid.NewGuid():N}");

        // mklink /J rather than Directory.CreateSymbolicLink: a junction needs no elevation
        // and no developer mode, so this runs on a stock machine and on CI alike.
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0,
            $"could not create a junction for the {label} worktree: {proc.StandardError.ReadToEnd()}");

        _junctions.Add(link);
        var exe = Path.Combine(link, Path.GetFileName(DemoExe));
        Assert.True(File.Exists(exe), $"the {label} worktree junction does not expose the demo.");
        return exe;
    }

    public void Dispose()
    {
        foreach (var link in _junctions)
        {
            // Delete the reparse point ONLY. A recursive delete through a junction would
            // reach into the real build output and erase it.
            try { Directory.Delete(link, recursive: false); } catch { /* best effort */ }
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task<CallToolResult> Call(
        McpClient mcp, string tool, Dictionary<string, object?>? args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        return await mcp.CallToolAsync(tool, args, cancellationToken: timeout.Token);
    }

    private static async Task<JsonElement> Ok(
        McpClient mcp, string tool, Dictionary<string, object?>? args, CancellationToken ct)
        => McpJson.Ok(await Call(mcp, tool, args, ct));

    private static Dictionary<string, object?> Drive(string? clientId = null) => clientId is null
        ? new Dictionary<string, object?> { ["selector"] = "#CountButton", ["action"] = "Invoke" }
        : new Dictionary<string, object?>
        {
            ["tool"] = "automation_action",
            ["client"] = clientId,
            ["args"] = new Dictionary<string, object?>
            {
                ["selector"] = "#CountButton",
                ["action"] = "Invoke",
            },
        };

    /// <summary>Drives an app the agent has NOT selected, targeting it for this one call.</summary>
    private static Task<CallToolResult> DriveOther(McpClient mcp, string clientId, CancellationToken ct)
        => Call(mcp, "hub_call_tool", Drive(clientId), ct);

    /// <summary>Starts an instance from <paramref name="exePath"/> and waits for exactly it.</summary>
    private async Task<string> LaunchAndWait(
        McpClient mcp, string exePath, List<string> started, CancellationToken ct)
    {
        var launched = await Ok(mcp, "hub_launch_client",
            new Dictionary<string, object?> { ["clientId"] = "demo", ["exePath"] = exePath }, ct);

        var launchId = launched.Str("launchId");
        Assert.False(string.IsNullOrWhiteSpace(launchId), "hub_launch_client returned no launchId.");
        rig.TrackLaunchedPid("worktree-demo", launched.Int("processId")!.Value);

        // By launchId, never by appId: with sibling instances up, an appId wait may hand back
        // somebody else's build, which is the failure this whole mechanism exists to prevent.
        var waited = await Ok(mcp, "hub_wait_for_client",
            new Dictionary<string, object?> { ["launchId"] = launchId, ["timeoutMs"] = 90_000 }, ct);

        Assert.True(waited.Bool("connected") ?? false, $"the instance from {exePath} never attached.");
        var clientId = waited.Str("clientId")!;
        started.Add(clientId);
        return clientId;
    }

    /// <summary>The <c>hub_list_clients</c> row for one instance, as that agent sees it.</summary>
    private static async Task<JsonElement> Row(McpClient mcp, string clientId, CancellationToken ct)
    {
        var clients = await Ok(mcp, "hub_list_clients", null, ct);
        return Assert.Single(clients.EnumerateArray(), c => c.Str("clientId") == clientId);
    }

    private static async Task<int> DemoCount(McpClient mcp, CancellationToken ct) =>
        (await Ok(mcp, "hub_list_clients", null, ct)).EnumerateArray().Count(c => c.Str("appId") == "demo");

    /// <summary>Runs a fact body and retires whatever it started, even if it threw.</summary>
    private async Task WithCleanup(McpClient mcp, CancellationToken ct, Func<List<string>, Task> body)
    {
        var started = new List<string>();
        try
        {
            await body(started);
        }
        finally
        {
            try
            {
                await Call(mcp, "hub_release_client", null, ct);
                await RetireAsync(mcp, ct, started.ToArray());
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Kills the instances a fact started and waits until the hub agrees they are gone, so
    /// the next fact in the shared collection starts from a clean registry.
    /// </summary>
    private async Task RetireAsync(McpClient mcp, CancellationToken ct, params string[] clientIds)
    {
        if (clientIds.Length == 0)
            return;

        foreach (var id in clientIds)
        {
            try
            {
                var clients = await Ok(mcp, "hub_list_clients", null, ct);
                var row = clients.EnumerateArray().FirstOrDefault(c => c.Str("clientId") == id);
                if (row.ValueKind != JsonValueKind.Object)
                    continue;
                if (row.Int("processId") is int pid && pid > 0)
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { /* already gone */ }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var live = (await Ok(mcp, "hub_list_clients", null, ct))
                .EnumerateArray().Select(c => c.Str("clientId")).ToHashSet();
            if (!clientIds.Any(live.Contains))
                return;
            await Task.Delay(200, ct);
        }

        Log($"WARNING: {string.Join(", ", clientIds)} still listed after teardown.");
    }

    private static string ErrorCode(CallToolResult result) =>
        result.StructuredContent is { } sc && sc.TryGetProperty("error", out var e)
            ? e.GetString() ?? ""
            : "";

    private static string OwnerOf(CallToolResult result) =>
        result.StructuredContent!.Value.GetProperty("owner").GetProperty("session").GetString()!;

    // =====================================================================

    [E2EFact]
    public async Task Each_Agent_Drives_The_Worktree_Build_It_Launched()
    {
        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;

        await using var agentA = await rig.ConnectViaShimAsync(ct);
        await using var agentB = await rig.ConnectViaShimAsync(ct);

        await WithCleanup(agentA, ct, async started =>
        {
            var labelA = (await Ok(agentA, "hub_status", null, ct)).Str("agent")!;
            var labelB = (await Ok(agentB, "hub_status", null, ct)).Str("agent")!;

            // Two agents, two worktrees, the same app id in both.
            var idA = await LaunchAndWait(agentA, WorktreeExe("a"), started, ct);
            var idB = await LaunchAndWait(agentB, WorktreeExe("b"), started, ct);

            Assert.NotEqual(idA, idB);
            Log($"{labelA} -> {idA}, {labelB} -> {idB}");

            // Each agent's instance is selected AND claimed for it, with no explicit step.
            Assert.Equal(idA, (await Ok(agentA, "hub_status", null, ct)).Str("activeClientId"));
            Assert.Equal(idB, (await Ok(agentB, "hub_status", null, ct)).Str("activeClientId"));

            var mineToA = await Row(agentA, idA, ct);
            Assert.True(mineToA.Bool("mine") ?? false, "an agent must own the instance it launched.");
            Assert.Equal(labelA, mineToA.Str("claimedBy"));
            Assert.Equal(labelA, mineToA.Str("launchedBy"));

            // ...and the sibling is visibly somebody else's, BEFORE trying anything with it.
            var theirsToA = await Row(agentA, idB, ct);
            Assert.False(theirsToA.Bool("mine") ?? true);
            Assert.Equal(labelB, theirsToA.Str("claimedBy"));

            // Each drives its own build without contending.
            Assert.False((await Call(agentA, "automation_action", Drive(), ct)).IsError ?? false);
            Assert.False((await Call(agentB, "automation_action", Drive(), ct)).IsError ?? false);

            // ...but neither may drive the other's.
            var trespass = await DriveOther(agentA, idB, ct);
            Assert.True(trespass.IsError ?? false, "an agent drove a build belonging to another agent.");
            Assert.Equal("client_claimed", ErrorCode(trespass));
            Assert.Equal(labelB, OwnerOf(trespass));

            // The refusal has to be actionable, not just a "no".
            var free = trespass.StructuredContent!.Value.GetProperty("recovery")
                .EnumerateArray().Select(r => r.GetProperty("tool").GetString()).ToArray();
            Assert.Contains("hub_launch_client", free);

            await Ok(agentB, "hub_release_client", null, ct);
        });
    }

    [E2EFact]
    public async Task An_Agent_That_Vanishes_Frees_The_App_It_Held()
    {
        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;

        var agentA = await rig.ConnectViaShimAsync(ct);
        await using var agentB = await rig.ConnectViaShimAsync(ct);

        await WithCleanup(agentB, ct, async started =>
        {
            var clientId = await LaunchAndWait(agentA, WorktreeExe("vanish"), started, ct);

            // A is driving it, so B cannot.
            Assert.Equal("client_claimed", ErrorCode(await DriveOther(agentB, clientId, ct)));

            // A goes away — the editor window closed, the process died, the shim exited. B
            // must recover on its own, with no operator and no force: an agent that is gone
            // holding an app hostage until a timeout is the failure this guards against.
            await agentA.DisposeAsync();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            CallToolResult? afterVanish = null;
            while (DateTime.UtcNow < deadline)
            {
                afterVanish = await DriveOther(agentB, clientId, ct);
                if (!(afterVanish.IsError ?? false))
                    break;
                await Task.Delay(250, ct);
            }

            Assert.False(afterVanish!.IsError ?? false,
                $"the claim outlived the agent that held it: {McpJson.Text(afterVanish)}");
            Log("claim released when the agent's session ended");
        });
    }

    [E2EFact]
    public async Task Restarting_An_App_Another_Agent_Drives_Is_Refused_Unless_Forced()
    {
        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;

        await using var agentA = await rig.ConnectViaShimAsync(ct);
        await using var agentB = await rig.ConnectViaShimAsync(ct);

        await WithCleanup(agentB, ct, async started =>
        {
            var labelA = (await Ok(agentA, "hub_status", null, ct)).Str("agent")!;
            var clientId = await LaunchAndWait(agentA, WorktreeExe("restart"), started, ct);

            // Restart kills the process, so doing it to an app somebody else is driving is a
            // strictly worse version of writing to it.
            var refused = await Call(agentB, "hub_restart_client",
                new Dictionary<string, object?> { ["clientId"] = clientId }, ct);

            Assert.True(refused.IsError ?? false, "an agent restarted an app another agent was driving.");
            Assert.Equal("client_claimed", ErrorCode(refused));
            Assert.Equal(labelA, OwnerOf(refused));

            // The override exists for the case where that agent is genuinely gone — and it
            // has to hand over ownership too, or forcing kills their app and still leaves you
            // unable to drive the replacement.
            var forced = await Ok(agentB, "hub_restart_client",
                new Dictionary<string, object?> { ["clientId"] = clientId, ["force"] = true }, ct);
            rig.TrackLaunchedPid("restarted-demo", forced.Int("processId")!.Value);

            var back = await Ok(agentB, "hub_wait_for_client",
                new Dictionary<string, object?> { ["launchId"] = forced.Str("launchId"), ["timeoutMs"] = 90_000 }, ct);
            Assert.True(back.Bool("connected") ?? false, "the forced restart never came back.");

            // The relaunched instance keeps the id, so a recording still replays against it.
            Assert.Equal(clientId, back.Str("clientId"));
            Assert.True(back.Bool("mine") ?? false, "forcing a restart must transfer the claim.");

            Assert.False((await DriveOther(agentB, clientId, ct)).IsError ?? false,
                "the agent that forced the restart could not drive the replacement.");

            // ...and the agent it was taken from is now the one locked out.
            Assert.Equal("client_claimed", ErrorCode(await DriveOther(agentA, clientId, ct)));
        });
    }

    [E2EFact]
    public async Task A_Bare_AppId_Restart_Is_Refused_While_Several_Instances_Run()
    {
        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;

        await using var agent = await rig.ConnectViaShimAsync(ct);

        await WithCleanup(agent, ct, async started =>
        {
            var first = await LaunchAndWait(agent, WorktreeExe("amb1"), started, ct);
            var second = await LaunchAndWait(agent, WorktreeExe("amb2"), started, ct);
            Assert.NotEqual(first, second);

            var before = await DemoCount(agent, ct);

            // Silently starting ANOTHER copy — the old behaviour — leaves the agent convinced
            // it restarted the app it was driving while it now talks to something else.
            var ambiguous = await Call(agent, "hub_restart_client",
                new Dictionary<string, object?> { ["clientId"] = "demo" }, ct);

            Assert.True(ambiguous.IsError ?? false, "a bare appId restart should not pick an instance for you.");
            var text = McpJson.Text(ambiguous);
            Assert.Contains(first, text, StringComparison.Ordinal);
            Assert.Contains(second, text, StringComparison.Ordinal);
            Log($"ambiguity reported: {text}");

            // Nothing was started behind that error.
            Assert.Equal(before, await DemoCount(agent, ct));

            // Naming the instance is accepted, because then there is nothing to guess.
            var named = await Ok(agent, "hub_restart_client",
                new Dictionary<string, object?> { ["clientId"] = first }, ct);
            rig.TrackLaunchedPid("disambiguated-demo", named.Int("processId")!.Value);
            Assert.True((await Ok(agent, "hub_wait_for_client",
                new Dictionary<string, object?> { ["launchId"] = named.Str("launchId"), ["timeoutMs"] = 90_000 }, ct))
                .Bool("connected") ?? false);
        });
    }

    [E2EFact]
    public async Task The_Hub_Refuses_Launch_And_Claim_Requests_It_Cannot_Honour()
    {
        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;

        await using var agent = await rig.ConnectViaShimAsync(ct);

        await WithCleanup(agent, ct, async started =>
        {
            // A registered instance, so a launch profile definitely exists for the guard to
            // compare against rather than depending on what earlier facts left behind.
            await LaunchAndWait(agent, WorktreeExe("guard"), started, ct);
            var before = await DemoCount(agent, ct);

            // (a) a path that is not there at all
            var missing = Path.Combine(
                Path.GetTempPath(), $"keincheck-never-built-{Guid.NewGuid():N}", "Keincheck.Demo.exe");
            var notThere = await Call(agent, "hub_launch_client",
                new Dictionary<string, object?> { ["clientId"] = "demo", ["exePath"] = missing }, ct);
            Assert.True(notThere.IsError ?? false, "the hub tried to launch a path that does not exist.");

            // (b) a real file whose NAME differs — the footgun guard. The same binary in
            //     another directory is the worktree case and is fine; a different name usually
            //     means the wrong application is about to be started under a familiar id.
            var oddDir = Path.Combine(Path.GetTempPath(), $"keincheck-guard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(oddDir);
            var oddName = Path.Combine(oddDir, "not-the-demo.exe");
            await File.WriteAllTextAsync(oddName, "placeholder", ct);
            try
            {
                var wrongName = await Call(agent, "hub_launch_client",
                    new Dictionary<string, object?> { ["clientId"] = "demo", ["exePath"] = oddName }, ct);

                Assert.True(wrongName.IsError ?? false, "the hub launched a differently-named executable.");
                Assert.Contains("allowDifferentExecutable", McpJson.Text(wrongName), StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(oddDir, recursive: true); } catch { /* best effort */ }
            }

            // Neither refusal may have started anything.
            Assert.Equal(before, await DemoCount(agent, ct));

            // (c) releasing an app you do not hold is an error, not a silent no-op — otherwise
            //     an agent believes it handed something over when it never did.
            Assert.True((await Call(agent, "hub_release_client",
                new Dictionary<string, object?> { ["clientId"] = "demo#999" }, ct)).IsError ?? false);

            // (d) claiming something the hub has never heard of
            var unknown = await Call(agent, "hub_claim_client",
                new Dictionary<string, object?> { ["clientId"] = "no-such-app#1" }, ct);
            Assert.True(unknown.IsError ?? false);
            Assert.Equal("client_unavailable", ErrorCode(unknown));
        });
    }

    [E2EFact]
    public async Task Two_Agents_Record_Side_By_Side_Without_Mixing()
    {
        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;

        await using var agentA = await rig.ConnectViaShimAsync(ct);
        await using var agentB = await rig.ConnectViaShimAsync(ct);

        await WithCleanup(agentA, ct, async started =>
        {
            var idA = await LaunchAndWait(agentA, WorktreeExe("reca"), started, ct);
            var idB = await LaunchAndWait(agentB, WorktreeExe("recb"), started, ct);

            await Ok(agentA, "hub_record_start", new Dictionary<string, object?> { ["name"] = "flow-a" }, ct);
            await Ok(agentB, "hub_record_start", new Dictionary<string, object?> { ["name"] = "flow-b" }, ct);

            // Interleave deliberately: one shared buffer would splice these together, and B
            // starting a recording would have wiped whatever A had captured so far.
            await Ok(agentA, "query_controls", new Dictionary<string, object?> { ["selector"] = "Button" }, ct);
            await Ok(agentB, "query_controls", new Dictionary<string, object?> { ["selector"] = "Button" }, ct);
            await Ok(agentA, "query_controls", new Dictionary<string, object?> { ["selector"] = "Button" }, ct);

            var statusA = await Ok(agentA, "hub_record_status", null, ct);
            var statusB = await Ok(agentB, "hub_record_status", null, ct);

            Assert.Equal("flow-a", statusA.Str("name"));
            Assert.Equal(2, statusA.Int("steps"));
            Assert.Equal("flow-b", statusB.Str("name"));
            Assert.Equal(1, statusB.Int("steps"));

            // And each export names only its own instance.
            var exportA = await Ok(agentA, "hub_export_test",
                new Dictionary<string, object?> { ["format"] = "json" }, ct);
            var stepsA = exportA.Prop("scenario")!.Value.Array("steps").ToArray();
            Assert.Equal(2, stepsA.Length);
            Assert.All(stepsA, s => Assert.Equal(idA, s.Str("clientId")));

            await Ok(agentA, "hub_record_stop", null, ct);
            await Ok(agentB, "hub_record_stop", null, ct);
            await Ok(agentB, "hub_release_client", null, ct);
        });
    }
}
