using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// The credential a build embeds to bind an app to one hub.
///
/// Parsing is strict on purpose: a half-populated credential would otherwise surface much later
/// as an obscure connect failure, at which point "which hub was this even built against?" is a
/// much harder question than it needs to be.
/// </summary>
public sealed class WebSocketCredentialTests
{
    private static WebSocketCredential Sample() => new()
    {
        Endpoint = "ws://127.0.0.1:3100/ws",
        Token = "a-token",
        Label = "demo",
    };

    [Fact]
    public void Round_Trips_Through_Its_Serialized_Form()
    {
        var parsed = WebSocketCredential.Parse(Sample().Serialize());

        Assert.Equal("ws://127.0.0.1:3100/ws", parsed.Endpoint);
        Assert.Equal("a-token", parsed.Token);
        Assert.Equal("demo", parsed.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{ "token": "t" }""")]                       // no endpoint
    [InlineData("""{ "endpoint": "ws://x/ws" }""")]            // no token
    [InlineData("""{ "endpoint": "", "token": "t" }""")]       // blank endpoint
    [InlineData("""{ "endpoint": "ws://x/ws", "token": "" }""")] // blank token
    // "/ws" is the interesting one: on Unix it parses as an absolute file: URI, so an
    // absolute-only check accepted it on Linux and rejected it on Windows. The rule is the
    // scheme, which is portable and is what the connector can actually dial.
    [InlineData("""{ "endpoint": "/ws", "token": "t" }""")]
    [InlineData("""{ "endpoint": "file:///ws", "token": "t" }""")]
    [InlineData("""{ "endpoint": "http://localhost:3100/ws", "token": "t" }""")]
    public void Refuses_Anything_That_Is_Not_A_Usable_Credential(string text)
    {
        Assert.Throws<FormatException>(() => WebSocketCredential.Parse(text));
    }

    [Fact]
    public void A_Label_Is_Optional()
    {
        var parsed = WebSocketCredential.Parse("""{ "endpoint": "ws://127.0.0.1:3100/ws", "token": "t" }""");

        Assert.Null(parsed.Label);
        Assert.Equal("t", parsed.Token);
    }

    [Fact]
    public void An_Assembly_Without_An_Embedded_Credential_Yields_Null()
    {
        // The ordinary case for an app not built with enrollment on -- not an error.
        Assert.Null(WebSocketCredential.FromAssembly(typeof(WebSocketCredentialTests).Assembly));
    }

    [Fact]
    public void The_Environment_Supplies_A_Credential_Inline()
    {
        var previous = Environment.GetEnvironmentVariable(WebSocketCredential.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WebSocketCredential.EnvironmentVariable, Sample().Serialize());

            var parsed = WebSocketCredential.FromEnvironment();

            Assert.NotNull(parsed);
            Assert.Equal("a-token", parsed!.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WebSocketCredential.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void No_Environment_Variable_Yields_Null()
    {
        var previous = Environment.GetEnvironmentVariable(WebSocketCredential.EnvironmentVariable);
        var previousFile = Environment.GetEnvironmentVariable(
            WebSocketCredential.EnvironmentVariable + "_FILE");
        try
        {
            Environment.SetEnvironmentVariable(WebSocketCredential.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable(WebSocketCredential.EnvironmentVariable + "_FILE", null);

            Assert.Null(WebSocketCredential.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(WebSocketCredential.EnvironmentVariable, previous);
            Environment.SetEnvironmentVariable(
                WebSocketCredential.EnvironmentVariable + "_FILE", previousFile);
        }
    }
}
