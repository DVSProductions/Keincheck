using System.Diagnostics;
using System.IO.Pipelines;
using Keincheck.Hub;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Launch affinity: the hub remembers which agent asked it to start a process, and hands that
/// agent the instance when it registers.
/// </summary>
/// <remarks>
/// This exists for the multi-worktree case. Several agents each build their own copy of the
/// same app from their own git worktree; all of them self-report the same app id, so they land
/// as <c>myapp#1</c>, <c>#2</c>, <c>#3</c> with nothing to tell them apart. Without affinity
/// an agent has to guess which instance is the one it just launched — and guessing wrong is
/// silent, because driving somebody else's build looks exactly like success.
/// </remarks>
public sealed class LaunchAffinityTests
{
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    private static (PipeChannel client, PipeChannel broker) DuplexPair()
    {
        var clientToBroker = new Pipe();
        var brokerToClient = new Pipe();

        var brokerChannel = new PipeChannel(
            new DuplexStream(clientToBroker.Reader.AsStream(), brokerToClient.Writer.AsStream()));
        var clientChannel = new PipeChannel(
            new DuplexStream(brokerToClient.Reader.AsStream(), clientToBroker.Writer.AsStream()));
        return (clientChannel, brokerChannel);
    }

    /// <summary>A broker whose launches are recorded instead of really starting anything.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
        private readonly List<PipeChannel> _clients = new();
        private readonly List<Task> _serves = new();

        public Rig(int firstPid = 1000)
        {
            var nextPid = firstPid;
            Broker = new PipeClientBroker(
                new BrokerOptions
                {
                    WatchdogInterval = TimeSpan.FromHours(1),
                    ProcessStarter = psi =>
                    {
                        Started.Add(psi);
                        return nextPid++;
                    },
                },
                KnownClientStore.Open(Path.Combine(
                    Path.GetTempPath(), $"keincheck-test-{Guid.NewGuid():N}.json")));
        }

        public PipeClientBroker Broker { get; }

        /// <summary>Every process the broker tried to start, in order.</summary>
        public List<ProcessStartInfo> Started { get; } = new();

        public CancellationToken Token => _cts.Token;

        /// <summary>
        /// Attaches a fake app and registers it, optionally echoing a launch token, and waits
        /// until registration has fully completed.
        /// </summary>
        /// <remarks>
        /// Waits on <c>ClientConnected</c> rather than polling the registry: the client is in
        /// <c>_live</c> (so <c>ClientStatus</c> already answers) well before registration
        /// finishes — the launch profile is persisted to disk and the default selection is
        /// applied afterwards. Polling the registry therefore returns while those are still
        /// pending, which is a race in the test, not in the broker.
        /// </remarks>
        public async Task<PipeChannel> RegisterAsync(
            string appId, int processId, string? launchToken = null)
        {
            var registered = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnConnected(object? _, ClientInfo info) => registered.TrySetResult(info);
            Broker.ClientConnected += OnConnected;

            try
            {
                var (client, brokerSide) = DuplexPair();
                _clients.Add(client);
                _serves.Add(Broker.AcceptChannel(brokerSide, _cts.Token));

                await client.SendAsync(MessageKind.Register, new RegisterMessage
                {
                    ClientId = appId,
                    ProcessId = processId,
                    ProtocolVersion = ProtocolVersion.Current,
                    LaunchToken = launchToken,
                });

                await registered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return client;
            }
            finally
            {
                Broker.ClientConnected -= OnConnected;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            foreach (var c in _clients)
            {
                try { await c.DisposeAsync(); } catch { }
            }
            foreach (var s in _serves)
            {
                try { await s; } catch { }
            }
            await Broker.DisposeAsync();
            _cts.Dispose();
        }
    }

