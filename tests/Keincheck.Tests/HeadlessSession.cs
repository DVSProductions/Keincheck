using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// xUnit collection fixture that owns the single, assembly-wide
/// <see cref="HeadlessUnitTestSession"/>. Avalonia only supports one running
/// <see cref="Avalonia.Application"/> per process, so every UI test shares this
/// session and dispatches its work onto the one headless UI thread.
/// </summary>
/// <remarks>
/// Usage: put <c>[Collection(HeadlessCollection.Name)]</c> on a test class and
/// take a <see cref="HeadlessSession"/> via the constructor. Run UI-thread work
/// through <see cref="RunOnUiThread{T}"/> / <see cref="RunOnUiThread"/>; the
/// delegate executes on the dispatcher thread, so spine calls that require the
/// UI thread (e.g. <c>ControlRegistry.Query</c>) are safe inside it.
/// </remarks>
public sealed class HeadlessSession : IDisposable
{
    private readonly HeadlessUnitTestSession _session;

    public HeadlessSession()
    {
        // Resolves the [assembly: AvaloniaTestApplication(typeof(TestApp))] marker,
        // invokes TestApp.BuildAvaloniaApp(), and starts the headless dispatcher
        // loop on a dedicated thread.
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestApp).Assembly);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the headless UI thread and returns its
    /// result. Exceptions thrown inside <paramref name="work"/> propagate to the
    /// caller (and therefore fail the test) once the dispatched task is awaited.
    /// </summary>
    public T RunOnUiThread<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // Dispatch(Func<T>) marshals onto the dispatcher and yields the result.
        return _session.Dispatch(work, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the headless UI thread.
    /// </summary>
    public void RunOnUiThread(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _session.Dispatch(work, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs async <paramref name="work"/> on the headless UI thread and unwraps
    /// the inner task. Useful once tool-level tests land, since tool methods are
    /// <c>async Task&lt;object&gt;</c> and use <c>UiDispatch.Run</c> internally.
    /// </summary>
    public T RunOnUiThread<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return _session.Dispatch(work, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Builds the canonical test window: a <see cref="Window"/> hosting a
/// <see cref="Button"/> named "Save" and a <see cref="TextBox"/> named "Input"
/// inside a <see cref="StackPanel"/>. Used by the spine tests as a known,
/// stable target for registry handles, selectors, and property round-trips.
/// </summary>
public static class TestWindowFactory
{
    public const string ButtonName = "Save";
    public const string TextBoxName = "Input";

    /// <summary>
    /// Creates (but does not show) the known window. Must be called on the UI
    /// thread (wrap in <see cref="HeadlessSession.RunOnUiThread{T}"/>).
    /// </summary>
    public static Window Create(out Button saveButton, out TextBox inputBox)
    {
        saveButton = new Button { Name = ButtonName, Content = "Save" };
        inputBox = new TextBox { Name = TextBoxName, Text = string.Empty };

        var panel = new StackPanel();
        panel.Children.Add(saveButton);
        panel.Children.Add(inputBox);

        return new Window
        {
            Title = "Keincheck Test Window",
            Width = 400,
            Height = 300,
            Content = panel,
        };
    }
}
