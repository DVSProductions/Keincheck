using System.Diagnostics;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// <see cref="PipeTransport.ConnectAsync"/> must give up when it says it will.
/// </summary>
public class PipeTransportTimeoutTests
{
    /// <summary>A pipe name nothing is listening on.</summary>
    private static string DeadPipe() => $"Keincheck.nf{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Connecting_To_A_Pipe_Nobody_Is_Serving_Times_Out()
    {
        // The give-up throw sat inside the try whose `catch (TimeoutException)` exists to retry
        // a not-yet-listening pipe, so the method caught its own signal to stop: past the
        // deadline it looped forever, ~500ms per turn, never returning and never throwing.
        //
        // This is the connect every client app makes to reach the hub, and the one the
        // keincheck-connect shim makes on behalf of the AI. With no hub running, "no hub on
        // pipe X within the timeout" became an indefinite silent stall.
        var timeout = TimeSpan.FromMilliseconds(600);
        var connect = PipeTransport.ConnectAsync(DeadPipe(), timeout);

        // Race it: a regression does not fail here, it hangs, and a hung test takes the suite.
        var winner = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(ReferenceEquals(winner, connect),
            "ConnectAsync was still going 15s into a 0.6s timeout; the deadline check is being " +
            "swallowed by the retry catch again.");

        await Assert.ThrowsAsync<TimeoutException>(() => connect);
    }

    [Fact]
    public async Task The_Timeout_Is_Roughly_The_One_That_Was_Asked_For()
    {
        // Not just "it throws eventually" -- the retry backoff caps at 500ms, so an off-by-one
        // in the deadline arithmetic would still throw, just far too late to be the contract.
        var timeout = TimeSpan.FromSeconds(1);
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => PipeTransport.ConnectAsync(DeadPipe(), timeout));

        sw.Stop();
        Assert.InRange(sw.Elapsed, timeout, timeout + TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task The_Message_Names_The_Pipe_So_A_Stall_Is_Diagnosable()
    {
        var pipe = DeadPipe();
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => PipeTransport.ConnectAsync(pipe, TimeSpan.FromMilliseconds(300)));

        Assert.Contains(pipe, ex.Message);
    }

    [Fact]
    public async Task Cancellation_Still_Wins_Over_The_Deadline()
    {
        // Cancelling must surface as cancellation, not be reshaped into a timeout -- callers
        // distinguish "I stopped this" from "the hub never showed up".
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PipeTransport.ConnectAsync(DeadPipe(), TimeSpan.FromMinutes(5), cts.Token));
    }

    [Fact]
    public async Task A_Listening_Pipe_Still_Connects()
    {
        // The guard must not break the path that matters: a hub that IS up gets connected to,
        // well inside the timeout.
        var pipe = $"Keincheck.t{Guid.NewGuid():N}"[..20];
        using var server = PipeTransport.CreateServerStream(pipe);

        var accept = PipeTransport.AcceptAsync(server, CancellationToken.None);
        await using var client = await PipeTransport.ConnectAsync(pipe, TimeSpan.FromSeconds(10));
        await using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(client);
        Assert.NotNull(accepted);
    }
}
