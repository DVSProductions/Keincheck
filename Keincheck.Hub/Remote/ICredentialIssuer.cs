using Keincheck.Protocol;

namespace Keincheck.Hub.Remote;

/// <summary>
/// Issues remote client credentials in response to an <see cref="EnrollRequestMessage"/>
/// arriving on the <b>local control pipe</b>.
/// </summary>
/// <remarks>
/// <para>
/// The broker takes this as a seam rather than depending on the certificate store directly, so
/// a hub built without remote support — or one whose operator never enabled it — simply has no
/// issuer and answers every enrollment request with a refusal.
/// </para>
/// <para>
/// <b>Why the pipe is allowed to do this.</b> The control pipe is created
/// <c>PipeOptions.CurrentUserOnly</c>, so the operating system has already established that
/// the requester is this user — the same boundary that already permits driving every
/// registered app. Issuance therefore needs no additional prompt, which is what makes a
/// build-time enrollment step practical. The hub's MCP endpoint (<c>hub_remote_issue</c>) and
/// the tray window carry exactly the same authorization, and are treated the same way.
/// </para>
/// <para>
/// The line that <i>is</i> drawn is by transport, not by caller: a <b>remote</b> peer can
/// never reach this. The remote handshake refuses the message kind outright and the broker
/// refuses it again, because a remote client able to mint further credentials would turn one
/// leaked build certificate into an unbounded, self-renewing grant.
/// </para>
/// </remarks>
public interface ICredentialIssuer
{
    /// <summary>
    /// Issues a credential, or returns a refusal. Must not throw for ordinary refusals — the
    /// caller reports <see cref="EnrollResponseMessage.Reason"/> back to the requester.
    /// </summary>
    EnrollResponseMessage Issue(EnrollRequestMessage request);
}
