using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Keincheck.E2E;

/// <summary>
/// Depth coverage for the <b>WPF adapter</b>, against a real hub and the real WPF demo.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WpfDemoSmokeTests"/> proves the binding reaches the hub at all. This suite
/// covers the parts that are genuinely toolkit-specific and were previously only ever
/// exercised on Avalonia: rendering, synthetic keyboard and pointer input, the
/// no-automation-peer fallback, WPF's dispatcher idle semantics, and the selector grammar's
/// documented behaviour on a framework with no style classes.
/// </para>
/// <para>
/// Deliberately <i>not</i> covered here: sessions, write-claims, launch affinity and
/// per-agent recording. That machinery lives entirely in the hub and never touches an
/// adapter, so re-running it with a WPF app on the far end would execute identical code for
/// no additional coverage.
/// </para>
/// <para>
/// Each fact attaches its own instance and kills it afterwards, so the facts are order
/// independent and leave the shared hub as they found it.
/// </para>
/// </remarks>
[Collection(HubCollection.Name)]
public sealed class WpfAdapterE2ETests(HubRig rig, ITestOutputHelper output)
{
    private static readonly TimeSpan Overall = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);

    private static string? WpfExe => Environment.GetEnvironmentVariable(E2EEnvironment.WpfDemoExeVar);

    private void Log(string line) => output.WriteLine($"    {line}");

    // ---- harness ----------------------------------------------------------

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

    /// <summary>
    /// Attaches a WPF demo instance, selects it, runs <paramref name="body"/>, and retires the
    /// instance afterwards even on failure — the collection shares one hub, so a fact that
    /// throws must not leave its app attached for the next one to trip over.
    /// </summary>
    private async Task WithWpfAsync(Func<McpClient, string, CancellationToken, Task> body)
    {
        var exe = WpfExe;
        if (string.IsNullOrWhiteSpace(exe))
        {
            Log($"{E2EEnvironment.WpfDemoExeVar} is not set; skipping the WPF adapter coverage.");
            return;
        }

        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;

        await using var mcp = await rig.ConnectViaShimAsync(ct);
        var wpf = rig.Track(ManagedProcess.Start("wpfdemo", exe));

        try
        {
            var attached = await Ok(mcp, "hub_wait_for_client",
                new Dictionary<string, object?> { ["appId"] = "wpfdemo", ["timeoutMs"] = 90_000 }, ct);
            Assert.True(attached.Bool("connected") ?? false,
                $"the WPF demo never attached.\n{wpf.Describe()}");

            var clientId = attached.Str("clientId")!;
            await Ok(mcp, "hub_select_client",
                new Dictionary<string, object?> { ["clientId"] = clientId }, ct);

            // Registration and the tool catalogue are separate frames, so wait for the tools
            // before driving anything.
            await WaitForTools(mcp, clientId, ct);

            await body(mcp, clientId, ct);
        }
        finally
        {
            try { wpf.Kill(); } catch { /* already gone */ }
        }
    }

    private static async Task WaitForTools(McpClient mcp, string clientId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var status = await Ok(mcp, "hub_client_status",
                new Dictionary<string, object?> { ["clientId"] = clientId }, ct);
            if ((status.Int("toolCount") ?? 0) > 0)
                return;
            await Task.Delay(200, ct);
        }
        Assert.Fail($"'{clientId}' never published a tool catalogue.");
    }

    private static async Task<string?> ReadText(McpClient mcp, string selector, CancellationToken ct)
    {
        var result = await Ok(mcp, "get_property",
            new Dictionary<string, object?> { ["selector"] = selector, ["propertyName"] = "Text" }, ct);
        return result.Str("value") ?? result.ToString();
    }

    /// <summary>Polls inside the client rather than sleeping here, so the suite needs no delays.</summary>
    private static async Task WaitForText(McpClient mcp, string selector, string expected, CancellationToken ct)
    {
        var waited = await Ok(mcp, "wait_for", new Dictionary<string, object?>
        {
            ["selector"] = selector,
            ["propertyName"] = "Text",
            ["expected"] = expected,
            ["timeoutMs"] = 10_000,
        }, ct);

        Assert.True(waited.Bool("ok") ?? false,
            $"'{selector}'.Text never became '{expected}': {waited}");
    }

    /// <summary>
    /// Reads a numeric property. <c>get_property</c> preserves the CLR type, so a double
    /// arrives as a JSON number — reading it as a string silently yields null.
    /// </summary>
    private static async Task<double> ReadNumber(
        McpClient mcp, string selector, string property, CancellationToken ct)
    {
        var result = await Ok(mcp, "get_property",
            new Dictionary<string, object?> { ["selector"] = selector, ["propertyName"] = property }, ct);
        var value = result.Prop("value");
        Assert.True(value is not null, $"'{selector}'.{property} reported no value: {result}");
        return value!.Value.GetDouble();
    }

    /// <summary>Polls a numeric property until it reaches <paramref name="expected"/>.</summary>
    private static async Task<double> PollNumber(
        McpClient mcp, string selector, string property, double expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var last = double.NaN;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadNumber(mcp, selector, property, ct);
            if (Math.Abs(last - expected) < 0.001)
                return last;
            await Task.Delay(100, ct);
        }
        return last;
    }

    private static async Task<int> CountMatching(McpClient mcp, string selector, CancellationToken ct) =>
        (await Ok(mcp, "query_controls", new Dictionary<string, object?> { ["selector"] = selector }, ct))
            .Int("count") ?? -1;

    private static void AssertPng(byte[] bytes, string what)
    {
        Assert.True(bytes.Length > 8, $"{what}: no image data.");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        // A blank or degenerate surface compresses to almost nothing, so a few KB is the
        // floor that catches "WPF rendered nothing" without being brittle about content.
        Assert.True(bytes.Length > 4096,
            $"{what}: only {bytes.Length} bytes — the WPF renderer probably produced a blank surface.");
    }

    // =====================================================================

    [E2EFact]
    public Task Wpf_Renders_Screenshots_With_An_Actionable_Legend() => WithWpfAsync(async (mcp, _, ct) =>
    {
        var shot = await Call(mcp, "screenshot_window", null, ct);
        Assert.False(shot.IsError ?? false, McpJson.Text(shot));

        var image = McpJson.Image(shot);
        Assert.NotNull(image);
        Assert.Equal("image/png", image!.MimeType);

        var bytes = image.DecodedData.ToArray();
        McpJson.Save("wpf-screenshot_window.png", bytes);
        AssertPng(bytes, "wpf screenshot_window");

        // Rendering a single control goes through a different WPF path than the whole window.
        var control = await Call(mcp, "screenshot_control",
            new Dictionary<string, object?> { ["target"] = "#Gauge" }, ct);
        Assert.False(control.IsError ?? false, McpJson.Text(control));
        var controlBytes = McpJson.Image(control)!.DecodedData.ToArray();
        McpJson.Save("wpf-screenshot_gauge.png", controlBytes);
        AssertPng(controlBytes, "wpf screenshot_control");

        var marked = await Call(mcp, "screenshot_marked", null, ct);
        Assert.False(marked.IsError ?? false, McpJson.Text(marked));
        var markedBytes = McpJson.Image(marked)!.DecodedData.ToArray();
        McpJson.Save("wpf-screenshot_marked.png", markedBytes);
        AssertPng(markedBytes, "wpf screenshot_marked");

        // The legend is what makes a set-of-marks screenshot usable: every number has to map
        // back to a handle the model can then act on. A picture with no legend is a dead end.
        var legend = McpJson.Ok(marked);
        Assert.True((legend.Int("count") ?? 0) > 0, "the WPF set-of-marks legend was empty.");

        var mark = Assert.Single(legend.Array("marks"), m => m.Str("name") == "Save");
        var handle = mark.Str("id");
        Assert.False(string.IsNullOrWhiteSpace(handle), "a mark must name a usable handle.");


        // Prove the handle really is actionable, rather than a label that only looks right.
        var invoked = await Ok(mcp, "automation_action",
            new Dictionary<string, object?> { ["handle"] = handle, ["action"] = "Invoke" }, ct);
        Assert.True(invoked.Bool("ok") ?? false, $"the legend handle did not drive the control: {invoked}");
        await WaitForText(mcp, "#CountLabel", "Saves: 1", ct);
        Log($"legend handle {handle} drove the Save button");

        // Every mark carries the geometry a caller needs to act on it positionally.
        var bounds = mark.Prop("globalBounds") ?? mark.Prop("bounds");
        Assert.True(bounds is not null, "a mark must carry bounds so it can be acted on.");
        Assert.True(bounds!.Value.GetProperty("width").GetDouble() > 0, "a mark's box must be real.");
    });

    [E2EFact]
    public Task Wpf_Accepts_Synthetic_Keyboard_Input() => WithWpfAsync(async (mcp, _, ct) =>
    {
        // type_text raises real WPF text input on the focused element — a completely
        // different path from set_property, which writes the dependency property directly.
        // Both have to land, and only the former proves WPF's input routing works.
        Assert.Equal("WPF", await ReadText(mcp, "#Input", ct));

        await Ok(mcp, "set_focus", new Dictionary<string, object?> { ["selector"] = "#Input" }, ct);
        var typed = await Ok(mcp, "type_text",
            new Dictionary<string, object?> { ["selector"] = "#Input", ["text"] = "-typed" }, ct);
        Assert.True(typed.Bool("ok") ?? false, $"type_text failed on WPF: {typed}");

        var after = await ReadText(mcp, "#Input", ct);
        Assert.Contains("typed", after!, StringComparison.Ordinal);

        // The TextBox is two-way bound with UpdateSourceTrigger=PropertyChanged, so the view
        // model saw it too — the keystrokes reached the binding, not just the visual.
        var context = await Ok(mcp, "get_data_context",
            new Dictionary<string, object?> { ["selector"] = "#Input" }, ct);
        Assert.True(context.Bool("hasDataContext") ?? false);

        // send_keys drives key CHORDS rather than characters — a separate routed-event path
        // from type_text. Backspace on the focused TextBox is the cleanest observable proof:
        // it edits through WPF's own key handling and the two-way binding follows.
        var beforeBack = await ReadText(mcp, "#Input", ct);
        await Ok(mcp, "set_focus", new Dictionary<string, object?> { ["selector"] = "#Input" }, ct);
        var back = await Ok(mcp, "send_keys",
            new Dictionary<string, object?> { ["selector"] = "#Input", ["keys"] = "Back" }, ct);
        Assert.True(back.Bool("ok") ?? false, $"send_keys failed on WPF: {back}");

        var afterBack = await ReadText(mcp, "#Input", ct);
        Assert.Equal(beforeBack!.Length - 1, afterBack!.Length);

        // A chord on a control whose own key handling owns the gesture: Space toggles a
        // CheckBox, and the two-way binding carries it to the view model.
        await Ok(mcp, "set_focus", new Dictionary<string, object?> { ["selector"] = "#SubscribeCheck" }, ct);
        var checkedBefore = (await Ok(mcp, "get_property",
            new Dictionary<string, object?> { ["selector"] = "#SubscribeCheck", ["propertyName"] = "IsChecked" }, ct))
            .Prop("value")!.Value.GetBoolean();

        var space = await Ok(mcp, "send_keys",
            new Dictionary<string, object?> { ["selector"] = "#SubscribeCheck", ["keys"] = "Space" }, ct);
        Assert.True(space.Bool("ok") ?? false, $"send_keys Space failed on WPF: {space}");

        var checkedAfter = (await Ok(mcp, "get_property",
            new Dictionary<string, object?> { ["selector"] = "#SubscribeCheck", ["propertyName"] = "IsChecked" }, ct))
            .Prop("value")!.Value.GetBoolean();
        Assert.NotEqual(checkedBefore, checkedAfter);

        // Worth knowing, and deliberately not asserted: arrow keys do NOT move a ListBox's
        // selection this way. WPF implements that navigation by moving keyboard focus between
        // item containers, so it needs focus on a ListBoxItem — raising KeyDown with the
        // ListBox itself as the source is inert. send_keys still reports ok, because it
        // reports that the chord was sent, not that a control chose to act on it.
        Log("type_text and send_keys both reached the WPF bindings");
    });

    [E2EFact]
    public Task Wpf_Drives_A_Control_With_No_Automation_Peer() => WithWpfAsync(async (mcp, _, ct) =>
    {
        // GaugeControl returns null from OnCreateAutomationPeer on purpose. UI-Automation
        // therefore cannot see it, and the only way to drive it is synthesised pointer input.
        // This is the WPF adapter's fallback path and nothing else in the suite covers it.
        var refused = await Call(mcp, "automation_action",
            new Dictionary<string, object?> { ["selector"] = "#Gauge", ["action"] = "Invoke" }, ct);

        var refusedOk = !(refused.IsError ?? false) && (McpJson.Ok(refused).Bool("ok") ?? false);
        Assert.False(refusedOk,
            "GaugeControl exposes no automation peer, so automation_action must not claim success.");

        Assert.Equal(0.25, await ReadNumber(mcp, "#Gauge", "Value", ct), 3);

        // Click its centre, in top-level client coordinates — which is exactly what
        // globalBounds reports, so no summing of parent offsets is needed.
        var hits = await Ok(mcp, "query_controls",
            new Dictionary<string, object?> { ["selector"] = "#Gauge" }, ct);
        var gauge = hits.Array("controls").First();
        var box = gauge.Prop("globalBounds");
        Assert.True(box is not null, "the gauge reported no top-level bounds to click.");

        var x = box!.Value.GetProperty("x").GetDouble() + box.Value.GetProperty("width").GetDouble() / 2;
        var y = box.Value.GetProperty("y").GetDouble() + box.Value.GetProperty("height").GetDouble() / 2;

        var clicked = await Ok(mcp, "click_at",
            new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["selector"] = "#Gauge" }, ct);
        Assert.True(clicked.Bool("ok") ?? false, $"click_at failed on WPF: {clicked}");

        // Exactly ONE notch. The count is the assertion, not just "it moved": raising both
        // the button-specific and the generic mouse event used to deliver every synthetic
        // click twice, so one click_at advanced the gauge two notches — an AI driving a WPF
        // app would double every click it made.
        var advanced = await PollNumber(mcp, "#Gauge", "Value", 0.35, ct);
        Assert.Equal(0.35, advanced, 3);

        var angle = await ReadNumber(mcp, "#Gauge", "LastClickAngle", ct);
        Assert.True(angle > 0, $"the gauge never recorded a click angle (got {angle}).");

        // ...and it stays one-per-click rather than merely being off by a constant.
        await Ok(mcp, "click_at",
            new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["selector"] = "#Gauge" }, ct);
        Assert.Equal(0.45, await PollNumber(mcp, "#Gauge", "Value", 0.45, ct), 3);
        Log("two synthetic clicks advanced the peer-less gauge exactly two notches");

        // Worth knowing, and why this fact targets a custom control rather than a Button:
        // WPF's ButtonBase only raises Click when its MouseLeftButtonDown handler wins mouse
        // CAPTURE, which a synthesised event cannot grant — IsPressed stays false and no
        // Click follows. Standard controls are therefore driven through UI Automation
        // (automation_action), and synthetic input is for the controls that expose no peer.
    });

    [E2EFact]
    public Task Wpf_Selector_Grammar_Behaves_As_Documented() => WithWpfAsync(async (mcp, _, ct) =>
    {
        // By x:Name, by type, and by attribute.
        Assert.Equal(1, await CountMatching(mcp, "#Save", ct));
        Assert.True(await CountMatching(mcp, "Button", ct) >= 1, "no Buttons matched by type.");
        Assert.Equal(1, await CountMatching(mcp, "TextBox", ct));
        Assert.Equal(1, await CountMatching(mcp, "TextBox[Name=Input]", ct));

        // Descendant combinator across the ScrollViewer/StackPanel nesting.
        Assert.Equal(1, await CountMatching(mcp, "Window TextBox", ct));

        // The documented gap: style classes are an Avalonia concept, so a class selector
        // matches NOTHING in WPF. The README promises this and nothing asserted it — a
        // future adapter change that started matching something would be a silent surprise.
        Assert.Equal(0, await CountMatching(mcp, ".primary", ct));
        Assert.Equal(0, await CountMatching(mcp, "Button.primary", ct));

        // A selector matching nothing is an empty result, not an error.
        Assert.Equal(0, await CountMatching(mcp, "#NoSuchControl", ct));

        // The accessibility tree names nodes by their ACCESSIBLE name where they have one
        // (a Button's is its Content), falling back to x:Name otherwise — the same contract
        // the Avalonia side asserts, which is what makes one set of selectors work on both.
        var semantic = await Ok(mcp, "get_semantic_tree",
            new Dictionary<string, object?> { ["interactiveOnly"] = true }, ct);
        var tree = semantic.ToString();
        Assert.Contains("\"role\":\"Button\",\"name\":\"Save\"", tree, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"CheckBox\"", tree, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"Edit\"", tree, StringComparison.Ordinal);

        // A broken binding crashes nothing, so without this a XAML regression in the WPF
        // sample would go unnoticed indefinitely.
        var bindingErrors = await Ok(mcp, "get_binding_errors", null, ct);
        Assert.DoesNotContain("BindingError", bindingErrors.ToString(), StringComparison.OrdinalIgnoreCase);

        // WPF's dispatcher has different idle semantics from Avalonia's, so this is genuinely
        // separate code rather than a repeat of the Avalonia assertion.
        var idle = await Ok(mcp, "wait_for_idle",
            new Dictionary<string, object?> { ["timeoutMs"] = 5000 }, ct);
        Assert.True(idle.Bool("idle") ?? false, $"the WPF dispatcher never reported idle: {idle}");
    });

    [E2EFact]
    public Task Wpf_Read_Only_Refuses_Writes_But_Never_Reads() => WithWpfAsync(async (mcp, clientId, ct) =>
    {
        try
        {
            await Ok(mcp, "hub_set_readonly",
                new Dictionary<string, object?> { ["clientId"] = clientId, ["readOnly"] = true }, ct);

            var blocked = await Call(mcp, "automation_action",
                new Dictionary<string, object?> { ["selector"] = "#Save", ["action"] = "Invoke" }, ct);
            Assert.True(blocked.IsError ?? false, "a read-only WPF client accepted a mutating tool.");
            Assert.Contains("read-only", McpJson.Text(blocked), StringComparison.OrdinalIgnoreCase);

            // Inspection is never blocked — that is the whole point of the flag.
            Assert.Equal("Saves: 0", await ReadText(mcp, "#CountLabel", ct));
            Assert.False((await Call(mcp, "screenshot_window", null, ct)).IsError ?? false);
        }
        finally
        {
            // MUST be restored: the flag is persisted per machine in known-clients.json, so
            // leaving it set would cripple this app for every later run and every real agent.
            await Ok(mcp, "hub_set_readonly",
                new Dictionary<string, object?> { ["clientId"] = clientId, ["readOnly"] = false }, ct);
        }

        var allowed = await Ok(mcp, "automation_action",
            new Dictionary<string, object?> { ["selector"] = "#Save", ["action"] = "Invoke" }, ct);
        Assert.True(allowed.Bool("ok") ?? false, "lifting read-only did not restore driving.");
        await WaitForText(mcp, "#CountLabel", "Saves: 1", ct);
    });

    [E2EFact]
    public async Task The_Hub_Can_Launch_And_Restart_A_Wpf_App()
    {
        var exe = WpfExe;
        if (string.IsNullOrWhiteSpace(exe))
        {
            Log($"{E2EEnvironment.WpfDemoExeVar} is not set; skipping the WPF launch coverage.");
            return;
        }

        using var cts = new CancellationTokenSource(Overall);
        var ct = cts.Token;
        await using var mcp = await rig.ConnectViaShimAsync(ct);

        string? clientId = null;
        try
        {
            // Launched BY THE HUB rather than by the harness, so the launch-profile
            // machinery is exercised against a net8.0-windows target rather than only the
            // Avalonia one.
            var launched = await Ok(mcp, "hub_launch_client",
                new Dictionary<string, object?> { ["clientId"] = "wpfdemo", ["exePath"] = exe }, ct);
            rig.TrackLaunchedPid("wpfdemo-launched", launched.Int("processId")!.Value);

            var attached = await Ok(mcp, "hub_wait_for_client",
                new Dictionary<string, object?> { ["launchId"] = launched.Str("launchId"), ["timeoutMs"] = 90_000 }, ct);
            Assert.True(attached.Bool("connected") ?? false, "the hub-launched WPF demo never attached.");

            clientId = attached.Str("clientId")!;
            Assert.Matches(@"^wpfdemo#\d+$", clientId);
            Assert.True(attached.Bool("mine") ?? false, "a hub-launched WPF instance must belong to its launcher.");

            await WaitForTools(mcp, clientId, ct);
            await WaitForText(mcp, "#CountLabel", "Saves: 0", ct);

            // Restart keeps the id, which is what lets a recording still replay after a
            // rebuild — the same promise the Avalonia side makes.
            var restarted = await Ok(mcp, "hub_restart_client",
                new Dictionary<string, object?> { ["clientId"] = clientId }, ct);
            rig.TrackLaunchedPid("wpfdemo-restarted", restarted.Int("processId")!.Value);

            var back = await Ok(mcp, "hub_wait_for_client",
                new Dictionary<string, object?> { ["launchId"] = restarted.Str("launchId"), ["timeoutMs"] = 90_000 }, ct);
            Assert.True(back.Bool("connected") ?? false, "the restarted WPF demo never came back.");
            Assert.Equal(clientId, back.Str("clientId"));

            // A genuinely fresh process, not the old one still answering.
            await WaitForTools(mcp, clientId, ct);
            await WaitForText(mcp, "#CountLabel", "Saves: 0", ct);
            Log($"hub launched and restarted {clientId}");
        }
        finally
        {
            try { await Call(mcp, "hub_release_client", null, ct); } catch { }
            if (clientId is not null)
            {
                try
                {
                    var clients = await Ok(mcp, "hub_list_clients", null, ct);
                    var row = clients.EnumerateArray().FirstOrDefault(c => c.Str("clientId") == clientId);
                    if (row.ValueKind == JsonValueKind.Object && row.Int("processId") is int pid && pid > 0)
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(pid);
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch { /* already gone */ }
            }
        }
    }
}
