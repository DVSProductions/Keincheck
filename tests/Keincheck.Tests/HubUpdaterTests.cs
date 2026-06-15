using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Unit tests for <see cref="HubUpdater"/>'s idle-gating policy: a staged update is applied
/// ONLY when no client is connected, and the terminal apply fires at most once. The Velopack
/// check/download/apply operations are injected, so these run with no network and no install.
/// </summary>
public sealed class HubUpdaterTests
{
    [Fact]
    public async Task Applies_Immediately_When_Hub_Is_Idle()
    {
        var broker = new FakeBroker(connected: 0);
        var applied = 0;
        var updater = new HubUpdater(
            broker,
            checkAndDownload: _ => Task.FromResult<string?>("9.9.9"),
            applyAndRestart: () => Interlocked.Increment(ref applied),
            interval: TimeSpan.FromHours(1));

        var staged = await updater.CheckNowAsync();

        Assert.True(staged);
        Assert.Equal(1, applied); // zero clients -> apply right away
    }

    [Fact]
    public async Task Waits_While_A_Client_Is_Connected_Then_Applies_When_It_Drops()
    {
        var broker = new FakeBroker(connected: 1);
        var applied = 0;
        var updater = new HubUpdater(
            broker,
            checkAndDownload: _ => Task.FromResult<string?>("9.9.9"),
            applyAndRestart: () => Interlocked.Increment(ref applied),
            interval: TimeSpan.FromHours(1));
        updater.Start(); // subscribes to ClientDown

        var staged = await updater.CheckNowAsync();
        Assert.True(staged);
        Assert.True(updater.UpdatePending);
        Assert.Equal(0, applied); // a client is connected -> stay staged, do not interrupt it

        broker.SetConnected(0);   // the client drops; the hub is now idle
        broker.RaiseClientDown();

        Assert.Equal(1, applied);
        await updater.DisposeAsync();
    }

    [Fact]
    public async Task Apply_Is_Terminal_And_Fires_At_Most_Once()
    {
        var broker = new FakeBroker(connected: 0);
        var applied = 0;
        var updater = new HubUpdater(
            broker,
            checkAndDownload: _ => Task.FromResult<string?>("9.9.9"),
            applyAndRestart: () => Interlocked.Increment(ref applied),
            interval: TimeSpan.FromHours(1));

        await updater.CheckNowAsync(); // applies once
        broker.RaiseClientDown();      // a later idle signal must not re-apply
        await updater.CheckNowAsync();

        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task No_Update_Available_Stays_Quiet()
    {
        var broker = new FakeBroker(connected: 0);
        var applied = 0;
        var updater = new HubUpdater(
            broker,
            checkAndDownload: _ => Task.FromResult<string?>(null), // up to date
            applyAndRestart: () => Interlocked.Increment(ref applied),
            interval: TimeSpan.FromHours(1));

        var staged = await updater.CheckNowAsync();

        Assert.False(staged);
        Assert.False(updater.UpdatePending);
        Assert.Equal(0, applied);
    }

    // A minimal IClientBroker fake: controllable connected-count + a raisable ClientDown.
    private sealed class FakeBroker : IClientBroker
    {
        private IReadOnlyList<ClientInfo> _clients;

        public FakeBroker(int connected) => _clients = Make(connected);

        public void SetConnected(int connected) => _clients = Make(connected);

        public void RaiseClientDown() => ClientDown?.Invoke(this, new ClientInfo { ClientId = "gone" });

        private static IReadOnlyList<ClientInfo> Make(int n) =>
            Enumerable.Range(0, n).Select(i => new ClientInfo { ClientId = $"app#{i}", IsConnected = true }).ToList();

        public IReadOnlyList<ClientInfo> ListClients() => _clients;

        public event EventHandler<ClientInfo>? ClientDown;

#pragma warning disable CS0067 // required by the interface, unused by this fake
        public event EventHandler<ClientInfo>? ClientConnected;
        public event EventHandler<ClientInfo>? ClientUpdated;
#pragma warning restore CS0067

        public string? ActiveClientId { get; set; }
        public IReadOnlyList<ClientInfo> ListKnownClients() => _clients;
        public ClientInfo? ClientStatus(string clientId) => null;
        public Task<int> LaunchClientAsync(string clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RestartClientAsync(string clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ClientInfo?> WaitForClientAsync(string? appIdOrClientId, TimeSpan timeout, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ToolResultMessage> InvokeOnClientAsync(string clientId, string toolName, JsonElement? argumentsJson, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
