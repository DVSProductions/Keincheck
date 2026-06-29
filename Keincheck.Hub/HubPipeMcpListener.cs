using Keincheck.Protocol;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Keincheck.Hub;

/// <summary>
/// Mechanic #3 (hub side) — runs an MCP server over an arbitrary <b>named-pipe
/// stream</b>, so the stdio shim (<c>Keincheck.Connect</c>) can be a trivial
/// byte-pump (stdin→pipe, pipe→stdout) instead of relaying HTTP. Each accepted pipe
/// connection gets its own <see cref="StreamServerTransport"/> + <see cref="McpServer"/>
/// wired with the same dynamic list/call handlers the HTTP host uses.
/// </summary>
/// <remarks>
/// We build a throwaway DI container per session to obtain configured
/// <see cref="McpServerOptions"/> (handlers + capabilities) from the shared
/// <see cref="HubMcpServer.ConfigureMcp"/> path, then create the server over the pipe
/// stream and run it until the peer disconnects.
/// </remarks>
public sealed class HubPipeMcpListener : IAsyncDisposable
{
    private readonly HubMcpServer _hub;
    private readonly HubOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public HubPipeMcpListener(HubMcpServer hub, HubOptions options)
    {
        _hub = hub;
        _options = options;
    }

    /// <summary>Starts accepting MCP-over-pipe sessions on the well-known pipe.</summary>
    public void Start(string? pipeName = null)
    {
        pipeName ??= _options.PipeName ?? PipeNames.McpSessionPipe("default");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(pipeName, _cts.Token));
    }

    private async Task AcceptLoopAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = PipeTransport.CreateServerStream(pipeName);
            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { await server.DisposeAsync().ConfigureAwait(false); break; }
            catch { await server.DisposeAsync().ConfigureAwait(false); continue; }

            // Serve this session concurrently; loop back to accept the next.
            _ = ServeSessionAsync(server, ct);
        }
    }

    private async Task ServeSessionAsync(System.IO.Pipes.NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await RunMcpOverStreamAsync(pipe, pipe, ct).ConfigureAwait(false);
        }
        catch { /* peer dropped */ }
        finally
        {
            try { await pipe.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Runs one MCP server session over the given input/output streams using the
    /// hub's dynamic handlers. Public + static-ish so tests can drive it over an
    /// in-memory stream pair without a real pipe.
    /// </summary>
    public async Task RunMcpOverStreamAsync(Stream input, Stream output, CancellationToken ct)
    {
        var services = new ServiceCollection();
        ConfigureSessionServices(services);
        await using var provider = services.BuildServiceProvider();

        var serverOptions = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;

        await using var transport = new StreamServerTransport(input, output, _options.ServerName, loggerFactory: null);
        await using var server = McpServer.Create(transport, serverOptions, loggerFactory: null, serviceProvider: provider);

        // Register this session's stable server for the lifetime of the session so it
        // receives list-changed / log notifications, and remove it on disconnect. Do NOT
        // register request.Server per tools/list — that wrapper is per-request and leaks.
        _hub.RegisterSession(server);
        try
        {
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _hub.UnregisterSession(server);
        }
    }

    private void ConfigureSessionServices(IServiceCollection services)
    {
        // Reuse the exact dynamic handler wiring the HTTP host uses.
        var mcp = services.AddMcpServer(o =>
        {
            o.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = _options.ServerName,
                Version = _options.ServerVersion,
            };
            o.Capabilities ??= new ModelContextProtocol.Protocol.ServerCapabilities();
            o.Capabilities.Tools ??= new ModelContextProtocol.Protocol.ToolsCapability { ListChanged = true };
        });
        _hub.ConfigureMcp(mcp);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}
