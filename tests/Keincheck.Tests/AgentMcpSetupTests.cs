using System.Text.Json;
using System.Text.Json.Nodes;
using Keincheck.Hub;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Unit tests for <see cref="AgentMcpSetup.AddServer"/> — the pure, safe-merge config transform
/// that registers the <c>keincheck-hub</c> stdio server in an AI client's config without
/// clobbering existing servers or unrelated keys. Both config flavors are covered: Claude's
/// explicit <c>"type": "stdio"</c> and Kimi's, which infers stdio from <c>command</c>.
/// </summary>
public sealed class AgentMcpSetupTests
{
    private const string Name = AgentMcpSetup.ServerName;
    private const string Exe = @"C:\Program Files\Keincheck\keincheck-connect.exe";

    [Fact]
    public void Claude_Desktop_Config_Path_Is_Where_The_App_Actually_Reads_It()
    {
        var path = AgentMcpSetup.TargetPath(AgentTarget.ClaudeDesktop);

        Assert.EndsWith(Path.Combine("Claude", "claude_desktop_config.json"), path);

        // macOS is the case worth pinning: .NET maps SpecialFolder.ApplicationData to
        // ~/.config there (the XDG convention), but a Mac app keeps its config under
        // ~/Library/Application Support — so the generic path writes a file Claude never reads.
        if (OperatingSystem.IsMacOS())
            Assert.Contains(Path.Combine("Library", "Application Support"), path);
    }

    [Fact]
    public void Adds_Server_To_Empty_Config()
    {
        var (json, outcome) = AgentMcpSetup.AddServer(null, Name, Exe);

        Assert.Equal(ConfigOutcome.Added, outcome);
        var entry = ServerEntry(json, Name);
        Assert.Equal("stdio", entry["type"]!.GetValue<string>());
        Assert.Equal(Exe, entry["command"]!.GetValue<string>());
        Assert.IsType<JsonArray>(entry["args"]);
        Assert.Empty((JsonArray)entry["args"]!);
    }

    [Fact]
    public void Preserves_Existing_Servers_And_Top_Level_Keys()
    {
        var existing = """
        {
          "numCompletions": 42,
          "mcpServers": {
            "other-server": { "type": "stdio", "command": "C:\\other.exe", "args": ["--x"] }
          }
        }
        """;

        var (json, outcome) = AgentMcpSetup.AddServer(existing, Name, Exe);

        Assert.Equal(ConfigOutcome.Added, outcome);
        var root = JsonNode.Parse(json) as JsonObject;
        Assert.Equal(42, root!["numCompletions"]!.GetValue<int>());          // unrelated key kept
        var servers = (JsonObject)root["mcpServers"]!;
        Assert.True(servers.ContainsKey("other-server"));                     // sibling server kept
        Assert.Equal("C:\\other.exe", ServerEntry(json, "other-server")["command"]!.GetValue<string>());
        Assert.Equal(Exe, ServerEntry(json, Name)["command"]!.GetValue<string>());
    }

    [Fact]
    public void Re_Adding_Same_Command_Is_Idempotent()
    {
        var (first, _) = AgentMcpSetup.AddServer(null, Name, Exe);
        var (_, outcome) = AgentMcpSetup.AddServer(first, Name, Exe);

        Assert.Equal(ConfigOutcome.AlreadyCurrent, outcome);
    }

    [Fact]
    public void Updates_When_The_Command_Path_Changed()
    {
        var (first, _) = AgentMcpSetup.AddServer(null, Name, @"C:\old\keincheck-connect.exe");
        var (json, outcome) = AgentMcpSetup.AddServer(first, Name, Exe);

        Assert.Equal(ConfigOutcome.Updated, outcome);
        Assert.Equal(Exe, ServerEntry(json, Name)["command"]!.GetValue<string>());
    }

    [Fact]
    public void Preserves_User_Added_Fields_On_The_Entry()
    {
        var existing = $$"""
        {
          "mcpServers": {
            "{{Name}}": { "type": "stdio", "command": "C:\\old.exe", "args": ["--keep"], "env": { "A": "1" } }
          }
        }
        """;

        var (json, outcome) = AgentMcpSetup.AddServer(existing, Name, Exe);

        Assert.Equal(ConfigOutcome.Updated, outcome);
        var entry = ServerEntry(json, Name);
        Assert.Equal(Exe, entry["command"]!.GetValue<string>());              // command refreshed
        Assert.Equal("1", ((JsonObject)entry["env"]!)["A"]!.GetValue<string>()); // env preserved
        Assert.Equal("--keep", ((JsonArray)entry["args"]!)[0]!.GetValue<string>()); // args preserved
    }

    [Fact]
    public void Malformed_Existing_Json_Throws_So_The_File_Is_Left_Untouched()
    {
        Assert.ThrowsAny<JsonException>(() => AgentMcpSetup.AddServer("{ not valid json", Name, Exe));
    }

    // ---------------------------------------------------------------- Kimi flavor

    [Fact]
    public void Kimi_Flavor_Writes_No_Type_Field()
    {
        var (json, outcome) = AgentMcpSetup.AddServer(null, Name, Exe, writeTypeField: false);

        Assert.Equal(ConfigOutcome.Added, outcome);
        var entry = ServerEntry(json, Name);
        Assert.False(entry.ContainsKey("type"));      // Kimi infers stdio from `command`
        Assert.Equal(Exe, entry["command"]!.GetValue<string>());
        Assert.Empty((JsonArray)entry["args"]!);
    }

    [Fact]
    public void Kimi_Flavor_Is_Idempotent()
    {
        var (first, _) = AgentMcpSetup.AddServer(null, Name, Exe, writeTypeField: false);
        var (_, outcome) = AgentMcpSetup.AddServer(first, Name, Exe, writeTypeField: false);

        Assert.Equal(ConfigOutcome.AlreadyCurrent, outcome);
    }

    [Fact]
    public void Kimi_Flavor_Preserves_Extra_Fields_While_Refreshing_The_Command()
    {
        var existing = $$"""
        {
          "mcpServers": {
            "{{Name}}": { "command": "C:\\old.exe", "transport": "sse", "cwd": "C:\\work" }
          }
        }
        """;

        var (json, outcome) = AgentMcpSetup.AddServer(existing, Name, Exe, writeTypeField: false);

        Assert.Equal(ConfigOutcome.Updated, outcome);
        var entry = ServerEntry(json, Name);
        Assert.Equal(Exe, entry["command"]!.GetValue<string>());               // command refreshed
        Assert.Equal("sse", entry["transport"]!.GetValue<string>());           // stray field kept
        Assert.Equal("C:\\work", entry["cwd"]!.GetValue<string>());            // user field kept
        Assert.False(entry.ContainsKey("type"));                               // still no type
    }

    private static JsonObject ServerEntry(string json, string name) =>
        (JsonObject)((JsonObject)((JsonObject)JsonNode.Parse(json)!)["mcpServers"]!)[name]!;
}
