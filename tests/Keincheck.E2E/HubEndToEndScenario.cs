using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Keincheck.E2E;

/// <summary>
/// The end-to-end scenario: a real installed hub, a real demo app, driven through the
/// real stdio shim, exercising discovery, UI control, record/replay/export, the read-only
/// gate, and launch/restart — then asserting nothing crashed.
/// </summary>
/// <remarks>
/// <para>Deliberately ONE fact with numbered steps rather than a dozen ordered facts. The
/// phases genuinely depend on each other — replay only means something after a recording,
/// read-only has to be turned back off, restart has to follow a first connect — and xunit's
/// ordering hooks would buy worse failure messages than a labelled step in the log does.
/// Genuinely independent checks (the other transports, the remote leg, WPF) are separate
/// facts, because they are genuinely independent.</para>
/// </remarks>
[Collection(HubCollection.Name)]
public sealed class HubEndToEndScenario(HubRig rig, ITestOutputHelper output)
{
    private static readonly TimeSpan Overall = TimeSpan.FromMinutes(8);

    /// <summary>
    /// Client-side ceiling for a proxied tool call. Deliberately ABOVE the hub's own
    /// <c>HubOptions.InvokeTimeout</c> of 60s, so a slow client surfaces the hub's
    /// structured timeout error rather than our cancellation masking it.
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);

    [E2EFact]
    public async Task Hub_Drives_The_Demo_End_To_End()
    {
        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;

        await using var mcp = await rig.ConnectViaShimAsync(ct);

        // ---- 01 handshake ------------------------------------------------
        string hubVersion = "";
        await Step("01 handshake", () =>
        {
            Assert.Equal("Keincheck.Hub", mcp.ServerInfo.Name);
            Assert.False(string.IsNullOrWhiteSpace(mcp.ServerInfo.Version));
            hubVersion = mcp.ServerInfo.Version!;
            Log($"server {mcp.ServerInfo.Name} {hubVersion}");
            return Task.CompletedTask;
        });

        // ---- 02 the hub reports itself, with nothing attached ------------
        await Step("02 hub_status and hub_guide with no client", async () =>
        {
            var status = McpJson.Ok(await Call(mcp, "hub_status", null, ct));
            Assert.Equal(hubVersion, status.Str("hubVersion"));
            Assert.Equal(0, status.Int("clientCount"));
            Assert.Null(status.Str("activeClientId"));
            Assert.NotNull(status.Prop("protocolRange"));

            var guide = McpJson.Text(await Call(mcp, "hub_guide", null, ct));
            Assert.Contains("hub_select_client", guide, StringComparison.Ordinal);
            Assert.True(guide.Length > 500, "hub_guide returned a suspiciously short document.");

            var clients = McpJson.Ok(await Call(mcp, "hub_list_clients", null, ct));
            Assert.Empty(clients.EnumerateArray());
        });

        // ---- 03 the catalogue with no client is exactly the meta-tools ----
        await Step("03 tools/list is meta-only", async () =>
        {
            var names = (await mcp.ListToolsAsync(cancellationToken: ct)).Select(t => t.Name).ToArray();

            // Two separate claims, deliberately not one set-equality assertion.
            //
            // (a) Every meta-tool is still advertised — catches a drop or a rename.
            foreach (var expected in MetaTools.Names)
                Assert.Contains(expected, names);

            // (b) With nothing attached, NOTHING but meta-tools may appear. That is the
            //     real bug this step guards: a stale catalogue leaking a departed client's
            //     tools, so the model is offered controls that cannot be driven.
            var leaked = names.Where(n => !n.StartsWith("hub_", StringComparison.Ordinal)).ToArray();
            Assert.True(leaked.Length == 0,
                $"tools/list advertised non-meta tools with no client attached: {string.Join(", ", leaked)}");

            // Set equality would also fail a hub that legitimately GAINED a meta-tool,
            // which is not a regression — and it does, against any hub built from a
            // different commit than this working tree.
            Log($"{names.Length} meta-tools, no client tools");
        });

        // ---- 04 attach the demo ------------------------------------------
        var demo = rig.Track(ManagedProcess.Start("demo", DemoExe));
        string clientId = "";

        await Step("04 hub_wait_for_client sees the demo", async () =>
        {
            var waited = McpJson.Ok(await Call(mcp, "hub_wait_for_client",
                new() { ["appId"] = "demo", ["timeoutMs"] = 90_000 }, ct));

            Assert.True(waited.Bool("connected") ?? false,
                $"the demo never attached. {demo.Describe()}");

            clientId = waited.Str("clientId")!;

            // Shape, not the literal "demo#1": a local run against a store that already
            // remembers a demo can legitimately hand out a higher suffix. The stability of
            // the suffix across reconnects is what matters, and step 11 asserts that.
            Assert.Matches(@"^demo#\d+$", clientId);
            Log($"attached as {clientId}");
        });

        // ---- 05 discovery -------------------------------------------------
        await Step("05 list/status/select", async () =>
        {
            // hub_wait_for_client returns on REGISTER, and the client's ToolList arrives in
            // a separate frame immediately after — so there is a real window in which a
            // client is connected and advertising nothing. Wait it out rather than racing
            // it; a model doing wait-then-list hits the same window.
            await WaitForToolCatalogue(mcp, clientId, CoreTools.Count, ct);

            var clients = McpJson.Ok(await Call(mcp, "hub_list_clients", null, ct));
            var entry = Assert.Single(clients.EnumerateArray(), c => c.Str("clientId") == clientId);

            Assert.Equal("demo", entry.Str("appId"));
            Assert.True(entry.Bool("connected") ?? false);
            Assert.True(entry.Bool("ownsWindows") ?? false, "the demo owns a window and must say so.");
            Assert.Equal("pipe", entry.Str("transport"));
            Assert.Null(entry.Str("host"));
            Assert.True(entry.Bool("canLaunch") ?? false);
            Assert.False(entry.Bool("readOnly") ?? true);
            // >= rather than ==: a client that GAINS a tool is not a regression, and step 06
            // asserts every expected name individually, which is the claim that matters.
            Assert.True((entry.Int("toolCount") ?? 0) >= CoreTools.Count,
                $"expected at least {CoreTools.Count} client tools, got {entry.Int("toolCount")}.");
            Assert.EndsWith("Keincheck.Demo.exe", entry.Str("executablePath")!, StringComparison.OrdinalIgnoreCase);

            var status = McpJson.Ok(await Call(mcp, "hub_client_status", Args(clientId), ct));
            Assert.Equal(clientId, status.Str("clientId"));

            var selected = McpJson.Ok(await Call(mcp, "hub_select_client", Args(clientId), ct));
            Assert.Equal(clientId, selected.Str("activeClientId"));
        });

        // ---- 06 the catalogue now carries the demo's tools ----------------
        await Step("06 tools/list includes the client's tools", async () =>
        {
            var names = (await mcp.ListToolsAsync(cancellationToken: ct)).Select(t => t.Name).ToArray();

            foreach (var expected in CoreTools.Names)
                Assert.Contains(expected, names);
            foreach (var expected in MetaTools.Names)
                Assert.Contains(expected, names);

            // The live check on HubMcpServer.BuildToolList's shadow guard: a client tool
            // whose name collides with a hub meta-tool is dropped rather than advertised
            // twice, which would be an MCP protocol violation and a way to put
            // client-authored text into the model's description of hub_remote_issue.
            Assert.Equal(names.Length, names.Distinct().Count());
            Log($"{names.Length} tools advertised");
        });

        // ---- 07 drive the real UI ----------------------------------------
        await Step("07 drive the demo's UI", async () =>
        {
            var windows = McpJson.Ok(await Call(mcp, "list_windows", null, ct));
            var titles = windows.Array("windows").Select(w => w.Str("title")).ToArray();
            Assert.Contains("Keincheck Demo", titles);

            var hits = McpJson.Ok(await Call(mcp, "query_controls",
                new() { ["selector"] = "#CountButton" }, ct));
            Assert.Equal(1, hits.Int("count"));
            var button = hits.Array("controls").First();
            Assert.Equal("CountButton", button.Str("name"));

            Assert.Equal("Clicks: 0", await ReadText(mcp, "#CountLabel", ct));

            // The whole spine in one call: MCP -> hub -> named pipe -> client -> UI
            // dispatcher -> Avalonia automation peer -> the bound view model -> back.
            var invoked = McpJson.Ok(await Call(mcp, "automation_action",
                new() { ["selector"] = "#CountButton", ["action"] = "Invoke" }, ct));
            Assert.True(invoked.Bool("ok") ?? false);

            await WaitForText(mcp, "#CountLabel", "Clicks: 1", ct);

            // The click handler also writes StatusMessage, which is bound into #MessageLabel —
            // so this proves the handler ran, not merely that the counter binding fired.
            var message = McpJson.Ok(await Call(mcp, "get_text",
                new() { ["selector"] = "#MessageLabel" }, ct));
            Assert.Contains("Incremented to 1.", message.ToString(), StringComparison.Ordinal);

            var written = McpJson.Ok(await Call(mcp, "set_property",
                new() { ["selector"] = "#NameBox", ["propertyName"] = "Text", ["value"] = "ci-run" }, ct));
            Assert.True(written.Bool("ok") ?? false);
            Assert.Equal("ci-run", written.Str("newValue"));
            Assert.Equal("ci-run", await ReadText(mcp, "#NameBox", ct));

            // A DataContext is attached and identified. Deliberately NOT asserting the view
            // model's field values through here: get_data_context reports the type name
            // ("Keincheck.Demo.MainViewModel") rather than expanding the object, so a
            // property-value assertion would be testing the serializer's appetite, not the
            // binding.
            var context = McpJson.Ok(await Call(mcp, "get_data_context",
                new() { ["selector"] = "#NameBox" }, ct));
            Assert.True(context.Bool("hasDataContext") ?? false);
            Assert.Contains("MainViewModel", context.ToString(), StringComparison.Ordinal);

            // Synthetic keyboard input — a completely different path from set_property:
            // real TextInput events raised on the focused element rather than a property
            // write. Both must land.
            await Call(mcp, "set_focus", new() { ["selector"] = "#NameBox" }, ct);
            var typed = McpJson.Ok(await Call(mcp, "type_text",
                new() { ["selector"] = "#NameBox", ["text"] = "-typed" }, ct));
            Assert.True(typed.Bool("ok") ?? false, $"type_text failed: {typed}");
            Assert.Contains("typed", (await ReadText(mcp, "#NameBox", ct))!, StringComparison.Ordinal);

            var toggled = McpJson.Ok(await Call(mcp, "automation_action",
                new() { ["selector"] = "#SubscribeCheck", ["action"] = "Toggle" }, ct));
            Assert.True(toggled.Bool("ok") ?? false);

            // A sleeper of a check: a broken binding crashes nothing, so without this a
            // XAML regression in the sample would go unnoticed indefinitely.
            var bindingErrors = McpJson.Ok(await Call(mcp, "get_binding_errors", null, ct));
            var errorText = bindingErrors.ToString();
            Assert.DoesNotContain("BindingError", errorText, StringComparison.OrdinalIgnoreCase);

            // The accessibility tree names nodes by their ACCESSIBLE name, not x:Name — a
            // Button's is its Content ("Increment"), while a TextBox with no accessible
            // name falls back to x:Name ("NameBox"). Asserting on x:Name throughout would
            // quietly be asserting the wrong contract.
            var semantic = McpJson.Ok(await Call(mcp, "get_semantic_tree",
                new() { ["interactiveOnly"] = true }, ct));
            var tree = semantic.ToString();
            Assert.Contains("\"role\":\"Button\",\"name\":\"Increment\"", tree, StringComparison.Ordinal);
            Assert.Contains("\"role\":\"Edit\",\"name\":\"NameBox\"", tree, StringComparison.Ordinal);
            Assert.Contains("\"role\":\"CheckBox\"", tree, StringComparison.Ordinal);

            var idle = McpJson.Ok(await Call(mcp, "wait_for_idle",
                new() { ["timeoutMs"] = 5000 }, ct));
            Assert.True(idle.Bool("idle") ?? false);
        });

        // ---- 08 the runner actually rendered something --------------------
        await Step("08 screenshots are real PNGs", async () =>
        {
            var shot = await Call(mcp, "screenshot_window", null, ct);
            Assert.False(shot.IsError ?? false, McpJson.Text(shot));

            var image = McpJson.Image(shot);
            Assert.NotNull(image);
            Assert.Equal("image/png", image!.MimeType);

            // Data holds base64-encoded UTF-8 bytes; DecodedData is the actual PNG.
            var bytes = image.DecodedData.ToArray();
            McpJson.Save("screenshot_window.png", bytes);
            AssertPng(bytes, "screenshot_window");

            var marked = await Call(mcp, "screenshot_marked", null, ct);
            var markedImage = McpJson.Image(marked);
            Assert.NotNull(markedImage);

            var markedBytes = markedImage!.DecodedData.ToArray();
            McpJson.Save("screenshot_marked.png", markedBytes);
            AssertPng(markedBytes, "screenshot_marked");

            // The legend is what makes a set-of-marks screenshot actionable: each number
            // must map back to a real, addressable handle. Marks carry accessible names,
            // so the button appears as its Content.
            var legend = McpJson.Ok(marked);
            Assert.True((legend.Int("count") ?? 0) > 0, "the set-of-marks legend was empty.");
            var mark = Assert.Single(legend.Array("marks"), m => m.Str("name") == "Increment");
            Assert.False(string.IsNullOrWhiteSpace(mark.Str("id")), "a mark must name a usable handle.");
        });

        // ---- 09 record, replay, export -----------------------------------
        await Step("09 record -> replay -> export", async () =>
        {
            var started = McpJson.Ok(await Call(mcp, "hub_record_start",
                new() { ["name"] = "ci_increment" }, ct));
            Assert.True(started.Bool("recording") ?? false);

            // Selectors, never handles: a handle is registry-scoped and would not survive
            // a client restart, which would make the exported scenario worthless as an
            // artifact — the very thing hub_export_test exists to produce.
            await Call(mcp, "query_controls", new() { ["selector"] = "#CountButton" }, ct);
            await Call(mcp, "automation_action",
                new() { ["selector"] = "#CountButton", ["action"] = "Invoke" }, ct);

            var mid = McpJson.Ok(await Call(mcp, "hub_record_status", null, ct));
            Assert.True(mid.Bool("recording") ?? false);
            Assert.Equal(2, mid.Int("steps"));

            var stopped = McpJson.Ok(await Call(mcp, "hub_record_stop", null, ct));
            Assert.False(stopped.Bool("recording") ?? true);

            // 'steps' is a COUNT here (HubRecorder.Stop returns an int), not the buffer.
            // Exactly 2: the meta-tools called in between are routed before the proxy and
            // must never be captured. A larger number would mean the recorder is eating its
            // own control calls.
            Assert.Equal(2, stopped.Int("steps"));

            await WaitForText(mcp, "#CountLabel", "Clicks: 2", ct);

            var replayed = McpJson.Ok(await Call(mcp, "hub_replay",
                new() { ["stopOnError"] = true, ["delayMs"] = 100 }, ct));
            Assert.Equal(2, replayed.Int("replayed"));
            Assert.Equal(2, replayed.Int("ok"));
            Assert.Equal(0, replayed.Int("failed"));
            Assert.Equal(0, replayed.Int("skipped"));

            // The assertion that makes replay mean something: the counter moved again,
            // so the steps were genuinely re-issued to the UI rather than merely
            // reported as successful.
            await WaitForText(mcp, "#CountLabel", "Clicks: 3", ct);

            var json = McpJson.Ok(await Call(mcp, "hub_export_test",
                new() { ["format"] = "json" }, ct));
            Assert.Equal("json", json.Str("format"));
            var scenario = json.Prop("scenario")!.Value;
            Assert.Equal("ci_increment", scenario.Str("name"));
            var steps = scenario.Array("steps").ToArray();
            Assert.Equal(2, steps.Length);
            Assert.Equal("query_controls", steps[0].Str("tool"));
            Assert.Equal("automation_action", steps[1].Str("tool"));
            Assert.All(steps, s => Assert.Equal(clientId, s.Str("clientId")));
            McpJson.Save("exported-scenario.json", scenario.ToString());

            var csharp = McpJson.Ok(await Call(mcp, "hub_export_test",
                new() { ["format"] = "csharp" }, ct));
            var code = csharp.Str("code")!;
            Assert.Contains("[Fact]", code, StringComparison.Ordinal);
            Assert.Contains("InvokeOnClientAsync", code, StringComparison.Ordinal);
            Assert.Contains("ci_increment", code, StringComparison.Ordinal);
            McpJson.Save("exported-test.cs", code);
        });

        // ---- 10 the read-only gate ---------------------------------------
        await Step("10 read-only refuses writes but not reads", async () =>
        {
            var on = McpJson.Ok(await Call(mcp, "hub_set_readonly",
                new() { ["clientId"] = clientId, ["readOnly"] = true }, ct));
            Assert.True(on.Bool("readOnly") ?? false);
            Assert.True(on.Bool("remembered") ?? false);

            try
            {
                var refusedInvoke = McpJson.Refused(await Call(mcp, "automation_action",
                    new() { ["selector"] = "#CountButton", ["action"] = "Invoke" }, ct));
                Assert.Contains("read-only", refusedInvoke, StringComparison.OrdinalIgnoreCase);

                var refusedWrite = McpJson.Refused(await Call(mcp, "set_property",
                    new() { ["selector"] = "#NameBox", ["propertyName"] = "Text", ["value"] = "nope" }, ct));
                Assert.Contains("read-only", refusedWrite, StringComparison.OrdinalIgnoreCase);

                // The half that matters: a blanket block would pass the assertions above.
                // Reads still working is what proves IsReadOnlyTool is classifying rather
                // than just closing the door.
                Assert.Equal(1, McpJson.Ok(await Call(mcp, "query_controls",
                    new() { ["selector"] = "#CountButton" }, ct)).Int("count"));
                Assert.Equal("Clicks: 3", await ReadText(mcp, "#CountLabel", ct));

                var status = McpJson.Ok(await Call(mcp, "hub_client_status", Args(clientId), ct));
                Assert.True(status.Bool("readOnly") ?? false);
            }
            finally
            {
                // Mandatory, not tidiness: hub_set_readonly persists through
                // KnownClientStore.SetReadOnly into %APPDATA%\Keincheck\known-clients.json,
                // so leaving it set would cripple this app on the developer's machine for
                // every future session.
                var off = McpJson.Ok(await Call(mcp, "hub_set_readonly",
                    new() { ["clientId"] = clientId, ["readOnly"] = false }, ct));
                Assert.False(off.Bool("readOnly") ?? true);
            }

            var allowed = McpJson.Ok(await Call(mcp, "automation_action",
                new() { ["selector"] = "#CountButton", ["action"] = "Invoke" }, ct));
            Assert.True(allowed.Bool("ok") ?? false);
            await WaitForText(mcp, "#CountLabel", "Clicks: 4", ct);
        });

        // ---- 11 launch and restart ---------------------------------------
        await Step("11 known clients, launch, restart", async () =>
        {
            var known = McpJson.Ok(await Call(mcp, "hub_list_known_clients", null, ct));
            var profile = Assert.Single(known.EnumerateArray(), c => c.Str("clientId") == clientId);
            Assert.EndsWith("Keincheck.Demo.exe", profile.Str("executablePath")!, StringComparison.OrdinalIgnoreCase);
            Assert.True(profile.Bool("canLaunch") ?? false);

            // Take the demo away from the hub and prove the hub can bring it back.
            demo.Kill();

            var relaunched = McpJson.Ok(await Call(mcp, "hub_launch_client", Args(clientId), ct));
            var pid = relaunched.Int("processId");
            Assert.NotNull(pid);
            // The broker starts these with UseShellExecute=true, so they are neither our
            // children nor capturable. Tracking the pid is the only way they get cleaned up.
            rig.TrackLaunchedPid("demo-relaunched", pid!.Value);

            var back = McpJson.Ok(await Call(mcp, "hub_wait_for_client",
                new() { ["clientId"] = clientId, ["timeoutMs"] = 90_000 }, ct));
            Assert.True(back.Bool("connected") ?? false);

            // The live regression guard for "let a known app reclaim its own id": before
            // that fix the first reconnect after a restart was handed #2 while #1 sat in
            // the list as a permanent ghost.
            Assert.Equal(clientId, back.Str("clientId"));

            // A genuinely fresh instance — the counter is back to zero.
            Assert.Equal("Clicks: 0", await ReadText(mcp, "#CountLabel", ct));

            var restarted = McpJson.Ok(await Call(mcp, "hub_restart_client", Args(clientId), ct));
            var restartPid = restarted.Int("processId");
            Assert.NotNull(restartPid);
            Assert.NotEqual(pid.Value, restartPid!.Value);
            rig.TrackLaunchedPid("demo-restarted", restartPid.Value);

            var afterRestart = McpJson.Ok(await Call(mcp, "hub_wait_for_client",
                new() { ["clientId"] = clientId, ["timeoutMs"] = 90_000 }, ct));
            Assert.True(afterRestart.Bool("connected") ?? false);
            Assert.Equal(clientId, afterRestart.Str("clientId"));
        });

        // ---- 12 the verdict ----------------------------------------------
        await Step("12 the hub is unchanged and still serving", async () =>
        {
            var status = McpJson.Ok(await Call(mcp, "hub_status", null, ct));

            // If the Velopack auto-updater ever swapped the binaries mid-run this is where
            // it shows, as one clear assertion instead of a baffling pipe drop.
            Assert.Equal(hubVersion, status.Str("hubVersion"));
            Assert.True((status.Int("clientCount") ?? 0) >= 1);

            Assert.False(rig.Hub.HasExited, $"the hub died during the scenario.\n{rig.Hub.Describe()}");
        });
    }

    // ---- helpers ----------------------------------------------------------

    private static string DemoExe =>
        Environment.GetEnvironmentVariable(E2EEnvironment.DemoExeVar)
        ?? throw new InvalidOperationException($"{E2EEnvironment.DemoExeVar} is not set.");

    private static Dictionary<string, object?> Args(string clientId) => new() { ["clientId"] = clientId };

    private static async Task<CallToolResult> Call(
        McpClient mcp, string tool, Dictionary<string, object?>? args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        return await mcp.CallToolAsync(tool, args, cancellationToken: timeout.Token);
    }

    /// <summary>
    /// Polls <c>hub_client_status</c> until the client has published its tool catalogue.
    /// Registration and the tool list are two separate frames, so "connected" briefly means
    /// "connected, advertising nothing".
    /// </summary>
    private static async Task WaitForToolCatalogue(
        McpClient mcp, string clientId, int expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        int seen;
        do
        {
            var status = McpJson.Ok(await Call(mcp, "hub_client_status", Args(clientId), ct));
            seen = status.Int("toolCount") ?? 0;
            if (seen >= expected)
                return;
            await Task.Delay(100, ct);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail($"'{clientId}' never advertised {expected} tools (last saw {seen}).");
    }

    private static async Task<string?> ReadText(McpClient mcp, string selector, CancellationToken ct)
    {
        var result = McpJson.Ok(await Call(mcp, "get_property",
            new() { ["selector"] = selector, ["propertyName"] = "Text" }, ct));
        return result.Str("value") ?? result.ToString();
    }

    /// <summary>
    /// Polls inside the client rather than sleeping here. Every post-mutation assertion
    /// goes through this or <c>wait_for_idle</c>, which is what keeps the suite free of
    /// arbitrary delays — and therefore free of the flakiness that would justify retries.
    /// </summary>
    private static async Task WaitForText(McpClient mcp, string selector, string expected, CancellationToken ct)
    {
        var waited = McpJson.Ok(await Call(mcp, "wait_for", new()
        {
            ["selector"] = selector,
            ["propertyName"] = "Text",
            ["expected"] = expected,
            ["timeoutMs"] = 10_000,
        }, ct));

        Assert.True(waited.Bool("ok") ?? false,
            $"'{selector}'.Text never became '{expected}': {waited}");
    }

    private static void AssertPng(byte[] bytes, string what)
    {
        Assert.True(bytes.Length > 8, $"{what}: no image data.");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        // A degenerate render (blank surface, 1x1) compresses to almost nothing. The real
        // captures recorded in docs/remote-design.md are ~28 KB, so a few KB is a floor
        // that catches "the runner drew nothing" without being brittle about content.
        Assert.True(bytes.Length > 4096,
            $"{what}: only {bytes.Length} bytes — the runner probably rendered a blank surface.");
    }

    private async Task Step(string label, Func<Task> body)
    {
        var sw = Stopwatch.StartNew();
        Log($"--> {label}");
        try
        {
            await body();
            Log($"<-- {label} ok ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Log($"<-- {label} FAILED ({sw.ElapsedMilliseconds} ms): {ex.Message}");
            throw;
        }
    }

    private void Log(string message)
    {
        output.WriteLine(message);
        rig.Hub.Note(message);
    }
}
