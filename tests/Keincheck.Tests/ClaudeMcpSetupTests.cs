using System.Text.Json;
using System.Text.Json.Nodes;
using Keincheck.Hub;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Unit tests for <see cref="ClaudeMcpSetup.AddServer"/> — the pure, safe-merge config transform
/// that registers the <c>keincheck-hub</c> stdio server in a Claude config without clobbering
/// existing servers or unrelated keys.
/// </summary>
public sealed class ClaudeMcpSetupTests
{
    private const string Name = ClaudeMcpSetup.ServerName;
    private const string Exe = @"C:\Program Files\Keincheck\keincheck-connect.exe";

    [Fact]
    public void Adds_Server_To_Empty_Config()
    {
        var (json, outcome) = ClaudeMcpSetup.AddServer(null, Name, Exe);

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

        var (json, outcome) = ClaudeMcpSetup.AddServer(existing, Name, Exe);

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
        var (first, _) = ClaudeMcpSetup.AddServer(null, Name, Exe);
        var (_, outcome) = ClaudeMcpSetup.AddServer(first, Name, Exe);

        Assert.Equal(ConfigOutcome.AlreadyCurrent, outcome);
    }

    [Fact]
    public void Updates_When_The_Command_Path_Changed()
    {
        var (first, _) = ClaudeMcpSetup.AddServer(null, Name, @"C:\old\keincheck-connect.exe");
        var (json, outcome) = ClaudeMcpSetup.AddServer(first, Name, Exe);

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

        var (json, outcome) = ClaudeMcpSetup.AddServer(existing, Name, Exe);

        Assert.Equal(ConfigOutcome.Updated, outcome);
        var entry = ServerEntry(json, Name);
        Assert.Equal(Exe, entry["command"]!.GetValue<string>());              // command refreshed
        Assert.Equal("1", ((JsonObject)entry["env"]!)["A"]!.GetValue<string>()); // env preserved
        Assert.Equal("--keep", ((JsonArray)entry["args"]!)[0]!.GetValue<string>()); // args preserved
    }

    [Fact]
    public void Malformed_Existing_Json_Throws_So_The_File_Is_Left_Untouched()
    {
        Assert.ThrowsAny<JsonException>(() => ClaudeMcpSetup.AddServer("{ not valid json", Name, Exe));
    }

    private static JsonObject ServerEntry(string json, string name) =>
        (JsonObject)((JsonObject)((JsonObject)JsonNode.Parse(json)!)["mcpServers"]!)[name]!;
}
