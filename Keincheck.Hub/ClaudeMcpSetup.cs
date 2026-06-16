using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Keincheck.Hub;

/// <summary>Which Claude client's MCP configuration to write.</summary>
public enum ClaudeTarget
{
    /// <summary>Claude Code's user-scoped config (<c>~/.claude.json</c>), available in every project.</summary>
    Code,

    /// <summary>Claude Desktop's config (<c>%APPDATA%\Claude\claude_desktop_config.json</c>).</summary>
    Desktop,
}

/// <summary>What writing the config did.</summary>
public enum ConfigOutcome
{
    /// <summary>The <c>keincheck-hub</c> server was newly added.</summary>
    Added,

    /// <summary>An existing <c>keincheck-hub</c> entry was updated (e.g. the command path changed).</summary>
    Updated,

    /// <summary>The config already pointed at the current shim; nothing was written.</summary>
    AlreadyCurrent,

    /// <summary>The config could not be written (e.g. the existing file is not valid JSON).</summary>
    Failed,
}

/// <summary>The result of configuring one Claude target.</summary>
public readonly record struct SetupResult(ClaudeTarget Target, ConfigOutcome Outcome, string Path, string Message);

/// <summary>
/// Registers the hub's stdio bridge (<c>keincheck-connect.exe</c>) as a <c>keincheck-hub</c>
/// MCP server in Claude's configuration, so a user gets the AI↔hub link without hand-editing
/// JSON. Writes the config file directly (rather than shelling out to <c>claude mcp add</c>):
/// it is idempotent, needs no <c>claude</c> CLI on PATH, and is the same shape for both targets.
/// </summary>
/// <remarks>
/// Every edit is a <b>safe merge</b> — existing servers and unrelated keys are preserved, and a
/// config that is not valid JSON is left untouched (reported as <see cref="ConfigOutcome.Failed"/>)
/// rather than clobbered. Claude reads the file on its next start, so a restart is needed.
/// </remarks>
public static class ClaudeMcpSetup
{
    /// <summary>The MCP server name written into Claude's config.</summary>
    public const string ServerName = "keincheck-hub";

    // ---------------------------------------------------------------- core merge

    /// <summary>
    /// Pure config transform: merges a <c>stdio</c> server named <paramref name="serverName"/>
    /// pointing at <paramref name="command"/> into <paramref name="existingJson"/>, preserving
    /// every other server and top-level key. Returns the new JSON text and what changed.
    /// </summary>
    /// <exception cref="JsonException">
    /// <paramref name="existingJson"/> is non-empty but not a valid JSON object — the caller MUST
    /// NOT overwrite the file in that case.
    /// </exception>
    public static (string Json, ConfigOutcome Outcome) AddServer(string? existingJson, string serverName, string command)
    {
        var root = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonObject()
            : JsonNode.Parse(existingJson) as JsonObject
              ?? throw new JsonException("The Claude config root is not a JSON object.");

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        var existed = servers[serverName] is JsonObject;
        var entry = servers[serverName] as JsonObject ?? new JsonObject();

        // Refresh only the essentials; preserve any fields the user added (env, extra args, …).
        var changed = !existed;
        changed |= SetString(entry, "type", "stdio");
        changed |= SetString(entry, "command", command);
        if (entry["args"] is null)
        {
            entry["args"] = new JsonArray();
            changed = true;
        }

        servers[serverName] = entry;

        var outcome = !existed ? ConfigOutcome.Added
            : changed ? ConfigOutcome.Updated
            : ConfigOutcome.AlreadyCurrent;

        return (root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), outcome);
    }

    private static bool SetString(JsonObject obj, string key, string value)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var existing) && existing == value)
            return false;
        obj[key] = value;
        return true;
    }

    // ------------------------------------------------------------- file targets

    /// <summary>The config file path for <paramref name="target"/> (the file may not exist yet).</summary>
    public static string TargetPath(ClaudeTarget target) => target switch
    {
        ClaudeTarget.Desktop => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"),
        ClaudeTarget.Code => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>
    /// Writes (or refreshes) the <c>keincheck-hub</c> entry in <paramref name="target"/>'s config to
    /// point at <paramref name="connectExe"/>. Safe: a malformed existing file is left untouched.
    /// </summary>
    public static SetupResult Configure(ClaudeTarget target, string connectExe)
    {
        var path = TargetPath(target);
        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            var (json, outcome) = AddServer(existing, ServerName, connectExe);

            if (outcome != ConfigOutcome.AlreadyCurrent)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            return new SetupResult(target, outcome, path, DescribeOutcome(target, outcome));
        }
        catch (Exception ex)
        {
            return new SetupResult(target, ConfigOutcome.Failed, path,
                $"Could not update {Label(target)} config: {ex.Message}");
        }
    }

    /// <summary>Whether <paramref name="target"/>'s config already points <c>keincheck-hub</c> at <paramref name="connectExe"/>.</summary>
    public static bool IsConfigured(ClaudeTarget target, string connectExe)
    {
        try
        {
            var path = TargetPath(target);
            if (!File.Exists(path))
                return false;

            var server = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["mcpServers"] as JsonObject;
            var command = (server?[ServerName] as JsonObject)?["command"]?.GetValue<string>();
            return command is not null && string.Equals(command, connectExe, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------- shim discovery

    /// <summary>
    /// Locates <c>keincheck-connect.exe</c>: the <c>KEINCHECK_CONNECT_EXE</c> override, then a copy
    /// co-located with the hub (the Velopack install layout), then the sibling
    /// <c>Keincheck.Connect</c> build output (dev). Returns <c>null</c> when none is found.
    /// </summary>
    public static string? ResolveConnectExe()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KEINCHECK_CONNECT_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        var baseDir = AppContext.BaseDirectory;
        var coLocated = ProbeConnect(baseDir);
        if (coLocated is not null)
            return coLocated;

        // Dev fallback: climb to the solution dir and probe Keincheck.Connect/bin/{Config}/{tfm}/.
        var dir = new DirectoryInfo(baseDir);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            var project = Path.Combine(dir.FullName, "Keincheck.Connect");
            if (!Directory.Exists(project))
                continue;

            var bin = Path.Combine(project, "bin");
            if (!Directory.Exists(bin))
                return null;

            foreach (var config in Directory.EnumerateDirectories(bin))
                foreach (var tfm in Directory.EnumerateDirectories(config))
                {
                    var hit = ProbeConnect(tfm);
                    if (hit is not null)
                        return hit;
                }
            return null;
        }
        return null;
    }

    private static string? ProbeConnect(string dir)
    {
        foreach (var name in new[] { "keincheck-connect.exe", "keincheck-connect" })
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // ------------------------------------------------------------- labels

    /// <summary>A human-readable label for a target (used in dialogs / logs).</summary>
    public static string Label(ClaudeTarget target) => target switch
    {
        ClaudeTarget.Code => "Claude Code",
        ClaudeTarget.Desktop => "Claude Desktop",
        _ => target.ToString(),
    };

    private static string DescribeOutcome(ClaudeTarget target, ConfigOutcome outcome) => outcome switch
    {
        ConfigOutcome.Added => $"Added keincheck-hub to {Label(target)}.",
        ConfigOutcome.Updated => $"Updated keincheck-hub in {Label(target)}.",
        ConfigOutcome.AlreadyCurrent => $"{Label(target)} is already set up.",
        _ => $"Could not configure {Label(target)}.",
    };
}
