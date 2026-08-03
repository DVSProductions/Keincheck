using Keincheck.Remote;

namespace Keincheck.Hub.Remote;

/// <summary>
/// The hub's remote-access facility: the certificate store, the listener, and the credential
/// issuer, switched on and off as one thing.
/// </summary>
/// <remarks>
/// <para>
/// Remote access is off in a fresh install and stays off until an operator turns it on. That
/// is not a default that could drift: with no certificate authority there is nothing for a
/// peer to authenticate against, so <see cref="RemoteClientListener.TryStart"/> refuses to
/// open a socket at all, and the credential issuer refuses every request.
/// </para>
/// <para>
/// Enabling is therefore a single deliberate act that provisions a CA, starts the listener,
/// and installs the issuer. Disabling stops the listener and removes the issuer but keeps the
/// CA and the issued list, so a hub can be switched back on without invalidating credentials
/// already baked into builds — and so revocation records survive.
/// </para>
/// </remarks>
public sealed class RemoteAccess : IAsyncDisposable
{
    private readonly PipeClientBroker _broker;
    private readonly RemoteStore _store;
    private readonly HubAuditLog _audit;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private RemoteClientListener? _listener;
    private int _disposed;

    public RemoteAccess(
        PipeClientBroker broker, HubAuditLog audit, RemoteStore? store = null, Action<string>? log = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _store = store ?? RemoteStore.Open();
        _log = log;
    }

    /// <summary>The certificate store, for the tray UI's credential list.</summary>
    public RemoteStore Store => _store;

    /// <summary>Whether the listener is currently accepting connections.</summary>
    public bool IsListening
    {
        get { lock (_gate) return _listener?.IsRunning == true; }
    }

    /// <summary>The endpoint actually bound, or null when not listening.</summary>
    public string? BoundEndpoint
    {
        get { lock (_gate) return _listener?.BoundEndpoint?.ToString(); }
    }

    /// <summary>
    /// Brings remote access up if the persisted settings say it is enabled. Called at hub
    /// startup; a no-op for the overwhelmingly common case of a hub that has never been asked
    /// to accept remote clients.
    /// </summary>
    public void StartIfEnabled()
    {
        // Nothing is listening yet at startup, so this never takes the rebind path and cannot
        // block on a teardown.
        if (_store.Settings.Enabled && _store.IsProvisioned)
            EnableAsync(_store.Settings).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Turns remote access on: provisions a certificate authority if there is not one yet,
    /// installs the credential issuer, and starts the listener.
    /// </summary>
    /// <returns>A human-readable description of what happened, for the UI and the log.</returns>
    public async Task<string> EnableAsync(RemoteSettings? settings = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Tear down an existing listener BEFORE taking the lock for the rebuild, so a bind
        // change actually moves the socket. Previously the listener was reused as-is, which
        // meant changing the port persisted the new value and baked it into every subsequently
        // issued credential while the socket stayed on the old one — reporting success for a
        // change it had not made.
        RemoteClientListener? stale = null;
        lock (_gate)
        {
            var requested = (settings ?? _store.Settings) with { Enabled = true };
            if (_listener is { IsRunning: true } running && !Matches(running, requested))
            {
                stale = _listener;
                _listener = null;
            }
        }

        if (stale is not null)
        {
            await stale.DisposeAsync().ConfigureAwait(false);
            _log?.Invoke("rebinding the remote listener.");
        }

        lock (_gate)
        {
            var effective = (settings ?? _store.Settings) with { Enabled = true };
            _store.UpdateSettings(effective);

            bool provisioned;
            try
            {
                provisioned = _store.Provision();
            }
            catch (InvalidOperationException ex)
            {
                // Refusing to overwrite an unloadable CA. Say so rather than half-enabling.
                _store.UpdateSettings(effective with { Enabled = false });
                return ex.Message;
            }

            if (provisioned)
            {
                _log?.Invoke("provisioned a new certificate authority.");
                _audit.Add(RemoteAudit.Entry(AuditKind.RemoteToggled, "remote access enabled (new CA provisioned)"));
            }

            _broker.CredentialIssuer = new StoreCredentialIssuer(_store, OnIssued);

            // A durable trail starts when remote access does. Until then the in-memory ring is
            // enough, because everything it records happened on this machine, initiated here.
            _audit.Sink ??= new JsonlAuditSink();

            _listener ??= new RemoteClientListener(_broker, _store, _audit, _log);
            if (!_listener.TryStart())
                return $"Remote access is enabled but the listener could not start on {effective.BindAddress}:{effective.Port}.";

            _audit.Add(RemoteAudit.Entry(AuditKind.RemoteToggled, $"listening on {_listener.BoundEndpoint}"));
            return $"Listening on {_listener.BoundEndpoint}.";
        }
    }

    /// <summary>Whether a running listener is already bound to what the settings ask for.</summary>
    private static bool Matches(RemoteClientListener listener, RemoteSettings settings)
    {
        var bound = listener.BoundEndpoint;
        return bound is not null
            // Port 0 means "any free port", so an already-bound ephemeral port satisfies it —
            // rebinding would only move it somewhere equally arbitrary.
            && (settings.Port == 0 || bound.Port == settings.Port)
            && string.Equals(bound.Address.ToString(), settings.BindAddress, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns remote access off: stops the listener and removes the issuer. Keeps the CA and
    /// the issued list, so switching back on does not invalidate credentials already baked
    /// into builds — and so revocations are never quietly forgotten.
    /// </summary>
    public async Task DisableAsync()
    {
        RemoteClientListener? listener;
        lock (_gate)
        {
            _store.UpdateSettings(_store.Settings with { Enabled = false });
            _broker.CredentialIssuer = null;
            listener = _listener;
            _listener = null;
        }

        if (listener is not null)
            await listener.DisposeAsync().ConfigureAwait(false);

        _audit.Add(RemoteAudit.Entry(AuditKind.RemoteToggled, "remote access disabled"));
        _log?.Invoke("remote access disabled.");
    }

    /// <summary>
    /// Issues a credential from the tray UI or the CLI. Provisions on demand, because reaching
    /// this code already required an operator action.
    /// </summary>
    public (string Bundle, IssuedCredential Record) Issue(string host, TimeSpan? lifetime = null, string? note = null)
    {
        lock (_gate)
        {
            // Throws rather than replacing an existing-but-unloadable CA; issuing under a
            // fresh root would silently orphan every credential already deployed.
            _store.Provision();
            var result = _store.Issue(host, lifetime ?? RemoteStore.ManualLifetime, issuedVia: "ui", note: note);
            OnIssued(result.Record);
            return result;
        }
    }

    /// <summary>Revokes a credential by serial. Takes effect at the peer's next connect.</summary>
    public bool Revoke(string serial)
    {
        if (!_store.Revoke(serial))
            return false;

        _audit.Add(RemoteAudit.Entry(AuditKind.Revoke, $"revoked credential {serial}"));
        _log?.Invoke($"revoked credential {serial}.");
        return true;
    }

    private void OnIssued(IssuedCredential record) => _audit.Add(RemoteAudit.Entry(
        AuditKind.Enroll,
        $"issued credential {record.Serial} for '{record.Host}' via {record.IssuedVia}, valid until {record.NotAfter:u}",
        host: record.Host));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        RemoteClientListener? listener;
        lock (_gate)
        {
            listener = _listener;
            _listener = null;
        }

        if (listener is not null)
            await listener.DisposeAsync().ConfigureAwait(false);
        _store.Dispose();
    }
}
