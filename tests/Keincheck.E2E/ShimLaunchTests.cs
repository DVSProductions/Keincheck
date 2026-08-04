using System.Diagnostics;
using Keincheck.Protocol;
using ModelContextProtocol.Client;
using Xunit;
using Xunit.Abstractions;

namespace Keincheck.E2E;

/// <summary>
/// Proves <c>keincheck-connect.exe</c> can start a hub from cold — the first thing that
/// happens on a fresh machine when Claude spawns the shim and no hub is running.
/// </summary>
/// <remarks>
/// <para>Deliberately outside <see cref="HubCollection"/> and behind its own opt-in. It has
/// to begin with <b>no hub running</b>, which is incompatible with a shared rig that owns
/// one: killing the rig's hub mid-assembly would fail every later fact and the rig's own
/// liveness verdict. The workflow therefore runs this as a separate <c>dotnet test</c>
/// invocation after the main suite.</para>
/// <para>What it covers that nothing else can: <c>EnsureHubRunningAsync</c>'s mutex probe,
/// <c>ResolveHubExe</c>'s co-located branch (marked <c>TODO(velopack)</c> in the source and
/// never otherwise exercised), the detached <c>LaunchHub</c>, and the
/// <c>PipeTransport.ConnectAsync</c> backoff that covers the gap between "process up" and
/// "pipe listening".</para>
/// </remarks>
public sealed class ShimLaunchTests(ITestOutputHelper output)
{
    /// <summary>Opt-in, set only by the workflow's dedicated invocation.</summary>
    private const string EnableVar = "KEINCHECK_E2E_SHIM_LAUNCH";

    [E2EFact]
    public async Task Shim_Starts_A_Hub_When_None_Is_Running()
    {
        if (Environment.GetEnvironmentVariable(EnableVar) != "1")
            return; // not this invocation's job

        var hubDir = Environment.GetEnvironmentVariable(E2EEnvironment.HubDirVar)
            ?? throw new InvalidOperationException($"{E2EEnvironment.HubDirVar} is not set.");
        var connectExe = Path.Combine(hubDir, "keincheck-connect.exe");
        Assert.True(File.Exists(connectExe),
            $"the shim must ship co-located with the hub; it is missing from {hubDir}.");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        await StopAnyRunningHubAsync(cts.Token);
        Assert.False(HubRig.IsHubRunning(), "a hub is still running; the cold-start path cannot be tested.");

        try
        {
            // No --hub-exe and no KEINCHECK_HUB_EXE: the shim must find the hub beside
            // itself, which is exactly what the Velopack 'current' layout provides.
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "keincheck-connect",
                Command = connectExe,
                WorkingDirectory = hubDir,
                ShutdownTimeout = TimeSpan.FromSeconds(10),
                StandardErrorLines = line => output.WriteLine($"shim| {line}"),
            });

            await using var mcp = await McpClient.CreateAsync(
                transport, clientOptions: null, loggerFactory: null, cancellationToken: cts.Token);

            Assert.Equal("Keincheck.Hub", mcp.ServerInfo.Name);

            var status = McpJson.Ok(await mcp.CallToolAsync("hub_status", null, cancellationToken: cts.Token));
            Assert.False(string.IsNullOrWhiteSpace(status.Str("hubVersion")));
            output.WriteLine($"the shim brought up hub {status.Str("hubVersion")} from cold");

            Assert.True(HubRig.IsHubRunning(), "the shim answered but no hub holds the single-instance mutex.");
        }
        finally
        {
            // Leave the machine as we found it — the hub the shim launched is detached and
            // would otherwise outlive the test run.
            await StopAnyRunningHubAsync(CancellationToken.None);
        }
    }

    private static async Task StopAnyRunningHubAsync(CancellationToken ct)
    {
        if (!HubRig.IsHubRunning())
            return;

        foreach (var stray in Process.GetProcessesByName("Keincheck.Hub"))
        {
            try { stray.Kill(entireProcessTree: true); stray.WaitForExit(10_000); }
            catch { /* raced us to exit */ }
            finally { stray.Dispose(); }
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && HubRig.IsHubRunning())
            await Task.Delay(200, ct);

        if (HubRig.IsHubRunning())
            throw new InvalidOperationException(
                $"A hub still holds '{PipeNames.SingleInstanceMutex}'.");
    }
}
