namespace Keincheck.Core;

/// <summary>
/// Marshals work onto the host UI toolkit's UI thread. Every tool handler that
/// touches the live visual tree (through <see cref="IUiAdapter"/>) MUST run inside
/// one of these helpers, because the MCP host runs on a background thread.
/// </summary>
/// <remarks>
/// This is the framework-neutral counterpart of the old static <c>UiDispatch</c>.
/// The Avalonia implementation wraps <c>Dispatcher.UIThread</c>; a WPF implementation
/// wraps <c>System.Windows.Application.Current.Dispatcher</c>. Implementations MUST
/// execute synchronously when already on the UI thread to avoid deadlocks.
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>
    /// Runs <paramref name="fn"/> on the UI thread and returns its result. If already
    /// on the UI thread, executes synchronously to avoid deadlocks.
    /// </summary>
    Task<T> Run<T>(Func<T> fn);

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread. If already on the UI thread,
    /// executes synchronously to avoid deadlocks.
    /// </summary>
    Task Run(Action action);

    /// <summary>Runs an async <paramref name="fn"/> on the UI thread and awaits it.</summary>
    Task<T> RunAsync<T>(Func<Task<T>> fn);

    /// <summary>Runs an async <paramref name="fn"/> on the UI thread and awaits it.</summary>
    Task RunAsync(Func<Task> fn);

    /// <summary>
    /// Completes once the UI thread has gone idle — pending layout, render, and queued
    /// work have drained. Tools <c>await</c> this after driving input so the next
    /// inspection observes a settled tree.
    /// </summary>
    /// <remarks>
    /// The default implementation is a plain UI-thread round-trip (<see cref="Run(Action)"/>
    /// of a no-op): it only guarantees that work already queued <em>ahead</em> of the call
    /// has run, which is a cheap, correct lower bound on idleness. Adapters override this to
    /// post a <em>Background</em>-priority no-op (so it runs only after layout/render
    /// passes), giving a true "the UI has settled" wait.
    /// </remarks>
    Task WaitForIdle() => Run(() => { });
}
