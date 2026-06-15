using System.Net;
using System.Reflection;
using Avalonia;
using Keincheck.Core;
using Keincheck.Core.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Keincheck;

/// <summary>
/// Hosts the embedded MCP server. <see cref="Attach"/> spins up a Kestrel
/// <see cref="WebApplication"/> bound to loopback on a dedicated background
/// thread, registers the shared spine (<see cref="ControlRegistry"/>,
/// <see cref="PropertyValueSerializer"/>, options) as singletons, and wires the
/// MCP HTTP transport plus all discovered tools. Dispose the returned handle to
/// shut the host down.
/// </summary>
public static class McpHost
{
    /// <summary>
    /// Starts the MCP host for <paramref name="app"/> using <paramref name="opts"/>.
    /// Returns an <see cref="IDisposable"/> that stops Kestrel and restores any
    /// logging sink on dispose. Call from the UI thread during app startup; the
    /// actual server runs off-thread and never blocks the dispatcher.
    /// </summary>
    public static IDisposable Attach(Application app, McpServerOptions opts)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(opts);

        var registry = new ControlRegistry();
        var serializer = new PropertyValueSerializer(opts.MaxSerializationDepth);

        BindingErrorSink? sink = null;
        if (opts.CaptureBindingErrors)
            sink = BindingErrorSink.Install(opts.BindingErrorBufferSize);

        var runner = new HostRunner(app, opts, registry, serializer, sink);
        runner.Start();
        return runner;
    }

    /// <summary>
    /// Owns the background thread and the running <see cref="WebApplication"/>.
    /// </summary>
    private sealed class HostRunner : IDisposable
    {
        private readonly Application _app;
        private readonly McpServerOptions _opts;
        private readonly ControlRegistry _registry;
        private readonly PropertyValueSerializer _serializer;
        private readonly BindingErrorSink? _sink;
        private readonly CancellationTokenSource _cts = new();
        private readonly ManualResetEventSlim _started = new(false);

        private Thread? _thread;
        private WebApplication? _web;
        private volatile bool _disposed;

        public HostRunner(
            Application app,
            McpServerOptions opts,
            ControlRegistry registry,
            PropertyValueSerializer serializer,
            BindingErrorSink? sink)
        {
            _app = app;
            _opts = opts;
            _registry = registry;
            _serializer = serializer;
            _sink = sink;
        }

        public void Start()
        {
            _thread = new Thread(RunThread)
            {
                IsBackground = true,
                Name = "Keincheck-Host",
            };
            _thread.Start();

            // Give Kestrel a brief window to bind so early failures surface,
            // but never block the UI thread indefinitely.
            _started.Wait(TimeSpan.FromSeconds(5));
        }

        private void RunThread()
        {
            try
            {
                var builder = WebApplication.CreateSlimBuilder();

                builder.WebHost.ConfigureKestrel(k =>
                {
                    // Loopback ONLY. Never 0.0.0.0.
                    k.Listen(IPAddress.Loopback, _opts.Port);
                });

                // Keep Kestrel quiet; the app owns its own logging.
                builder.Logging.SetMinimumLevel(LogLevel.Warning);

                // Register the shared spine for tool handlers to consume via DI.
                builder.Services.AddSingleton(_app);
                builder.Services.AddSingleton(_opts);
                builder.Services.AddSingleton(_registry);
                builder.Services.AddSingleton(_serializer);
                if (_sink is not null)
                    builder.Services.AddSingleton(_sink);

                // The framework-agnostic UI seam. Tools (Phase B) consume IUiAdapter
                // instead of calling Avalonia directly; register it now so that
                // rerouting tool bodies requires no host change.
                builder.Services.AddSingleton<IUiAdapter>(
                    new AvaloniaUiAdapter(_serializer, _sink, _opts.MaxScreenshotDimension));

                // The MCP server + HTTP/SSE transport + tools. Tools now live in the
                // CORE assembly (Keincheck.Core.Tools.*), so discovery targets it.
                // The entry assembly is still scanned so a host app can add its own
                // [McpServerTool] classes.
                builder.Services
                    .AddMcpServer()
                    .WithHttpTransport()
                    .WithToolsFromAssembly(typeof(InspectionTools).Assembly)
                    .WithToolsFromAssembly(Assembly.GetEntryAssembly() ?? typeof(McpHost).Assembly);

                _web = builder.Build();
                _web.MapMcp();

                _web.Start();   // binds Kestrel synchronously
                _started.Set();

                // Block this background thread until shutdown is requested.
                _cts.Token.WaitHandle.WaitOne();
            }
            catch (Exception ex)
            {
                // Surface startup failures to the debugger/console without
                // crashing the host application.
                System.Diagnostics.Debug.WriteLine($"[Keincheck] host failed: {ex}");
                Console.Error.WriteLine($"[Keincheck] host failed: {ex}");
            }
            finally
            {
                _started.Set();
                try
                {
                    _web?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    (_web as IDisposable)?.Dispose();
                }
                catch
                {
                    // best-effort shutdown
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                _cts.Cancel();
            }
            catch
            {
                // ignore
            }

            _thread?.Join(TimeSpan.FromSeconds(3));
            _sink?.Uninstall();
            _cts.Dispose();
            _started.Dispose();
        }
    }
}
