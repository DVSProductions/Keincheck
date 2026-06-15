using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace AvaloniaMcp.Protocol;

/// <summary>
/// Connect/accept helpers for the broker's named-pipe transport. The hub uses
/// <see cref="CreateServerStream"/> + <see cref="AcceptAsync"/> to listen on the
/// well-known control pipe; clients (and the stdio shim) use
/// <see cref="ConnectAsync"/> to reach it.
/// </summary>
/// <remarks>
/// <para><b>Security.</b> The pipe lives in the local (per-machine) pipe namespace
/// and is named per-user (<see cref="PipeNames.ControlPipe"/>). On Windows the
/// default DACL of a server pipe grants access to the creating user, plus SYSTEM
/// and Administrators — i.e. not to other interactive users. The hub may further
/// restrict the ACL via <c>NamedPipeServerStreamAcl</c> (in
/// <c>System.IO.Pipes.AccessControl</c>); that package is intentionally kept out of
/// this zero-dependency Protocol assembly. For the live Windows target the default
/// DACL is the current-user-only baseline; the hub owns any hardening.</para>
/// <para><b>UDS fallback.</b> On non-Windows, .NET maps named pipes onto Unix domain
/// sockets under the temp dir. That path is functional for tests but is the live
/// target only on Windows; treat the POSIX path as best-effort.</para>
/// </remarks>
public static class PipeTransport
{
    /// <summary>Default inbound connection backlog the hub keeps available.</summary>
    public const int DefaultMaxServerInstances = 32;

    /// <summary>
    /// Creates one server-side pipe instance bound to <paramref name="pipeName"/>
    /// (defaults to <see cref="PipeNames.ControlPipe"/>). Call <see cref="AcceptAsync"/>
    /// on the returned stream to wait for a client, then create another instance for
    /// the next connection. The stream is asynchronous and message-agnostic (we frame
    /// with <see cref="FrameCodec"/>, so byte-stream mode is correct).
    /// </summary>
    public static NamedPipeServerStream CreateServerStream(
        string? pipeName = null,
        int maxServerInstances = DefaultMaxServerInstances)
    {
        pipeName ??= PipeNames.ControlPipe;
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    /// <summary>
    /// Waits for a client to connect to <paramref name="server"/> and returns a
    /// <see cref="PipeChannel"/> wrapping it (owns the stream). Cancellation aborts
    /// the wait and disposes the half-open instance.
    /// </summary>
    public static async Task<PipeChannel> AcceptAsync(
        NamedPipeServerStream server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        try
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new PipeChannel(server, ownsStream: true);
    }

    /// <summary>
    /// Connects to the hub's pipe (defaults to <see cref="PipeNames.ControlPipe"/>),
    /// retrying with backoff up to <paramref name="timeout"/>. Returns a
    /// <see cref="PipeChannel"/> on success; throws <see cref="TimeoutException"/> if
    /// the hub never came up in time, or <see cref="OperationCanceledException"/> on
    /// cancellation.
    /// </summary>
    public static async Task<PipeChannel> ConnectAsync(
        string? pipeName = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        pipeName ??= PipeNames.ControlPipe;
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var delayMs = 25;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw new TimeoutException($"No AvaloniaMcp hub on pipe '{pipeName}' within the timeout.");
                }

                // ConnectAsync(int) treats the value as a one-shot wait; we wrap our
                // own backoff loop so a not-yet-listening pipe retries cleanly.
                var attemptMs = (int)Math.Min(remaining.TotalMilliseconds, 250);
                await client.ConnectAsync(attemptMs, cancellationToken).ConfigureAwait(false);
                return new PipeChannel(client, ownsStream: true);
            }
            catch (TimeoutException)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                // pipe not up yet — back off and retry until the deadline
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, 500);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>True when running on Windows, where the pipe transport is the live target.</summary>
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
