using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaMcp.Protocol;
using ModelContextProtocol.Protocol;

namespace AvaloniaMcp.Hub;

/// <summary>
/// The hub's <b>meta-tools</b>: the always-present catalog the hub serves itself
/// (independently of any client), plus the helpers that build their schemas, dispatch
/// their calls against an <see cref="IClientBroker"/>, and shape the structured error
/// returned when the AI targets a down/unknown client.
/// </summary>
/// <remarks>
/// These tool <i>names</i> are stable wire surface (the spike + tests assert
/// <c>hub_list_clients</c> / <c>hub_select_client</c>): the <c>hub_</c> prefix keeps
/// them from ever colliding with a client's own tool names (which are proxied
/// verbatim). Everything here is pure: no MCP-session state lives in this type, so the
/// HTTP host and the per-pipe stream sessions share one definition.
/// </remarks>
internal static class HubMetaTools
{
    // ---- canonical names (single source of truth) -------------------------

    public const string ListClients      = "hub_list_clients";
    public const string ListKnownClients = "hub_list_known_clients";
    public const string LaunchClient     = "hub_launch_client";
    public const string RestartClient    = "hub_restart_client";
    public const string SelectClient     = "hub_select_client";
    public const string ClientStatus     = "hub_client_status";

    /// <summary>True if <paramref name="name"/> is one of the hub's own meta-tools.</summary>
    public static bool IsMetaTool(string name) => name switch
    {
        ListClients or ListKnownClients or LaunchClient or RestartClient
            or SelectClient or ClientStatus => true,
        _ => false,
    };

    /// <summary>
    /// The optional <c>client</c> argument every <i>proxied</i> UI tool accepts to
    /// override the active client for a single call. Surfaced in this module so the
    /// proxy and the docs stay in lock-step.
    /// </summary>
    public const string ClientOverrideArg = "client";

    // ---- catalog ----------------------------------------------------------

    /// <summary>Builds the always-present meta-tool entries (order is stable).</summary>
    public static IEnumerable<Tool> BuildCatalog()
    {
        yield return Meta(ListClients,
            "List the AvaloniaMcp clients currently connected to the hub "
            + "(id, app id, display name, pid, read-only, tool count). No arguments.");

        yield return Meta(ListKnownClients,
            "List every client the hub has seen this run, including disconnected ones "
            + "(for launch/restart). No arguments.");

        yield return Meta(LaunchClient,
            "Launch a known client by id (uses its recorded executable path). The app "
            + "connects back on its own. Args: { \"clientId\": string }.",
            ClientIdSchema());

        yield return Meta(RestartClient,
            "Restart a client: terminate the running instance (if any) and launch it "
            + "again. Use this when a client has dropped. Args: { \"clientId\": string }.",
            ClientIdSchema());

        yield return Meta(SelectClient,
            "Make a client active so its tools are advertised (emits "
            + "tools/list_changed). Args: { \"clientId\": string }.",
            ClientIdSchema());

        yield return Meta(ClientStatus,
            "Report the full status of one client (connected, read-only, pid, tools, "
            + "last-seen). Args: { \"clientId\": string }.",
            ClientIdSchema());
    }

    // ---- dispatch ---------------------------------------------------------

