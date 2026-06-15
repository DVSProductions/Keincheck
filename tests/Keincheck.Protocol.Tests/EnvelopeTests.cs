using System.Text.Json;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// Tests for <see cref="MessageEnvelope"/> wrap/unwrap and the shared
/// <see cref="ProtocolJson"/> policy (string enums, omitted nulls,
/// case-insensitive read) that both ends of the channel rely on.
/// </summary>
public class EnvelopeTests
{
    [Fact]
    public void Wrap_StampsKind_CurrentVersion_AndCorrelationId()
    {
        var heartbeat = new HeartbeatMessage { ClientId = "c", Sequence = 5, TimestampUnixMs = 42 };

        var env = MessageEnvelope.Wrap(MessageKind.Heartbeat, heartbeat, correlationId: "corr-1");

        Assert.Equal(MessageKind.Heartbeat, env.Kind);
        Assert.Equal(ProtocolVersion.Current, env.Version);
        Assert.Equal("corr-1", env.CorrelationId);
        Assert.Equal(JsonValueKind.Object, env.Payload.ValueKind);
    }

    [Fact]
    public void Wrap_NullCorrelationId_IsAllowed()
    {
        var env = MessageEnvelope.Wrap(MessageKind.Heartbeat,
            new HeartbeatMessage { ClientId = "c" });

        Assert.Null(env.CorrelationId);
    }

    [Fact]
    public void Wrap_Then_Unwrap_RoundTrips_Payload()
    {
        var original = new InvokeToolMessage
        {
            ClientId = "client-1",
            ToolName = "screenshot_control",
            Arguments = JsonSerializer.SerializeToElement(new { handle = "ctl-7" }),
        };

        var env = MessageEnvelope.Wrap(MessageKind.InvokeTool, original);
        var back = env.Unwrap<InvokeToolMessage>();

        Assert.NotNull(back);
        Assert.Equal(original.ClientId, back!.ClientId);
        Assert.Equal(original.ToolName, back.ToolName);
        Assert.Equal("ctl-7", back.Arguments!.Value.GetProperty("handle").GetString());
    }

    [Fact]
    public void Envelope_FullySerializes_Through_ProtocolJson_AndBack()
    {
        var result = new ToolResultMessage
        {
            ClientId = "client-2",
            ToolName = "get_property",
            IsError = false,
            Content = JsonSerializer.SerializeToElement(new { value = 123 }),
        };
        var env = MessageEnvelope.Wrap(MessageKind.ToolResult, result, correlationId: "x");

        var json = JsonSerializer.Serialize(env, ProtocolJson.Options);
        var backEnv = JsonSerializer.Deserialize<MessageEnvelope>(json, ProtocolJson.Options);

        Assert.NotNull(backEnv);
        Assert.Equal(MessageKind.ToolResult, backEnv!.Kind);
        Assert.Equal("x", backEnv.CorrelationId);

        var backResult = backEnv.Unwrap<ToolResultMessage>();
        Assert.NotNull(backResult);
        Assert.False(backResult!.IsError);
        Assert.Equal(123, backResult.Content!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public void Envelope_UsesShortPropertyNames_OnTheWire()
    {
        var env = MessageEnvelope.Wrap(MessageKind.Register,
            new RegisterMessage { ClientId = "c" }, correlationId: "id-1");

        var json = JsonSerializer.Serialize(env, ProtocolJson.Options);

        Assert.Contains("\"kind\"", json);
        Assert.Contains("\"v\"", json);
        Assert.Contains("\"id\"", json);
        Assert.Contains("\"payload\"", json);
    }

    [Fact]
    public void Envelope_Kind_IsWrittenAsString_NotNumber()
    {
        var env = MessageEnvelope.Wrap(MessageKind.ClientDown,
            new ClientDownMessage { ClientId = "c", Graceful = true });

        var json = JsonSerializer.Serialize(env, ProtocolJson.Options);

        // JsonStringEnumConverter => the discriminator is the readable name, not "6".
        Assert.Contains("\"ClientDown\"", json);
        Assert.DoesNotContain("\"kind\":6", json);
    }

    [Fact]
    public void Envelope_NullCorrelationId_IsOmitted()
    {
        var env = MessageEnvelope.Wrap(MessageKind.Heartbeat,
            new HeartbeatMessage { ClientId = "c" });

        var json = JsonSerializer.Serialize(env, ProtocolJson.Options);

        Assert.DoesNotContain("\"id\"", json);
    }

    [Fact]
    public void Unwrap_AbsentPayload_ReturnsDefault()
    {
        var env = new MessageEnvelope { Kind = MessageKind.Heartbeat };
        // Payload was never assigned => default JsonElement (ValueKind.Undefined).
        Assert.Equal(JsonValueKind.Undefined, env.Payload.ValueKind);

        var back = env.Unwrap<HeartbeatMessage>();

        Assert.Null(back);
    }

    [Fact]
    public void Unwrap_NullPayload_ReturnsDefault()
    {
        // A JSON null payload must unwrap to default, not throw.
        var json = "{\"kind\":\"Heartbeat\",\"v\":1,\"payload\":null}";
        var env = JsonSerializer.Deserialize<MessageEnvelope>(json, ProtocolJson.Options);

        Assert.NotNull(env);
        Assert.Null(env!.Unwrap<HeartbeatMessage>());
    }

    [Fact]
    public void Wrap_DefaultEnvelope_UsesCurrentVersion_NotZero()
    {
        // The DTO default for Version is ProtocolVersion.Current; Wrap restamps it.
        var env = MessageEnvelope.Wrap(MessageKind.Register, new RegisterMessage { ClientId = "c" });

        Assert.NotEqual(0, env.Version);
        Assert.Equal(ProtocolVersion.Current, env.Version);
    }

    [Fact]
    public void ProtocolJson_DeserializesCaseInsensitively_FromAlienCasing()
    {
        // A lenient peer that wrote PascalCase keys must still read correctly,
        // because the policy sets PropertyNameCaseInsensitive = true.
        var json = "{\"ClientId\":\"X\",\"Sequence\":9,\"TimestampUnixMs\":1}";
        var msg = JsonSerializer.Deserialize<HeartbeatMessage>(json, ProtocolJson.Options);

        Assert.NotNull(msg);
        Assert.Equal("X", msg!.ClientId);
        Assert.Equal(9, msg.Sequence);
        Assert.Equal(1, msg.TimestampUnixMs);
    }

    [Fact]
    public void ProtocolJson_ReadsEnumFromString()
    {
        var json = "{\"kind\":\"InvokeTool\",\"v\":1,\"payload\":{}}";
        var env = JsonSerializer.Deserialize<MessageEnvelope>(json, ProtocolJson.Options);

        Assert.NotNull(env);
        Assert.Equal(MessageKind.InvokeTool, env!.Kind);
    }

    [Fact]
    public void ProtocolJson_OptionsInstance_IsStable()
    {
        // Both ends read the same singleton; assert it is the canonical instance.
        Assert.Same(ProtocolJson.Options, ProtocolJson.Options);
    }
}
