using Keincheck.Hub;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Unit tests for the one-driver-per-app-instance rule, driven against an injectable clock so
/// idle expiry is deterministic rather than a sleep.
/// </summary>
public sealed class ClientClaimRegistryTests
{
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    /// <summary>A registry whose clock the test advances by hand.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
    }

    private static (ClientClaimRegistry registry, Clock clock, HubAuditLog audit) NewRegistry(
        TimeSpan? idleTimeout = null)
    {
        var clock = new Clock();
        var audit = new HubAuditLog();
        var registry = new ClientClaimRegistry(
            idleTimeout ?? TimeSpan.FromMinutes(15), audit, () => clock.Now);
        return (registry, clock, audit);
    }

    [Fact]
    public void First_Write_Takes_The_Claim()
    {
        var (registry, _, _) = NewRegistry();

        var verdict = registry.CheckWrite("myapp#1", AgentA, "claude-code-1");

        var granted = Assert.IsType<ClaimGranted>(verdict);
        Assert.True(granted.NewlyAcquired);
        Assert.Equal(ClaimOrigin.FirstWrite, granted.Claim.Origin);
        Assert.Equal("claude-code-1", registry.Get("myapp#1")?.SessionLabel);
    }

    [Fact]
    public void The_Owner_Keeps_Writing_Without_Reacquiring()
    {
        var (registry, _, _) = NewRegistry();
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");

        var again = Assert.IsType<ClaimGranted>(registry.CheckWrite("myapp#1", AgentA, "claude-code-1"));

        Assert.False(again.NewlyAcquired);
    }

    [Fact]
    public void A_Second_Agent_Is_Refused_And_Told_Who_Holds_It()
    {
        var (registry, clock, _) = NewRegistry();
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");
        clock.Advance(TimeSpan.FromSeconds(42));

        var denied = Assert.IsType<ClaimDenied>(registry.CheckWrite("myapp#1", AgentB, "claude-code-2"));

        Assert.Equal("claude-code-1", denied.Owner.SessionLabel);
        Assert.Equal(TimeSpan.FromSeconds(42), denied.IdleFor);
        Assert.Equal(TimeSpan.FromMinutes(15), denied.IdleTimeout);
    }

    [Fact]
    public void An_Idle_Claim_Is_Taken_Over_By_The_Next_Writer()
    {
        var (registry, clock, _) = NewRegistry(TimeSpan.FromMinutes(15));
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");

        // Agent A crashed without closing its transport; nobody should be wedged forever.
        clock.Advance(TimeSpan.FromMinutes(16));
        var granted = Assert.IsType<ClaimGranted>(registry.CheckWrite("myapp#1", AgentB, "claude-code-2"));

        Assert.True(granted.NewlyAcquired);
        Assert.Equal("claude-code-2", registry.Get("myapp#1")?.SessionLabel);
    }

    [Fact]
    public void Reads_By_The_Owner_Keep_The_Claim_Warm()
    {
        var (registry, clock, _) = NewRegistry(TimeSpan.FromMinutes(15));
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");

        // Ten minutes of inspection, then ten more: without the touch this would expire.
        clock.Advance(TimeSpan.FromMinutes(10));
        registry.TouchIfOwner("myapp#1", AgentA);
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.IsType<ClaimDenied>(registry.CheckWrite("myapp#1", AgentB, "claude-code-2"));
    }

    [Fact]
    public void A_NonOwners_Touch_Does_Not_Extend_The_Claim()
    {
        var (registry, clock, _) = NewRegistry(TimeSpan.FromMinutes(15));
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");

        clock.Advance(TimeSpan.FromMinutes(10));
        registry.TouchIfOwner("myapp#1", AgentB); // B does not hold it, so this must do nothing
        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.IsType<ClaimGranted>(registry.CheckWrite("myapp#1", AgentB, "claude-code-2"));
    }

    [Fact]
    public void An_Infinite_Timeout_Never_Expires()
    {
        var (registry, clock, _) = NewRegistry(Timeout.InfiniteTimeSpan);
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");

        clock.Advance(TimeSpan.FromDays(7));

        var denied = Assert.IsType<ClaimDenied>(registry.CheckWrite("myapp#1", AgentB, "claude-code-2"));
        Assert.Null(denied.IdleTimeout);
    }

    [Fact]
    public void Force_Takes_A_Live_Claim_And_Keeps_The_Origin_The_Caller_Gave()
    {
        var (registry, _, _) = NewRegistry();
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");

        Assert.IsType<ClaimDenied>(
            registry.Acquire("myapp#1", AgentB, "claude-code-2", ClaimOrigin.Explicit, force: false));

        var granted = Assert.IsType<ClaimGranted>(
            registry.Acquire("myapp#1", AgentB, "claude-code-2", ClaimOrigin.Steal, force: true));

        Assert.Equal(ClaimOrigin.Steal, granted.Claim.Origin);
        Assert.Equal("claude-code-2", registry.Get("myapp#1")?.SessionLabel);
    }

    [Fact]
    public void A_Forced_Launch_Claim_Is_Recorded_As_A_Launch_Not_A_Steal()
    {
        // The hub force-acquires on behalf of whichever agent asked it to start a process,
        // because a claim survives a client disconnect and the stale one from the previous
        // incarnation would otherwise win. That is a launch, not somebody barging in, and the
        // audit trail has to say so — an operator reading "steal" would go looking for a
        // conflict that never happened.
        var (registry, _, _) = NewRegistry();
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");

        var granted = Assert.IsType<ClaimGranted>(
            registry.Acquire("myapp#1", AgentB, "claude-code-2", ClaimOrigin.Launch, force: true));

        Assert.Equal(ClaimOrigin.Launch, granted.Claim.Origin);
        Assert.Equal(AgentB, registry.Get("myapp#1")?.SessionId);
    }

    [Fact]
    public void Only_The_Owner_Can_Release_Unless_Forced()
    {
        var (registry, _, _) = NewRegistry();
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");

        Assert.False(registry.Release("myapp#1", AgentB, force: false));
        Assert.NotNull(registry.Get("myapp#1"));

        // The tray's operator override does not need to be the owner.
        Assert.True(registry.Release("myapp#1", sessionId: null, force: true));
        Assert.Null(registry.Get("myapp#1"));
    }

    [Fact]
    public void Ending_A_Session_Releases_Everything_It_Held()
    {
        var (registry, _, _) = NewRegistry();
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");
        registry.CheckWrite("other#1", AgentA, "claude-code-1");
        registry.CheckWrite("myapp#2", AgentB, "claude-code-2");

        Assert.Equal(2, registry.ReleaseSession(AgentA));

        Assert.Null(registry.Get("myapp#1"));
        Assert.Null(registry.Get("other#1"));
        Assert.Equal("claude-code-2", registry.Get("myapp#2")?.SessionLabel);
    }

    [Fact]
    public void Claims_Are_Per_Instance_Not_Per_App()
    {
        // The worktree case: three builds of the same app, one agent each, no contention.
        var (registry, _, _) = NewRegistry();
        var agentC = Guid.NewGuid();

        Assert.IsType<ClaimGranted>(registry.CheckWrite("myapp#1", AgentA, "claude-code-1"));
        Assert.IsType<ClaimGranted>(registry.CheckWrite("myapp#2", AgentB, "claude-code-2"));
        Assert.IsType<ClaimGranted>(registry.CheckWrite("myapp#3", agentC, "claude-code-3"));

        Assert.Equal(3, registry.Snapshot().Count);
    }

    [Fact]
    public void Transitions_Are_Audited_With_The_Agent_That_Caused_Them()
    {
        var (registry, _, audit) = NewRegistry();

        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");
        registry.Release("myapp#1", AgentA, force: false);

        var kinds = audit.Snapshot().Select(e => e.Kind).ToList();
        Assert.Contains(AuditKind.ClaimAcquired, kinds);
        Assert.Contains(AuditKind.ClaimReleased, kinds);
        Assert.All(audit.Snapshot(), e => Assert.Equal("claude-code-1", e.Agent));
    }

    [Fact]
    public void Agents_Racing_A_Free_App_Elect_Exactly_One_Owner()
    {
        // The load-bearing guarantee of the whole design, and the one that cannot be observed
        // from a bug report: two agents whose first mutating call lands at the same instant
        // must not both be told yes. A check-then-acquire would pass every single-threaded
        // test in this file and still hand one app to two drivers here.
        const int agents = 16;
        const int rounds = 200;

        for (var round = 0; round < rounds; round++)
        {
            var (registry, _, _) = NewRegistry();
            var sessions = Enumerable.Range(0, agents).Select(_ => Guid.NewGuid()).ToArray();
            var granted = 0;
            var denied = 0;

            using var gate = new ManualResetEventSlim(false);
            var threads = new Thread[agents];
            for (var i = 0; i < agents; i++)
            {
                var index = i;
                threads[i] = new Thread(() =>
                {
                    // Real threads, not the pool: every contender has to be parked on the
                    // same gate at once, and a pool that decides to run them sequentially
                    // would quietly turn this into a single-threaded test that always passes.
                    gate.Wait();
                    if (registry.CheckWrite("myapp#1", sessions[index], $"agent-{index}") is ClaimGranted)
                        Interlocked.Increment(ref granted);
                    else
                        Interlocked.Increment(ref denied);
                });
                threads[i].Start();
            }

            gate.Set();
            foreach (var t in threads)
                Assert.True(t.Join(TimeSpan.FromSeconds(10)), "a contender deadlocked in CheckWrite.");

            Assert.Equal(1, granted);
            Assert.Equal(agents - 1, denied);
            Assert.NotNull(registry.Get("myapp#1"));
        }
    }

    [Fact]
    public void The_Owner_Is_Never_Locked_Out_By_Contenders()
    {
        // The mirror of the race above: while other agents hammer a claimed app, the agent
        // that holds it must pass every time. A lock that let a contender win a moment of
        // ownership would show up here as the owner being refused its own app.
        var (registry, _, _) = NewRegistry();
        registry.CheckWrite("myapp#1", AgentA, "owner");

        var ownerRefusals = 0;
        var intruderGrants = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var intruders = Enumerable.Range(0, 4).Select(i => new Thread(() =>
        {
            var session = Guid.NewGuid();
            while (!stop.IsCancellationRequested)
            {
                if (registry.CheckWrite("myapp#1", session, $"intruder-{i}") is ClaimGranted)
                    Interlocked.Increment(ref intruderGrants);
            }
        })).ToArray();

        var owner = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (registry.CheckWrite("myapp#1", AgentA, "owner") is not ClaimGranted)
                    Interlocked.Increment(ref ownerRefusals);
            }
        });

        foreach (var t in intruders) t.Start();
        owner.Start();

        owner.Join();
        foreach (var t in intruders) t.Join();

        Assert.Equal(0, ownerRefusals);
        Assert.Equal(0, intruderGrants);
    }

    [Fact]
    public void Changed_Fires_For_Acquire_And_Release()
    {
        var (registry, _, _) = NewRegistry();
        var fired = 0;
        registry.Changed += () => Interlocked.Increment(ref fired);

        registry.CheckWrite("myapp#1", AgentA, "claude-code-1");
        registry.CheckWrite("myapp#1", AgentA, "claude-code-1"); // refresh: not a change
        registry.Release("myapp#1", AgentA, force: false);

        Assert.Equal(2, fired);
    }
}
