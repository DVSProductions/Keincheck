using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Keincheck.E2E;

/// <summary>
/// The remote path against a real hub process, over loopback: enable the listener, issue a
/// credential, attach a second demo through mTLS, prove the guards hold, then revoke and
/// disable.
/// </summary>
/// <remarks>
/// <para>Opt-in via <c>KEINCHECK_E2E_REMOTE=1</c> even when the suite is enabled, because
/// enabling remote access provisions a real certificate authority into
/// <c>%APPDATA%\Keincheck\remote</c> and installs the JSONL audit sink. That is fine on a
/// disposable runner and rude on a developer's machine.</para>
/// <para><c>RemoteEndToEndTests</c> already covers this in-process. What is new here is that
/// it runs through the shipped hub binary, the real <c>UseMcpClient</c> +
/// <c>RemoteChannelConnector.FromEnvironment()</c> client stack, and a real TCP socket —
/// the combination that until now had only ever been exercised by hand against a second
/// physical machine.</para>
/// </remarks>
[Collection(HubCollection.Name)]
public sealed class RemoteLoopbackTests(HubRig rig, ITestOutputHelper output)
{
    private const string TargetLabel = "ci";
    private const int LoopbackPort = 7423;

    [E2EFact]
    public async Task Remote_Client_Attaches_Read_Only_And_Can_Be_Revoked()
    {
        if (!E2EEnvironment.RemoteEnabled)
            return; // provisioning a CA is not something to do by surprise

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = cts.Token;

        await using var mcp = await rig.ConnectViaShimAsync(ct);

        var available = McpJson.Ok(await mcp.CallToolAsync("hub_remote_status", null, cancellationToken: ct));
        if (!(available.Bool("available") ?? false))
        {
            output.WriteLine("this hub build has no remote support; nothing to test.");
            return;
        }

        var credentialPath = Path.Combine(E2EEnvironment.ArtifactsDirectory, "remote-ci.credential");
        ManagedProcess? remoteDemo = null;
        string? serial = null;

        try
        {
            // ---- enable ----------------------------------------------------
            var enabled = McpJson.Ok(await mcp.CallToolAsync("hub_remote_enable",
                new Dictionary<string, object?> { ["bindAddress"] = "127.0.0.1", ["port"] = LoopbackPort },
                cancellationToken: ct));
            Assert.True(enabled.Bool("enabled") ?? false);
            Assert.True(enabled.Bool("listening") ?? false, "the listener did not bind.");
            output.WriteLine($"listening on {enabled.Str("boundEndpoint")}");

            // ---- issue -----------------------------------------------------
            var issued = McpJson.Ok(await mcp.CallToolAsync("hub_remote_issue",
                new Dictionary<string, object?>
                {
                    ["target"] = TargetLabel,
                    ["days"] = 1,
                    ["note"] = "keincheck e2e",
                    ["outPath"] = credentialPath,
                },
                cancellationToken: ct));

            serial = issued.Str("serial");
            Assert.False(string.IsNullOrWhiteSpace(serial));
            Assert.Equal(TargetLabel, issued.Str("host"));
            Assert.True(File.Exists(credentialPath), "the credential bundle was not written.");

            // ---- attach ----------------------------------------------------
            // The SAME demo binary, dialling in over TCP instead of the pipe purely because
            // KEINCHECK_REMOTE_FILE is set — RemoteChannelConnector.FromEnvironment returns
            // null without it, which is what makes shipping this in a sample safe.
            remoteDemo = rig.Track(ManagedProcess.Start(
                "demo-remote",
                Environment.GetEnvironmentVariable(E2EEnvironment.DemoExeVar)!,
                environment: new Dictionary<string, string?>
                {
                    ["KEINCHECK_REMOTE_FILE"] = credentialPath,
                }));

            var remoteId = $"demo@{TargetLabel}#1";
            var attached = McpJson.Ok(await mcp.CallToolAsync("hub_wait_for_client",
                new Dictionary<string, object?> { ["clientId"] = remoteId, ["timeoutMs"] = 90_000 },
                cancellationToken: ct));
            Assert.True(attached.Bool("connected") ?? false,
                $"the remote demo never attached.\n{remoteDemo.Describe()}");

            // ---- the guards ------------------------------------------------
            var clients = McpJson.Ok(await mcp.CallToolAsync("hub_list_clients", null, cancellationToken: ct));
            var entry = Assert.Single(clients.EnumerateArray(), c => c.Str("clientId") == remoteId);
            Assert.Equal("tcp", entry.Str("transport"));
            Assert.Equal(TargetLabel, entry.Str("host"));
            Assert.True(entry.Bool("readOnly") ?? false, "a remote client must START read-only.");
            Assert.False(entry.Bool("canLaunch") ?? true, "the hub cannot start processes on another machine.");

            // Never auto-selected: a machine dialling in must not silently become the thing
            // the AI is driving.
            var status = McpJson.Ok(await mcp.CallToolAsync("hub_status", null, cancellationToken: ct));
            Assert.NotEqual(remoteId, status.Str("activeClientId"));

            var previousActive = status.Str("activeClientId");

            await mcp.CallToolAsync("hub_select_client",
                new Dictionary<string, object?> { ["clientId"] = remoteId }, cancellationToken: ct);

            // Inspection works while read-only holds; mutation does not.
            var windows = McpJson.Ok(await mcp.CallToolAsync("list_windows", null, cancellationToken: ct));
            Assert.Contains("Keincheck Demo", windows.ToString(), StringComparison.Ordinal);

            var refused = McpJson.Refused(await mcp.CallToolAsync("automation_action",
                new Dictionary<string, object?> { ["selector"] = "#CountButton", ["action"] = "Invoke" },
                cancellationToken: ct));
            Assert.Contains("read-only", refused, StringComparison.OrdinalIgnoreCase);

            // Described in the source as the most important guard in the remote work: without
            // it, "restart the app on the suit" silently launches a LOCAL copy instead.
            var launchRefused = McpJson.Refused(await mcp.CallToolAsync("hub_launch_client",
                new Dictionary<string, object?> { ["clientId"] = remoteId }, cancellationToken: ct));
            output.WriteLine($"launch refused for a remote client: {launchRefused}");

            // ---- revoke ----------------------------------------------------
            var listed = McpJson.Ok(await mcp.CallToolAsync("hub_remote_status", null, cancellationToken: ct));
            var credential = Assert.Single(listed.Array("credentials"), c => c.Str("serial") == serial);
            Assert.False(credential.Bool("revoked") ?? true);
            Assert.True(credential.Bool("isUsable") ?? false);

            var revoked = McpJson.Ok(await mcp.CallToolAsync("hub_remote_revoke",
                new Dictionary<string, object?> { ["serial"] = serial }, cancellationToken: ct));
            Assert.Equal(serial, revoked.Str("revoked"));

            var afterRevoke = McpJson.Ok(await mcp.CallToolAsync("hub_remote_status", null, cancellationToken: ct));
            var revokedEntry = Assert.Single(afterRevoke.Array("credentials"), c => c.Str("serial") == serial);
            Assert.True(revokedEntry.Bool("revoked") ?? false);

            if (previousActive is not null)
                await mcp.CallToolAsync("hub_select_client",
                    new Dictionary<string, object?> { ["clientId"] = previousActive }, cancellationToken: ct);

            // The durable trail the remote design promises, which nothing has ever checked.
            var auditDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Keincheck", "audit");
            Assert.True(Directory.Exists(auditDir), "enabling remote access must install the audit sink.");
            var auditFiles = Directory.GetFiles(auditDir, "*.jsonl");
            Assert.NotEmpty(auditFiles);
            foreach (var file in auditFiles)
                File.Copy(file, Path.Combine(E2EEnvironment.ArtifactsDirectory, Path.GetFileName(file)), overwrite: true);
        }
        finally
        {
            // Order matters: drop the client first so the listener is idle, then close it.
            // Leaving either behind would poison whatever fact runs next against this hub.
            remoteDemo?.Kill();
            await WaitForClientGoneAsync(mcp, $"demo@{TargetLabel}#1", TimeSpan.FromSeconds(30));

            try
            {
                await mcp.CallToolAsync("hub_remote_disable", null, cancellationToken: CancellationToken.None);
                var final = McpJson.Ok(await mcp.CallToolAsync("hub_remote_status", null, cancellationToken: CancellationToken.None));
                Assert.False(final.Bool("enabled") ?? true);
                Assert.False(final.Bool("listening") ?? true);
            }
            catch (Exception ex)
            {
                output.WriteLine($"could not disable remote access: {ex.Message}");
            }

            try { File.Delete(credentialPath); } catch { /* best effort */ }
        }
    }

    private static async Task WaitForClientGoneAsync(McpClient mcp, string clientId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var clients = McpJson.Ok(await mcp.CallToolAsync("hub_list_clients", null, cancellationToken: CancellationToken.None));
            if (!clients.EnumerateArray().Any(c => c.Str("clientId") == clientId))
                return;
            await Task.Delay(250);
        }
    }
}
