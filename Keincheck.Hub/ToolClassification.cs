using Keincheck.Protocol;

namespace Keincheck.Hub;

/// <summary>
/// Decides whether a client tool is read-only (inspection) or mutating (drives the UI).
/// </summary>
/// <remarks>
/// <para>
/// Two independent gates ask this question, so it lives in one place rather than being
/// duplicated: the broker's read-only refusal (<see cref="PipeClientBroker"/>) and the
/// hub's write-claim enforcement (<see cref="HubMcpServer"/>). They must agree — a tool
/// the claim gate treats as a write but the read-only gate treats as a read would let a
/// second agent drive an app it does not own.
/// </para>
/// <para>
/// The client's own <see cref="ToolDescriptor.ReadOnly"/> wins when it reported one: it
/// owns the tool implementations and already computes this to enforce its local gate, so
/// it is the authority. The name heuristic remains only as the fallback for v1 clients
/// that predate the field, and stays deliberately fail-closed: anything it does not
/// recognise counts as mutating.
/// </para>
/// </remarks>
internal static class ToolClassification
{
    /// <summary>
    /// True if <paramref name="toolName"/> is side-effect-free according to
    /// <paramref name="tools"/> (the client's reported catalog), or — for a tool the client
    /// did not classify — according to the fail-closed name heuristic.
    /// </summary>
    internal static bool IsReadOnly(IReadOnlyList<ToolDescriptor> tools, string toolName)
    {
        foreach (var tool in tools)
        {
            if (!string.Equals(tool.Name, toolName, StringComparison.Ordinal))
                continue;
            if (tool.ReadOnly is { } declared)
                return declared;
            break; // known tool, but the client did not classify it — fall through
        }

        return toolName.StartsWith("get_", StringComparison.Ordinal)
            || toolName is "list_windows" or "query_controls" or "hit_test" or "wait_for"
            || toolName.StartsWith("screenshot_", StringComparison.Ordinal);
    }
}
