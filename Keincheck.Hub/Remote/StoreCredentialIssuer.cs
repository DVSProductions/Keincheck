using Keincheck.Protocol;
using Keincheck.Remote;

namespace Keincheck.Hub.Remote;

/// <summary>
/// Issues credentials from the hub's <see cref="RemoteStore"/>, recording each one so it can
/// be listed and revoked later.
/// </summary>
public sealed class StoreCredentialIssuer : ICredentialIssuer
{
    private readonly RemoteStore _store;
    private readonly Action<IssuedCredential>? _onIssued;

    /// <param name="store">The certificate store to issue from.</param>
    /// <param name="onIssued">Called after a successful issue, for the audit trail.</param>
    public StoreCredentialIssuer(RemoteStore store, Action<IssuedCredential>? onIssued = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _onIssued = onIssued;
    }

    /// <inheritdoc/>
    public EnrollResponseMessage Issue(EnrollRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Refuse rather than provision on demand. Creating a certificate authority is an
        // operator decision -- a build step should never be able to switch remote access on
        // for a hub whose owner never asked for it.
        if (!_store.IsProvisioned)
        {
            return Refuse(
                "Remote access is not set up on this hub. Enable it from the hub window first.");
        }

        var host = request.TargetName;
        try
        {
            RemoteCertificates.ValidateHostLabel(host);
        }
        catch (ArgumentException ex)
        {
            return Refuse(ex.Message);
        }

        // The requester asks; the hub decides. A build step requesting ten years gets ninety
        // days, because the credential it is asking for ends up inside a binary.
        var lifetime = request.RequestedDays > 0
            ? TimeSpan.FromDays(request.RequestedDays)
            : RemoteStore.BuildLifetime;
        if (lifetime > RemoteStore.MaxLifetime)
            lifetime = RemoteStore.MaxLifetime;

        try
        {
            var (bundle, record) = _store.Issue(host, lifetime, issuedVia: "pipe", note: Trim(request.Note));
            _onIssued?.Invoke(record);

            return new EnrollResponseMessage
            {
                Accepted = true,
                Bundle = bundle,
                Serial = record.Serial,
                NotAfter = record.NotAfter,
            };
        }
        catch (Exception ex)
        {
            return Refuse(ex.Message);
        }
    }

    private static EnrollResponseMessage Refuse(string reason) =>
        new() { Accepted = false, Reason = reason };

    /// <summary>Bounds a free-text note so a caller cannot bloat the issued list.</summary>
    private static string? Trim(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;
        note = note.Trim();
        return note.Length <= 200 ? note : note[..200];
    }
}
