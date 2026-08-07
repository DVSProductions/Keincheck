namespace Keincheck.E2E;

/// <summary>
/// The hub's own meta-tools, mirroring <c>HubMetaTools.BuildCatalog</c>.
/// </summary>
/// <remarks>
/// Duplicated deliberately rather than referenced. This suite does not link
/// <c>Keincheck.Hub</c>, so the list is written out here as an independent statement of
/// the advertised contract — a tool silently renamed or dropped breaks the test instead of
/// quietly redefining what "correct" means, which is exactly what sharing the constant
/// would do.
/// </remarks>
public static class MetaTools
{
    public static readonly string[] Names =
    [
        "hub_guide",
        "hub_list_clients",
        "hub_list_known_clients",
        "hub_launch_client",
        "hub_restart_client",
        "hub_select_client",
        "hub_client_status",
        "hub_wait_for_client",
        "hub_status",
        "hub_set_readonly",
        "hub_record_start",
        "hub_record_stop",
        "hub_record_status",
        "hub_replay",
        "hub_export_test",
        "hub_remote_status",
        "hub_remote_enable",
        "hub_remote_issue",
        "hub_remote_disable",
        "hub_remote_revoke",
        // Static-tooling companions: the discovery + generic-proxy pair an agent that cannot
        // handle a changing tool list relies on entirely.
        "hub_list_client_tools",
        "hub_call_tool",
        // Write-claims: one driver per app instance when several agents share the hub.
        "hub_claim_client",
        "hub_release_client",
    ];

    public static IEnumerable<string> Order() => Names.Order();

    public static int Count => Names.Length;
}

/// <summary>
/// The tools a <c>Keincheck.Core</c>-backed client advertises. The MCP SDK snake-cases the
/// method names, so <c>ClickAt</c> ships as <c>click_at</c>.
/// </summary>
public static class CoreTools
{
    public static readonly string[] Names =
    [
        // InspectionTools
        "list_windows", "get_logical_tree", "get_visual_tree", "query_controls",
        "get_properties", "get_property", "get_data_context", "get_text",
        "get_binding_errors", "hit_test", "get_focused_element",
        // ActionTools
        "set_property", "automation_action", "set_focus", "wait_for",
        // InputTools — the synthetic-input fallback for peer-less controls
        "pointer", "click_at", "scroll_at", "type_text", "send_keys",
        // ScreenshotTools
        "screenshot_window", "screenshot_control",
        // SemanticTools
        "get_semantic_tree", "screenshot_marked", "describe_screen", "wait_for_idle",
        // GuideTools
        "keincheck_guide",
    ];

    public static int Count => Names.Length;

    /// <summary>Tools the read-only gate must refuse.</summary>
    public static readonly string[] Mutating =
    [
        "set_property", "automation_action", "type_text", "click_at", "pointer", "send_keys",
    ];

    /// <summary>Tools the read-only gate must keep allowing.</summary>
    public static readonly string[] ReadOnly =
    [
        "query_controls", "get_text", "get_property", "screenshot_window", "list_windows",
    ];
}
