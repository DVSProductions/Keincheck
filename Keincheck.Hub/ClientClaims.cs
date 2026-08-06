namespace Keincheck.Hub;

/// <summary>How an agent came to hold a write-claim.</summary>
public enum ClaimOrigin
{
    /// <summary>Taken implicitly by the agent's first mutating call.</summary>
    FirstWrite = 0,

    /// <summary>Taken deliberately via <c>hub_claim_client</c>.</summary>
    Explicit,

    /// <summary>Granted because this agent asked the hub to launch the instance.</summary>
    Launch,

    /// <summary>Taken from another agent with <c>force</c>.</summary>
    Steal,
}

/// <summary>One agent's exclusive right to drive one app instance.</summary>
/// <param name="ClientId">The hub id of the claimed instance (e.g. <c>myapp#2</c>).</param>
/// <param name="SessionId">The owning agent session.</param>
/// <param name="SessionLabel">That agent's display label, for messages and the tray.</param>
/// <param name="AcquiredUtc">When the claim was taken.</param>
/// <param name="LastActivityUtc">When the owner last touched the instance (drives idle release).</param>
/// <param name="Origin">How it was acquired.</param>
public sealed record ClaimInfo(
    string ClientId,
    Guid SessionId,
    string SessionLabel,
    DateTimeOffset AcquiredUtc,
    DateTimeOffset LastActivityUtc,
    ClaimOrigin Origin);

/// <summary>The outcome of asking to write to an instance.</summary>
public abstract record ClaimVerdict;

/// <summary>The caller may write; <paramref name="Claim"/> is theirs.</summary>
public sealed record ClaimGranted(ClaimInfo Claim, bool NewlyAcquired) : ClaimVerdict;

/// <summary>Another agent holds the instance.</summary>
public sealed record ClaimDenied(ClaimInfo Owner, TimeSpan IdleFor, TimeSpan? IdleTimeout) : ClaimVerdict;

