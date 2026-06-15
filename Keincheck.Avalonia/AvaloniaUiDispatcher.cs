using Avalonia.Threading;
using Keincheck.Core;

namespace Keincheck.Avalonia;

/// <summary>
/// <see cref="IUiDispatcher"/> over Avalonia's <see cref="Dispatcher.UIThread"/>.
/// Marshals work onto the Avalonia UI thread. Every tool handler that touches the
/// visual tree (through the <see cref="AvaloniaUiAdapter"/>) runs inside one of these
/// helpers, because the MCP host runs on a background thread.
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public async Task<T> Run<T>(Func<T> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);

        if (Dispatcher.UIThread.CheckAccess())
            return fn();

        return await Dispatcher.UIThread.InvokeAsync(fn, DispatcherPriority.Normal);
    }

    /// <inheritdoc />
    public async Task Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal);
    }

    /// <inheritdoc />
    public async Task<T> RunAsync<T>(Func<Task<T>> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        // Avalonia's InvokeAsync(Func{Task{T}}) already unwraps the inner task,
        // so a single await yields T.
        return await Dispatcher.UIThread.InvokeAsync(fn, DispatcherPriority.Normal);
    }

    /// <inheritdoc />
    public async Task RunAsync(Func<Task> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        await Dispatcher.UIThread.InvokeAsync(fn, DispatcherPriority.Normal);
    }

    /// <inheritdoc />
    public async Task WaitForIdle()
    {
        // Post a no-op at Background priority: the dispatcher only runs Background work
        // AFTER all higher-priority queued items — including Layout and Render passes —
        // have drained. Awaiting it is therefore a true "the UI has settled" barrier,
        // not just the round-trip the default WaitForIdle provides. Always go through the
        // queue (even when already on the UI thread) so pending layout/render still runs.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
