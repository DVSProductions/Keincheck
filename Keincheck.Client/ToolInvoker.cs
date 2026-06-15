using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Keincheck.Client;

/// <summary>
/// Builds the <see cref="RequestContext{TParams}"/> the SDK needs to invoke an
/// <see cref="McpServerTool"/> out-of-band (i.e. not through a live MCP session).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="McpServerTool.InvokeAsync"/> takes a
/// <c>RequestContext&lt;CallToolRequestParams&gt;</c>. That context carries the
/// matched params and a back-reference to the owning <see cref="McpServer"/>; the
/// SDK reads DI services from <see cref="McpServerToolCreateOptions.Services"/>
/// (captured at tool creation), so a tool's spine parameters resolve without a live
/// connection. We construct a parked <see cref="McpServer"/> over an unconnected
/// in-memory transport purely to satisfy the context shape — it is never run and
/// sends no traffic.
/// </para>
/// </remarks>
internal static class ToolInvoker
{
    /// <summary>
    /// Creates a request context for invoking a tool with <paramref name="parameters"/>.
    /// <paramref name="services"/> is the same provider the tools were created with.
    /// </summary>
    public static RequestContext<CallToolRequestParams> CreateRequest(
        CallToolRequestParams parameters, IServiceProvider services)
    {
        var jsonRpc = new JsonRpcRequest
        {
            Method = RequestMethods.ToolsCall,
            Id = new RequestId("client-local"),
        };

        var server = ParkedServer.Value;
        return new RequestContext<CallToolRequestParams>(server, jsonRpc, parameters)
        {
            Services = services,
        };
    }

    // A single parked server instance, lazily created over a dead in-memory pipe.
    private static readonly Lazy<McpServer> ParkedServer = new(CreateParkedServer);

    private static McpServer CreateParkedServer()
    {
        // A transport over two ends of an in-memory pipe that is never pumped. The
        // server is created but RunAsync is never called, so nothing is read/written.
        var pipe = new System.IO.Pipelines.Pipe();
        var input = pipe.Reader.AsStream();
        var output = pipe.Writer.AsStream();
        var transport = new StreamServerTransport(input, output, serverName: "keincheck-client-local", loggerFactory: null);

        var options = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new Implementation { Name = "Keincheck.Client", Version = "0.1.0" },
        };

        return McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
    }
}
