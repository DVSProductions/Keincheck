using Avalonia.Threading;

namespace Keincheck.Core;

/// <summary>
/// Marshals work onto the Avalonia UI thread. Every tool handler that touches
/// the visual tree, controls, rendering, <c>Application.Current</c>, or any
/// <c>TopLevel</c> MUST run inside one of these helpers, because the MCP host
/// runs on a background thread.
/// </summary>
public static class UiDispatch
{
    /// <summary>
    /// Runs <paramref name="fn"/> on the UI thread and returns its result.
    /// If already on the UI thread, executes synchronously to avoid deadlocks.
    /// </summary>
    public static async Task<T> Run<T>(Func<T> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);

        if (Dispatcher.UIThread.CheckAccess())
            return fn();

        return await Dispatcher.UIThread.InvokeAsync(fn, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread.
    /// If already on the UI thread, executes synchronously to avoid deadlocks.
    /// </summary>
    public static async Task Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Runs an async <paramref name="fn"/> on the UI thread and awaits it.
    /// Convenience for handlers that must await UI-thread work
    /// (e.g. rendering pipelines that yield).
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        // Avalonia's InvokeAsync(Func{Task{T}}) already unwraps the inner task,
        // so a single await yields T.
        return await Dispatcher.UIThread.InvokeAsync(fn, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Runs an async <paramref name="fn"/> on the UI thread and awaits it.
    /// </summary>
    public static async Task RunAsync(Func<Task> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        await Dispatcher.UIThread.InvokeAsync(fn, DispatcherPriority.Normal);
    }
}
