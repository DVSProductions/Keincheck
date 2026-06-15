using System.Text.Json;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// Round-trips every IPC DTO and the <see cref="MessageEnvelope"/> through
/// <see cref="ProtocolJson.Options"/> — the single serializer both ends of the
/// broker channel agree on. Every field, every optional, and the JSON shape
/// (string enums, omitted nulls, deterministic property names) is asserted so a
/// future broker can trust the contract byte-for-byte.
/// </summary>
public class MessageRoundTripTests
{
    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, ProtocolJson.Options);
        var back = JsonSerializer.Deserialize<T>(json, ProtocolJson.Options);
        Assert.NotNull(back);
        return back!;
    }

    [Fact]
    public void RegisterMessage_RoundTrips_AllFields()
    {
        var msg = new RegisterMessage
        {
            ClientId = "client-abc",
            DisplayName = "ProtoFace (pid 1234)",
            ProcessId = 1234,
            ProtocolVersion = ProtocolVersion.Current,
        };

        var back = RoundTrip(msg);

        Assert.Equal(msg.ClientId, back.ClientId);
        Assert.Equal(msg.DisplayName, back.DisplayName);
        Assert.Equal(msg.ProcessId, back.ProcessId);
        Assert.Equal(msg.ProtocolVersion, back.ProtocolVersion);
    }

    [Fact]
    public void RegisterMessage_OwnsWindows_RoundTrips_And_Uses_Camel_Property()
    {
        // finding-2 Fix B: the client reports window-ownership on Register so the hub can
        // surface ownsWindows without the AI probing each client with list_windows.
        var on = new RegisterMessage
        {
            ClientId = "ui", ProcessId = 1, ProtocolVersion = ProtocolVersion.Current,
            OwnsWindows = true,
        };

        var json = JsonSerializer.Serialize(on, ProtocolJson.Options);
        Assert.Contains("\"ownsWindows\":true", json);
        Assert.True(RoundTrip(on).OwnsWindows);

        // Default is false and a defaulted register still round-trips to false.
        var off = new RegisterMessage
        {
            ClientId = "worker", ProcessId = 2, ProtocolVersion = ProtocolVersion.Current,
        };
        Assert.False(off.OwnsWindows);
        Assert.False(RoundTrip(off).OwnsWindows);
    }

    [Fact]
    public void ToolListMessage_OwnsWindows_RoundTrips()
    {
        // ownsWindows is recomputed and resent on every ToolList (windows open after startup).
        var msg = new ToolListMessage
        {
            ClientId = "ui",
            OwnsWindows = true,
            Tools = new[] { new ToolDescriptor { Name = "list_windows" } },
        };

        var json = JsonSerializer.Serialize(msg, ProtocolJson.Options);
        Assert.Contains("\"ownsWindows\":true", json);

        var back = RoundTrip(msg);
        Assert.True(back.OwnsWindows);
        Assert.Equal("list_windows", Assert.Single(back.Tools).Name);
    }

    [Fact]
    public void RegisterMessage_NullDisplayName_IsOmittedFromJson_And_RoundTrips()
    {
        var msg = new RegisterMessage
        {
            ClientId = "c1",
            DisplayName = null,
            ProcessId = 7,
            ProtocolVersion = 1,
        };

        var json = JsonSerializer.Serialize(msg, ProtocolJson.Options);
        // ignore-null policy: the optional display name must not appear on the wire.
        Assert.DoesNotContain("displayName", json);
        // explicit [JsonPropertyName] casing must be honored.
        Assert.Contains("\"clientId\"", json);
        Assert.Contains("\"processId\"", json);
        Assert.Contains("\"protocolVersion\"", json);

        var back = RoundTrip(msg);
        Assert.Null(back.DisplayName);
        Assert.Equal("c1", back.ClientId);
    }

    [Fact]
    public void HeartbeatMessage_RoundTrips_AllFields()
    {
        var msg = new HeartbeatMessage
        {
            ClientId = "c2",
            Sequence = long.MaxValue,
            TimestampUnixMs = 1_700_000_000_123L,
        };

        var back = RoundTrip(msg);

        Assert.Equal(msg.ClientId, back.ClientId);
        Assert.Equal(msg.Sequence, back.Sequence);
        Assert.Equal(msg.TimestampUnixMs, back.TimestampUnixMs);
    }

    [Fact]
    public void ToolDescriptor_RoundTrips_With_InputSchema()
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { target = new { type = "string" } },
            required = new[] { "target" },
        });

        var msg = new ToolDescriptor
        {
            Name = "screenshot_window",
            Description = "Renders a window to PNG.",
            InputSchema = schema,
        };

        var back = RoundTrip(msg);

        Assert.Equal(msg.Name, back.Name);
        Assert.Equal(msg.Description, back.Description);
        Assert.NotNull(back.InputSchema);
        Assert.Equal(JsonValueKind.Object, back.InputSchema!.Value.ValueKind);
        Assert.Equal("object", back.InputSchema.Value.GetProperty("type").GetString());
        Assert.Equal(
            "string",
            back.InputSchema.Value.GetProperty("properties").GetProperty("target").GetProperty("type").GetString());
    }

    [Fact]
    public void ToolDescriptor_NullSchema_IsOmitted_And_RoundTrips()
    {
        var msg = new ToolDescriptor { Name = "list_windows", Description = null, InputSchema = null };

        var json = JsonSerializer.Serialize(msg, ProtocolJson.Options);
        Assert.DoesNotContain("inputSchema", json);
        Assert.DoesNotContain("description", json);

        var back = RoundTrip(msg);
        Assert.Equal("list_windows", back.Name);
        Assert.Null(back.Description);
        Assert.Null(back.InputSchema);
    }

    [Fact]
    public void ToolListMessage_RoundTrips_PreservingOrderAndCount()
    {
        var msg = new ToolListMessage
        {
            ClientId = "c3",
            Tools = new[]
            {
                new ToolDescriptor { Name = "a", Description = "first" },
                new ToolDescriptor { Name = "b", Description = "second" },
                new ToolDescriptor { Name = "c", Description = null },
            },
        };

        var back = RoundTrip(msg);

        Assert.Equal("c3", back.ClientId);
        Assert.Equal(3, back.Tools.Count);
        Assert.Equal("a", back.Tools[0].Name);
        Assert.Equal("b", back.Tools[1].Name);
        Assert.Equal("c", back.Tools[2].Name);
        Assert.Equal("first", back.Tools[0].Description);
        Assert.Null(back.Tools[2].Description);
    }

    [Fact]
    public void ToolListMessage_EmptyToolList_RoundTrips_ToEmpty()
    {
        var msg = new ToolListMessage { ClientId = "c4", Tools = Array.Empty<ToolDescriptor>() };

        var back = RoundTrip(msg);

        Assert.Equal("c4", back.ClientId);
        Assert.NotNull(back.Tools);
        Assert.Empty(back.Tools);
    }

    [Fact]
    public void InvokeToolMessage_RoundTrips_With_Arguments()
    {
        var args = JsonSerializer.SerializeToElement(new { selector = "Button[Name=ok]", count = 2 });
        var msg = new InvokeToolMessage { ClientId = "c5", ToolName = "click_at", Arguments = args };

        var back = RoundTrip(msg);

        Assert.Equal("c5", back.ClientId);
        Assert.Equal("click_at", back.ToolName);
        Assert.NotNull(back.Arguments);
        Assert.Equal("Button[Name=ok]", back.Arguments!.Value.GetProperty("selector").GetString());
        Assert.Equal(2, back.Arguments.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public void InvokeToolMessage_NullArguments_IsOmitted_And_RoundTrips()
    {
        var msg = new InvokeToolMessage { ClientId = "c6", ToolName = "list_windows", Arguments = null };

        var json = JsonSerializer.Serialize(msg, ProtocolJson.Options);
        Assert.DoesNotContain("arguments", json);

        var back = RoundTrip(msg);
        Assert.Equal("list_windows", back.ToolName);
        Assert.Null(back.Arguments);
    }

    [Fact]
    public void ToolResultMessage_Success_RoundTrips_With_Content()
    {
        var content = JsonSerializer.SerializeToElement(new[]
        {
            new { type = "text", text = "ok" },
        });
        var msg = new ToolResultMessage
        {
            ClientId = "c7",
            ToolName = "get_text",
            IsError = false,
            Content = content,
            Error = null,
        };

        var json = JsonSerializer.Serialize(msg, ProtocolJson.Options);
        Assert.DoesNotContain("error", json); // null error omitted on a success result

        var back = RoundTrip(msg);
        Assert.False(back.IsError);
        Assert.Null(back.Error);
        Assert.NotNull(back.Content);
        Assert.Equal(JsonValueKind.Array, back.Content!.Value.ValueKind);
        Assert.Equal("ok", back.Content.Value[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ToolResultMessage_Error_RoundTrips_With_Error()
    {
        var msg = new ToolResultMessage
        {
            ClientId = "c8",
            ToolName = "set_property",
            IsError = true,
            Content = null,
            Error = "no such control: ctl-99",
        };

        var json = JsonSerializer.Serialize(msg, ProtocolJson.Options);
        Assert.DoesNotContain("content", json); // null content omitted on an error result
        Assert.Contains("\"isError\":true", json);

        var back = RoundTrip(msg);
        Assert.True(back.IsError);
        Assert.Null(back.Content);
        Assert.Equal("no such control: ctl-99", back.Error);
    }

    [Fact]
    public void ClientDownMessage_RoundTrips_Graceful()
    {
        var msg = new ClientDownMessage { ClientId = "c9", Reason = "app exited", Graceful = true };

        var back = RoundTrip(msg);

        Assert.Equal("c9", back.ClientId);
        Assert.Equal("app exited", back.Reason);
        Assert.True(back.Graceful);
    }

    [Fact]
    public void ClientDownMessage_RoundTrips_Ungraceful_NullReason_Omitted()
    {
        var msg = new ClientDownMessage { ClientId = "c10", Reason = null, Graceful = false };

        var json = JsonSerializer.Serialize(msg, ProtocolJson.Options);
        Assert.DoesNotContain("reason", json);

        var back = RoundTrip(msg);
        Assert.Equal("c10", back.ClientId);
        Assert.Null(back.Reason);
        Assert.False(back.Graceful);
    }
}