/// <summary>
/// Tracks which agent is allowed to <b>drive</b> each app instance. Reads stay open to
/// everyone; only mutating calls are gated.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the app side cannot cope with two drivers. An instrumented app runs
/// incoming invokes concurrently and its input machinery is process-global: one synthetic
/// pointer with capture state, one focus, one flat <c>ctl-N</c> handle table. Two agents
/// clicking and typing into the same instance corrupt each other's capture and steal each
/// other's focus, and the symptoms look like app bugs. So the hub serialises at the level
/// that actually matters — one driver per instance — rather than pretending interleaved
/// input is safe.
/// </para>
/// <para>
/// Claims are keyed on the <b>hub id</b> (<c>myapp#2</c>), never the app id. That is the
/// worktree case: three agents each build and launch their own copy of the same app from
/// their own git worktree, all three self-report <c>myapp</c>, and they must not contend.
/// Because a reconnecting client reclaims its suffix, a claim also survives the
/// rebuild-relaunch loop without any extra machinery.
/// </para>
/// <para><b>Threading.</b> One private lock; audit writes and <see cref="Changed"/> are
/// raised outside it. The registry never calls into the broker or the MCP server, so
/// "broker/server → registry" is always a safe lock order and there is no cycle to reason
/// about.</para>
/// </remarks>
public sealed class ClientClaimRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ClaimInfo> _claims = new(StringComparer.Ordinal);
    private readonly HubAuditLog? _audit;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Creates a registry.
    /// </summary>
    /// <param name="idleTimeout">
    /// How long a claim may sit untouched before another agent's write may take it over.
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables idle release entirely.
    /// </param>
    /// <param name="audit">Where claim transitions are recorded, when there is a trail.</param>
    /// <param name="clock">Test seam for the passage of time.</param>
    public ClientClaimRegistry(
        TimeSpan idleTimeout, HubAuditLog? audit = null, Func<DateTimeOffset>? clock = null)
    {
        IdleTimeout = idleTimeout;
        _audit = audit;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>How long a claim survives without activity. Infinite disables idle release.</summary>
    public TimeSpan IdleTimeout { get; }

    /// <summary>Raised after any claim is taken, refreshed-into-existence, or released.</summary>
    public event Action? Changed;

    /// <summary>
    /// Decides whether <paramref name="sessionId"/> may perform a mutating call on
    /// <paramref name="clientId"/>, taking the claim if it is free or has gone idle.
    /// </summary>
    /// <remarks>
    /// Deliberately one atomic operation rather than a check followed by an acquire: two
    /// agents writing to a free instance at the same moment must not both be told yes.
    /// </remarks>
    public ClaimVerdict CheckWrite(string clientId, Guid sessionId, string sessionLabel)
    {
        ClaimVerdict verdict;
        var announce = false;
        string? audit = null;

        lock (_gate)
        {
            var now = _clock();
            if (_claims.TryGetValue(clientId, out var existing))
            {
                if (existing.SessionId == sessionId)
                {
                    _claims[clientId] = existing with { LastActivityUtc = now };
                    return new ClaimGranted(_claims[clientId], NewlyAcquired: false);
                }

                var idleFor = now - existing.LastActivityUtc;
                if (!IsExpired(idleFor))
                    return new ClaimDenied(existing, idleFor, EffectiveTimeout());

                // The owner went quiet long enough to be presumed gone (a crashed agent whose
                // transport has not closed yet). Hand the instance over rather than wedging it.
                audit = $"'{clientId}' claim taken over from {existing.SessionLabel} after {idleFor:g} idle";
            }

            var claim = new ClaimInfo(clientId, sessionId, sessionLabel, now, now, ClaimOrigin.FirstWrite);
            _claims[clientId] = claim;
            verdict = new ClaimGranted(claim, NewlyAcquired: true);
            announce = true;
            audit ??= $"'{clientId}' claimed by {sessionLabel}";
        }

        Record(AuditKind.ClaimAcquired, audit, clientId, sessionLabel);
        if (announce)
            Changed?.Invoke();
        return verdict;
    }

    /// <summary>
    /// Takes the claim deliberately (<c>hub_claim_client</c> or a launch the agent asked for).
    /// With <paramref name="force"/>, takes it even from a live owner.
    /// </summary>
    public ClaimVerdict Acquire(
        string clientId, Guid sessionId, string sessionLabel, ClaimOrigin origin, bool force)
    {
        ClaimVerdict verdict;
        string audit;

        lock (_gate)
        {
            var now = _clock();
            if (_claims.TryGetValue(clientId, out var existing) && existing.SessionId != sessionId)
            {
                var idleFor = now - existing.LastActivityUtc;
                if (!force && !IsExpired(idleFor))
                    return new ClaimDenied(existing, idleFor, EffectiveTimeout());

                audit = force
                    ? $"'{clientId}' claim forced away from {existing.SessionLabel} by {sessionLabel}"
                    : $"'{clientId}' claim taken over from {existing.SessionLabel} after {idleFor:g} idle";
            }
            else
            {
                audit = $"'{clientId}' claimed by {sessionLabel}";
            }

            var claim = new ClaimInfo(clientId, sessionId, sessionLabel, now, now, origin);
            _claims[clientId] = claim;
            verdict = new ClaimGranted(claim, NewlyAcquired: true);
        }

        Record(AuditKind.ClaimAcquired, audit, clientId, sessionLabel);
        Changed?.Invoke();
        return verdict;
    }

    /// <summary>
    /// Releases one claim. Without <paramref name="force"/> only the owner may do so;
    /// <paramref name="force"/> is the operator's override from the tray.
    /// </summary>
    public bool Release(string clientId, Guid? sessionId, bool force)
    {
        ClaimInfo? removed = null;
        lock (_gate)
        {
            if (_claims.TryGetValue(clientId, out var existing)
                && (force || (sessionId is { } id && existing.SessionId == id)))
            {
                _claims.Remove(clientId);
                removed = existing;
            }
        }

        if (removed is null)
            return false;

        Record(AuditKind.ClaimReleased, $"'{clientId}' released by {removed.SessionLabel}",
            clientId, removed.SessionLabel);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Releases everything one agent holds. Called when its session ends, which is what stops
    /// a disconnected agent from wedging an app forever.
    /// </summary>
    public int ReleaseSession(Guid sessionId)
    {
        List<ClaimInfo> removed;
        lock (_gate)
        {
            removed = _claims.Values.Where(c => c.SessionId == sessionId).ToList();
            foreach (var c in removed)
                _claims.Remove(c.ClientId);
        }

        foreach (var c in removed)
            Record(AuditKind.ClaimReleased, $"'{c.ClientId}' released — {c.SessionLabel} disconnected",
                c.ClientId, c.SessionLabel);

        if (removed.Count > 0)
            Changed?.Invoke();
        return removed.Count;
    }

    /// <summary>
    /// Marks the owner as still active. Read-only calls do this so an agent that spends a
    /// while inspecting an app does not lose the claim it is about to write through.
    /// </summary>
    public void TouchIfOwner(string clientId, Guid sessionId)
    {
        lock (_gate)
        {
            if (_claims.TryGetValue(clientId, out var existing) && existing.SessionId == sessionId)
                _claims[clientId] = existing with { LastActivityUtc = _clock() };
        }
    }

    /// <summary>The live claim on an instance, or null when it is free (expired claims read as free).</summary>
    public ClaimInfo? Get(string clientId)
    {
        lock (_gate)
        {
            if (!_claims.TryGetValue(clientId, out var existing))
                return null;
            return IsExpired(_clock() - existing.LastActivityUtc) ? null : existing;
        }
    }

    /// <summary>Every live claim, for the tray and <c>hub_status</c>.</summary>
    public IReadOnlyList<ClaimInfo> Snapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            return _claims.Values.Where(c => !IsExpired(now - c.LastActivityUtc)).ToList();
        }
    }

    /// <summary>How long <paramref name="claim"/> has been untouched.</summary>
    public TimeSpan IdleFor(ClaimInfo claim) => _clock() - claim.LastActivityUtc;

    private bool IsExpired(TimeSpan idleFor) =>
        IdleTimeout != Timeout.InfiniteTimeSpan && IdleTimeout > TimeSpan.Zero && idleFor >= IdleTimeout;

    private TimeSpan? EffectiveTimeout() =>
        IdleTimeout == Timeout.InfiniteTimeSpan || IdleTimeout <= TimeSpan.Zero ? null : IdleTimeout;

    private void Record(AuditKind kind, string text, string clientId, string agent)
    {
        _audit?.Add(new AuditEntry
        {
            TimestampUtc = _clock(),
            ClientId = clientId,
            ToolName = text,
            Agent = agent,
            Kind = kind,
            Outcome = AuditOutcome.Ok,
        });
    }

    /// <summary>Records a refused write, so the trail shows contention rather than silence.</summary>
    internal void RecordDenied(string clientId, string tool, string requester, ClaimInfo owner)
    {
        _audit?.Add(new AuditEntry
        {
            TimestampUtc = _clock(),
            ClientId = clientId,
            ToolName = $"'{tool}' refused — held by {owner.SessionLabel}",
            Agent = requester,
            Kind = AuditKind.ClaimDenied,
            Outcome = AuditOutcome.Error,
            Error = $"claimed by {owner.SessionLabel}",
        });
    }
}
