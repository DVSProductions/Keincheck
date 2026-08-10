using Keincheck.Hub;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// <c>Keincheck.Hub --issue-websocket-token</c> — the build-time command that binds a browser app
/// to one hub, mirroring <c>--issue-credential</c>.
///
/// The behaviour worth pinning is what it refuses. A build step runs this on every compile, so
/// it must not enable a network-reachable endpoint on the operator's behalf, and it must not
/// mint a fresh token each time and bury the hub's token list.
/// </summary>
public sealed class WebSocketTokenCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "keincheck-wscli-" + Guid.NewGuid().ToString("N"));

    private string PolicyPath => Path.Combine(_dir, "websocket-access.json");
    private string OutPath => Path.Combine(_dir, "out.credential");

    private const string Origin = "http://localhost:5000";

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private HubWebSocketAccess Enabled()
    {
        var access = HubWebSocketAccess.Open(PolicyPath);
        access.SetEnabled(true);
        return access;
    }

    private static int Run(HubWebSocketAccess access, params string[] args) =>
        WebSocketTokenCli.Run(args, hubOptions: null, access: access);

    [Fact]
    public void Without_The_Declaration_It_Will_Not_Enable_The_Endpoint()
    {
        // Exit 4 mirrors --issue-credential's "remote access is not enabled".
        var access = HubWebSocketAccess.Open(PolicyPath);

        var exit = Run(access, "--issue-websocket-token", "--origin", Origin, "--out", OutPath);

        Assert.Equal(4, exit);
        Assert.False(access.Enabled);
        Assert.False(File.Exists(OutPath));
    }

    [Fact]
    public void A_Web_App_Build_Enables_The_Endpoint_For_A_Loopback_Origin()
    {
        // The endpoint binds 127.0.0.1, and enabling it grants nothing on its own -- with no
        // origin allowlisted and no token issued every request is still refused. This same
        // command is about to do both of the things that DO grant access, so withholding the
        // flag would have been ceremony rather than a control.
        var access = HubWebSocketAccess.Open(PolicyPath);

        var exit = Run(access, "--issue-websocket-token", "--origin", Origin,
            "--out", OutPath, "--enable-if-needed");

        Assert.Equal(0, exit);
        Assert.True(access.Enabled);
        var credential = WebSocketCredential.LoadFile(OutPath);
        Assert.Equal(WebSocketGateResult.Allowed, access.Check(Origin, credential.Token));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    [InlineData("https://localhost:7001")]
    public void Loopback_Is_Recognized_However_It_Is_Spelled(string origin)
    {
        var access = HubWebSocketAccess.Open(PolicyPath);

        Assert.Equal(0, Run(access, "--issue-websocket-token", "--origin", origin,
            "--out", OutPath, "--enable-if-needed"));
        Assert.True(access.Enabled);
    }

    [Theory]
    [InlineData("https://myapp.example")]
    [InlineData("http://192.168.1.50:5000")]
    public void A_Published_Origin_Still_Has_To_Be_Enabled_By_Hand(string origin)
    {
        // This is the case where the embedded token stops being a secret: a browser ships its
        // assemblies to every visitor. Opting into that should be a deliberate act.
        var access = HubWebSocketAccess.Open(PolicyPath);

        var exit = Run(access, "--issue-websocket-token", "--origin", origin,
            "--out", OutPath, "--enable-if-needed");

        Assert.Equal(4, exit);
        Assert.False(access.Enabled);
        Assert.False(File.Exists(OutPath));
    }

    [Fact]
    public void Enabling_From_A_Build_Records_That_It_Was_Not_A_Human()
    {
        // The hub surfaces what it did rather than doing it quietly, so an operator who finds
        // the endpoint on can find out what turned it on.
        var access = HubWebSocketAccess.Open(PolicyPath);

        Run(access, "--issue-websocket-token", "--origin", Origin,
            "--out", OutPath, "--label", "mywebapp", "--enable-if-needed");

        Assert.NotNull(access.EnabledBy);
        Assert.Contains("mywebapp", access.EnabledBy!);
        Assert.Contains(Origin, access.EnabledBy!);
        Assert.NotNull(access.EnabledAt);
    }

    [Fact]
    public void An_Already_Enabled_Endpoint_Is_Not_Re_Attributed_To_The_Build()
    {
        // The operator turned it on; a later build must not overwrite that record.
        var access = Enabled();

        Run(access, "--issue-websocket-token", "--origin", Origin,
            "--out", OutPath, "--enable-if-needed");

        Assert.Null(access.EnabledBy);
    }

    [Fact]
    public void Issues_A_Credential_The_Gate_Then_Accepts()
    {
        var access = Enabled();

        var exit = Run(access, "--issue-websocket-token", "--origin", Origin, "--out", OutPath);

        Assert.Equal(0, exit);
        var credential = WebSocketCredential.LoadFile(OutPath);
        Assert.Equal(WebSocketGateResult.Allowed, access.Check(Origin, credential.Token));
    }

    [Fact]
    public void Allowlists_The_Origin_It_Issued_For()
    {
        var access = Enabled();

        Run(access, "--issue-websocket-token", "--origin", Origin, "--out", OutPath);

        Assert.Contains(Origin, access.AllowedOrigins);
    }

    [Fact]
    public void The_Issued_Credential_Carries_A_Usable_Endpoint()
    {
        // The app should not have to hardcode the hub's port beside its token; a changed
        // HubOptions.HttpPort would silently break it.
        var access = Enabled();

        Run(access, "--issue-websocket-token", "--origin", Origin, "--out", OutPath);

        var credential = WebSocketCredential.LoadFile(OutPath);
        Assert.True(Uri.TryCreate(credential.Endpoint, UriKind.Absolute, out var uri));
        Assert.Equal("ws", uri!.Scheme);
        Assert.True(uri.IsLoopback);
    }

    [Fact]
    public void If_Missing_Reuses_The_Existing_Token_Instead_Of_Minting_Per_Build()
    {
        // Without this, every compile adds a token, and the hub's list stops being something an
        // operator can scan for anything unusual.
        var access = Enabled();
        Run(access, "--issue-websocket-token", "--origin", Origin, "--out", OutPath, "--if-missing");
        var first = WebSocketCredential.LoadFile(OutPath).Token;

        Run(access, "--issue-websocket-token", "--origin", Origin, "--out", OutPath, "--if-missing");
        var second = WebSocketCredential.LoadFile(OutPath).Token;

        Assert.Equal(first, second);
        Assert.Single(access.TokenLabels);
    }

    [Fact]
    public void If_Missing_Replaces_A_Token_That_Is_No_Longer_Valid()
    {
        var access = Enabled();
        Run(access, "--issue-websocket-token", "--origin", Origin, "--out", OutPath, "--if-missing");
        var first = WebSocketCredential.LoadFile(OutPath).Token;

        // The operator revoked it; the next build must notice rather than embed a dead token.
        foreach (var label in access.TokenLabels.ToArray())
            access.RevokeToken(label);

        Run(access, "--issue-websocket-token", "--origin", Origin, "--out", OutPath, "--if-missing");
        var second = WebSocketCredential.LoadFile(OutPath).Token;

        Assert.NotEqual(first, second);
        Assert.Equal(WebSocketGateResult.Allowed, access.Check(Origin, second));
    }

    [Fact]
    public void Without_An_Origin_It_Is_A_Usage_Error()
    {
        var access = Enabled();

        Assert.Equal(2, Run(access, "--issue-websocket-token", "--out", OutPath));
    }

    [Fact]
    public void A_Malformed_Origin_Is_A_Usage_Error()
    {
        var access = Enabled();

        Assert.Equal(2, Run(access, "--issue-websocket-token", "--origin", "not a url", "--out", OutPath));
        Assert.False(File.Exists(OutPath));
    }

    [Fact]
    public void The_Label_Defaults_To_The_Origins_Authority()
    {
        // So the hub's token list reads as a list of apps rather than a column of "build".
        var access = Enabled();

        Run(access, "--issue-websocket-token", "--origin", Origin, "--out", OutPath);

        Assert.Contains("localhost:5000", access.TokenLabels);
    }

    [Fact]
    public void A_Running_Hub_Picks_Up_A_Token_Issued_By_A_Separate_Process()
    {
        // The build runs as its own process and writes the policy file directly. A hub already
        // holding the policy in memory has to notice, or a freshly enrolled app cannot connect
        // until the hub is restarted -- which would make the whole build-time flow useless.
        var hubSideView = Enabled();
        var buildSideProcess = HubWebSocketAccess.Open(PolicyPath);

        Run(buildSideProcess, "--issue-websocket-token", "--origin", Origin, "--out", OutPath);
        var credential = WebSocketCredential.LoadFile(OutPath);

        Assert.Equal(WebSocketGateResult.Allowed, hubSideView.Check(Origin, credential.Token));
    }
}