    /// <summary>
    /// Handles a meta-tool call synchronously against the broker. Caller has already
    /// confirmed <see cref="IsMetaTool"/>. Returns the MCP result to relay to the AI.
    /// </summary>
    public static async ValueTask<CallToolResult> DispatchAsync(
        IClientBroker broker, string name, JsonElement? args,
        Action onActiveChanged, CancellationToken ct)
    {
        switch (name)
        {
            case ListClients:
                return JsonResult(broker.ListClients().Select(ToView));

            case ListKnownClients:
                return JsonResult(broker.ListKnownClients().Select(ToView));

            case SelectClient:
            {
                if (!TryGetClientId(args, out var id, out var err))
                    return ErrorResult(err);
                if (broker.ClientStatus(id) is null)
                    return DownClientError(id, "is not known to the hub");
                broker.ActiveClientId = id;
                onActiveChanged();
                return JsonResult(new { activeClientId = id });
            }

            case ClientStatus:
            {
                if (!TryGetClientId(args, out var id, out var err))
                    return ErrorResult(err);
                var info = broker.ClientStatus(id);
                return info is null
                    ? DownClientError(id, "is not known to the hub")
                    : JsonResult(ToView(info));
            }

            case LaunchClient:
            {
                if (!TryGetClientId(args, out var id, out var err))
                    return ErrorResult(err);
                try
                {
                    var pid = await broker.LaunchClientAsync(id, ct).ConfigureAwait(false);
                    return JsonResult(new { launched = id, processId = pid });
                }
                catch (Exception ex)
                {
                    return ErrorResult($"Failed to launch '{id}': {ex.Message}");
                }
            }

            case RestartClient:
            {
                if (!TryGetClientId(args, out var id, out var err))
                    return ErrorResult(err);
                try
                {
                    var pid = await broker.RestartClientAsync(id, ct).ConfigureAwait(false);
                    return JsonResult(new { restarted = id, processId = pid });
                }
                catch (Exception ex)
                {
                    return ErrorResult($"Failed to restart '{id}': {ex.Message}");
                }
            }

            default:
                // Unreachable: callers gate on IsMetaTool. Defensive only.
                return ErrorResult($"Unknown meta-tool '{name}'.");
        }
    }

    // ---- structured errors ------------------------------------------------

    /// <summary>
    /// The structured error returned when the AI calls a tool against a client that is
    /// down or unknown. It is a <see cref="CallToolResult"/> with <c>IsError</c> set and
    /// a machine-readable <c>structuredContent</c> object that names the recovery tool
    /// (<see cref="RestartClient"/>) so the model can self-heal.
    /// </summary>
    public static CallToolResult DownClientError(string clientId, string reason)
    {
        var text =
            $"Client '{clientId}' {reason}. It cannot service tool calls right now. "
            + $"Call {RestartClient} with {{ \"clientId\": \"{clientId}\" }} to bring it "
            + $"back, or {SelectClient} a different one ({ListClients} shows live clients).";

        var structured = new JsonObject
        {
            ["error"] = "client_unavailable",
            ["clientId"] = clientId,
            ["reason"] = reason,
            ["recovery"] = new JsonObject
            {
                ["tool"] = RestartClient,
                ["arguments"] = new JsonObject { ["clientId"] = clientId },
            },
        };

        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(structured),
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
        };
    }

    // ---- shared result helpers (reused by the proxy in HubMcpServer) -------

    public static CallToolResult JsonResult(object? value)
    {
        var json = JsonSerializer.Serialize(value, ProtocolJson.Options);
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = json } },
        };
    }

    public static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
    };

    // ---- internals --------------------------------------------------------

    /// <summary>A trimmed, serialization-friendly projection of a client snapshot.</summary>
    private static object ToView(ClientInfo c) => new
    {
        clientId = c.ClientId,
        appId = c.AppId,
        displayName = c.DisplayName,
        processId = c.ProcessId,
        connected = c.IsConnected,
        readOnly = c.ReadOnly,
        toolCount = c.Tools.Count,
        executablePath = c.ExecutablePath,
        lastSeenUtc = c.LastSeenUtc,
    };

    private static bool TryGetClientId(JsonElement? args, out string clientId, out string error)
    {
        clientId = "";
        error = "";
        if (args is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty("clientId", out var v)
            && v.ValueKind == JsonValueKind.String
            && v.GetString() is { Length: > 0 } id)
        {
            clientId = id;
            return true;
        }
        error = "Missing required argument: 'clientId' (a non-empty string).";
        return false;
    }

    private static Tool Meta(string name, string description, JsonElement? schema = null) => new()
    {
        Name = name,
        Description = description,
        InputSchema = schema ?? EmptyObjectSchema(),
        // Meta-tools never mutate app state; mark read-only so clients can hint them.
        Annotations = new ToolAnnotations { ReadOnlyHint = true },
    };

    public static JsonElement EmptyObjectSchema() =>
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    public static JsonElement ClientIdSchema() =>
        JsonDocument.Parse(
            """{"type":"object","properties":{"clientId":{"type":"string","description":"The hub-assigned client id."}},"required":["clientId"]}""")
            .RootElement.Clone();
}