    private static async Task<T?> WaitFor<T>(Func<T?> probe, int timeoutMs = 5000) where T : class
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(20);
        }
        return probe();
    }

    /// <summary>Writes a throwaway file to stand in for an app executable.</summary>
    private static string FakeExe(string directory, string fileName = "myapp.exe")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "not a real executable");
        return path;
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"keincheck-wt-{Guid.NewGuid():N}");

    [Fact]
    public async Task Launch_Passes_A_Token_In_The_Environment_And_Returns_It()
    {
        await using var rig = new Rig();
        var exe = FakeExe(TempDir());

        var result = await rig.Broker.LaunchClientAsync(
            "myapp", new LaunchOptions { ExePath = exe, SessionId = AgentA, SessionLabel = "claude-code-1" });

        var psi = Assert.Single(rig.Started);
        Assert.Equal(exe, psi.FileName);
        Assert.False(psi.UseShellExecute); // required to pass an environment variable
        Assert.Equal(result.LaunchId, psi.Environment[PipeNames.LaunchTokenEnvVar]);
    }

    [Fact]
    public async Task A_Registration_Carrying_The_Token_Is_Given_To_The_Launching_Agent()
    {
        await using var rig = new Rig();
        var exe = FakeExe(TempDir());

        var launched = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Broker.LaunchRegistered += (_, info) => launched.TrySetResult(info);

        var result = await rig.Broker.LaunchClientAsync(
            "myapp", new LaunchOptions { ExePath = exe, SessionId = AgentA, SessionLabel = "claude-code-1" });

        // The process the hub started is often not the one that registers (launcher script,
        // dotnet host), so the token — not the pid — is what makes this work.
        await rig.RegisterAsync("myapp", processId: 999999, launchToken: result.LaunchId);

        var info = await launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("myapp#1", info.ClientId);
        Assert.Equal(result.LaunchId, info.LaunchId);
        Assert.Equal(AgentA, info.LaunchSessionId);
        Assert.Equal("claude-code-1", info.LaunchSessionLabel);
    }

    [Fact]
    public async Task An_Old_Client_With_No_Token_Still_Matches_On_Process_Id()
    {
        await using var rig = new Rig(firstPid: 4242);
        var exe = FakeExe(TempDir());

        var launched = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Broker.LaunchRegistered += (_, info) => launched.TrySetResult(info);

        await rig.Broker.LaunchClientAsync(
            "myapp", new LaunchOptions { ExePath = exe, SessionId = AgentA, SessionLabel = "claude-code-1" });

        await rig.RegisterAsync("myapp", processId: 4242, launchToken: null);

        var info = await launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentA, info.LaunchSessionId);
    }

    [Fact]
    public async Task A_Client_That_Connected_On_Its_Own_Is_Not_Claimed_By_Anyone()
    {
        await using var rig = new Rig();
        var launched = false;
        rig.Broker.LaunchRegistered += (_, _) => launched = true;

        await rig.RegisterAsync("myapp", processId: 777);
        var info = await WaitFor(() => rig.Broker.ClientStatus("myapp#1"));

        Assert.NotNull(info);
        Assert.Null(info!.LaunchSessionId);
        Assert.False(launched);

        // ...and it still becomes the hub-wide default, as a lone app always did.
        Assert.Equal("myapp#1", rig.Broker.DefaultClientId);
    }

    [Fact]
    public async Task A_Launched_Instance_Does_Not_Become_The_Hub_Wide_Default()
    {
        // It belongs to the agent that asked for it; making it the default would hand it to
        // whichever agent connects next.
        await using var rig = new Rig();
        var exe = FakeExe(TempDir());

        var result = await rig.Broker.LaunchClientAsync(
            "myapp", new LaunchOptions { ExePath = exe, SessionId = AgentA, SessionLabel = "claude-code-1" });
        await rig.RegisterAsync("myapp", processId: 1, launchToken: result.LaunchId);

        await WaitFor(() => rig.Broker.ClientStatus("myapp#1"));
        Assert.Null(rig.Broker.DefaultClientId);
    }

    [Fact]
    public async Task Three_Worktree_Builds_Each_Belong_To_The_Agent_That_Launched_Them()
    {
        await using var rig = new Rig();
        var claims = new ClientClaimRegistry(TimeSpan.FromMinutes(15));
        rig.Broker.Claims = claims;

        var agentC = Guid.NewGuid();
        var agents = new[]
        {
            (Session: AgentA, Label: "claude-code-1"),
            (Session: AgentB, Label: "claude-code-2"),
            (Session: agentC, Label: "claude-code-3"),
        };

        // Three worktrees, three builds of the SAME app, one agent each.
        var launches = new List<LaunchResult>();
        foreach (var agent in agents)
        {
            var exe = FakeExe(TempDir());
            launches.Add(await rig.Broker.LaunchClientAsync("myapp", new LaunchOptions
            {
                ExePath = exe,
                SessionId = agent.Session,
                SessionLabel = agent.Label,
            }));
        }

        for (var i = 0; i < agents.Length; i++)
            await rig.RegisterAsync("myapp", processId: 100 + i, launchToken: launches[i].LaunchId);

        await WaitFor(() => rig.Broker.ListClients().Count == 3 ? "ready" : null);

        // Every agent can find ITS instance by launch id, and they are all different.
        var resolved = new List<string>();
        for (var i = 0; i < agents.Length; i++)
        {
            var found = await rig.Broker.WaitForClientAsync(
                new ClientWaitFilter { LaunchId = launches[i].LaunchId },
                TimeSpan.FromSeconds(5));

            Assert.NotNull(found);
            Assert.Equal(agents[i].Session, found!.LaunchSessionId);
            resolved.Add(found.ClientId);
        }

        Assert.Equal(3, resolved.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { "myapp#1", "myapp#2", "myapp#3" }, resolved.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Waiting_By_AppId_Prefers_An_Unclaimed_Instance_Over_Someone_Elses()
    {
        await using var rig = new Rig();
        var claims = new ClientClaimRegistry(TimeSpan.FromMinutes(15));
        rig.Broker.Claims = claims;

        await rig.RegisterAsync("myapp", processId: 1);
        await rig.RegisterAsync("myapp", processId: 2);
        await WaitFor(() => rig.Broker.ListClients().Count == 2 ? "ready" : null);

        // Agent A is driving #1, so a bare "myapp" wait by agent B must land on #2 rather
        // than resolving by enumeration order and handing over an app in use.
        claims.CheckWrite("myapp#1", AgentA, "claude-code-1");

        var found = await rig.Broker.WaitForClientAsync(
            new ClientWaitFilter { IdOrAppId = "myapp", SessionId = AgentB },
            TimeSpan.FromSeconds(5));

        Assert.Equal("myapp#2", found?.ClientId);
    }

    [Fact]
    public async Task UnclaimedOnly_Skips_Instances_Other_Agents_Hold()
    {
        await using var rig = new Rig();
        var claims = new ClientClaimRegistry(TimeSpan.FromMinutes(15));
        rig.Broker.Claims = claims;

        await rig.RegisterAsync("myapp", processId: 1);
        await WaitFor(() => rig.Broker.ClientStatus("myapp#1"));
        claims.CheckWrite("myapp#1", AgentA, "claude-code-1");

        var found = await rig.Broker.WaitForClientAsync(
            new ClientWaitFilter { IdOrAppId = "myapp", UnclaimedOnly = true, SessionId = AgentB },
            TimeSpan.FromMilliseconds(300));

        Assert.Null(found);
    }

    [Fact]
    public async Task An_Override_Executable_Must_Share_The_Recorded_Name()
    {
        await using var rig = new Rig();

        // Register once so a launch profile exists naming this process's real executable.
        await rig.RegisterAsync("myapp", processId: Environment.ProcessId);
        await WaitFor(() => rig.Broker.ClientStatus("myapp#1"));

        var wrongName = FakeExe(TempDir(), "totally-different.exe");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Broker.LaunchClientAsync("myapp", new LaunchOptions { ExePath = wrongName }));

        Assert.Contains("allowDifferentExecutable", ex.Message, StringComparison.Ordinal);

        // ...and the opt-out works, for the caller who really meant it.
        await rig.Broker.LaunchClientAsync(
            "myapp", new LaunchOptions { ExePath = wrongName, AllowDifferentExecutable = true });
        Assert.Contains(rig.Started, p => p.FileName == wrongName);
    }

    [Fact]
    public async Task The_Same_Executable_Name_In_Another_Directory_Is_Allowed()
    {
        // The whole worktree case: same binary, different build directory.
        await using var rig = new Rig();
        await rig.RegisterAsync("myapp", processId: Environment.ProcessId);
        await WaitFor(() => rig.Broker.ClientStatus("myapp#1"));

        var recorded = rig.Broker.ClientStatus("myapp#1")!.ExecutablePath;
        Assert.NotNull(recorded);

        var sibling = FakeExe(TempDir(), Path.GetFileName(recorded)!);
        await rig.Broker.LaunchClientAsync("myapp", new LaunchOptions { ExePath = sibling });

        Assert.Contains(rig.Started, p => p.FileName == sibling);
    }

    [Fact]
    public async Task A_Missing_Override_Path_Is_Refused_Before_Anything_Starts()
    {
        await using var rig = new Rig();
        var missing = Path.Combine(TempDir(), "never-built.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Broker.LaunchClientAsync("myapp", new LaunchOptions { ExePath = missing }));

        Assert.Empty(rig.Started);
    }

    [Fact]
    public async Task Restarting_A_Bare_AppId_With_Several_Instances_Is_Refused()
    {
        await using var rig = new Rig();
        await rig.RegisterAsync("myapp", processId: 1);
        await rig.RegisterAsync("myapp", processId: 2);
        await WaitFor(() => rig.Broker.ListClients().Count == 2 ? "ready" : null);

        // The old behaviour silently started a THIRD copy, leaving the agent talking to an
        // instance it never asked for.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Broker.RestartClientAsync("myapp"));

        Assert.Contains("myapp#1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("myapp#2", ex.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Started);
    }

    [Fact]
    public async Task A_Launch_That_Never_Registers_Is_Forgotten()
    {
        await using var rig = new Rig(firstPid: 5150);
        var exe = FakeExe(TempDir());

        // A tight sweep window plus a watchdog that actually runs.
        await using var broker = new PipeClientBroker(
            new BrokerOptions
            {
                WatchdogInterval = TimeSpan.FromMilliseconds(50),
                LaunchRegisterTimeout = TimeSpan.FromMilliseconds(100),
                ProcessStarter = _ => 5150,
            },
            KnownClientStore.Open(Path.Combine(Path.GetTempPath(), $"keincheck-test-{Guid.NewGuid():N}.json")));
        broker.Start();

        var result = await broker.LaunchClientAsync(
            "myapp", new LaunchOptions { ExePath = exe, SessionId = AgentA, SessionLabel = "claude-code-1" });

        await Task.Delay(400);

        // A later, unrelated process reusing that pid must not inherit the agent's instance.
        var launched = false;
        broker.LaunchRegistered += (_, _) => launched = true;

        var registered = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.ClientConnected += (_, info) => registered.TrySetResult(info);

        var (client, brokerSide) = DuplexPair();
        var serve = broker.AcceptChannel(brokerSide, CancellationToken.None);
        await client.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = "myapp", ProcessId = 5150, ProtocolVersion = ProtocolVersion.Current,
        });

        var info = await registered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(launched);
        Assert.Null(info.LaunchSessionId);
        Assert.NotEqual(string.Empty, result.LaunchId);

        await client.DisposeAsync();
        try { await serve; } catch { }
    }

    /// <summary>A read/write stream stitched from two half-duplex streams.</summary>
    private sealed class DuplexStream : Stream
    {
        private readonly Stream _read;
        private readonly Stream _write;

        public DuplexStream(Stream read, Stream write)
        {
            _read = read;
            _write = write;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _write.Flush();
        public override Task FlushAsync(CancellationToken ct) => _write.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _read.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _read.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => _write.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _write.WriteAsync(buffer, offset, count, ct);

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;
            _read.Dispose();
            _write.Dispose();
        }
    }
}
