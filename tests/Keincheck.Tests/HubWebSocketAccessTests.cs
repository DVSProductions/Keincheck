using Keincheck.Hub;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// The gate in front of the hub's client-attach WebSocket endpoint.
///
/// This is the one place in the hub where a loopback TCP port stands in for a named pipe, and
/// the pipe's OS-level ACL is not there to help. Two callers can reach it that the pipe would
/// have excluded — any local process, and any web page the user happens to visit, because the
/// same-origin policy does not apply to WebSockets. So the interesting assertions here are all
/// about what is *refused*.
/// </summary>
public sealed class HubWebSocketAccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "keincheck-ws-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "websocket-access.json");

    private const string GoodOrigin = "http://localhost:5000";
    private const string EvilOrigin = "https://evil.example";

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>An access policy that is enabled, allows GoodOrigin, and has one token.</summary>
    private (HubWebSocketAccess Access, string Token) Ready()
    {
        var access = HubWebSocketAccess.Open(Path_);
        access.SetEnabled(true);
        Assert.True(access.AllowOrigin(GoodOrigin));
        return (access, access.IssueToken("demo"));
    }

    // ------------------------------------------------------------------ closed by default

    [Fact]
    public void Is_Closed_Until_Explicitly_Opened()
    {
        var access = HubWebSocketAccess.Open(Path_);

        Assert.False(access.Enabled);
        Assert.Empty(access.AllowedOrigins);
        Assert.Empty(access.TokenLabels);
        Assert.False(File.Exists(Path_)); // reading does not create it
    }

    [Fact]
    public void A_Disabled_Gate_Refuses_Even_A_Valid_Looking_Request()
    {
        var access = HubWebSocketAccess.Open(Path_);
        access.AllowOrigin(GoodOrigin);
        var token = access.IssueToken("demo");
        // Everything is in place EXCEPT the enable flag.

        Assert.Equal(WebSocketGateResult.Disabled, access.Check(GoodOrigin, token));
    }

    // ------------------------------------------------------------------ origin

    [Fact]
    public void A_Hostile_Page_Is_Refused_Even_Holding_A_Valid_Token()
    {
        // Cross-site WebSocket hijacking: the browser will open ws://127.0.0.1 from any page.
        // Origin is set by the browser and cannot be forged by page script, so it is what
        // separates the developer's own app from a tab they opened by accident.
        var (access, token) = Ready();

        Assert.Equal(WebSocketGateResult.OriginNotAllowed, access.Check(EvilOrigin, token));
    }

    [Fact]
    public void Revoking_An_Origin_Refuses_It_Again()
    {
        var (access, token) = Ready();
        Assert.Equal(WebSocketGateResult.Allowed, access.Check(GoodOrigin, token));

        Assert.True(access.RevokeOrigin(GoodOrigin));

        Assert.Equal(WebSocketGateResult.OriginNotAllowed, access.Check(GoodOrigin, token));
    }

    [Fact]
    public void An_Origin_Is_Stored_As_Scheme_Host_Port_So_It_Matches_The_Header()
    {
        var access = HubWebSocketAccess.Open(Path_);
        access.SetEnabled(true);

        // A user pasting their app's URL will include a path; a browser never sends one in
        // Origin. Storing it verbatim would produce an allowlist entry that can never match.
        Assert.True(access.AllowOrigin("http://localhost:5000/index.html"));

        Assert.Equal(["http://localhost:5000"], access.AllowedOrigins);
    }

    [Fact]
    public void A_Malformed_Origin_Is_Refused_Rather_Than_Stored()
    {
        var access = HubWebSocketAccess.Open(Path_);

        Assert.False(access.AllowOrigin("not a url"));
        Assert.Empty(access.AllowedOrigins);
    }

    // ------------------------------------------------------------------ token

    [Fact]
    public void No_Token_Is_Refused()
    {
        var (access, _) = Ready();

        Assert.Equal(WebSocketGateResult.TokenRejected, access.Check(GoodOrigin, null));
        Assert.Equal(WebSocketGateResult.TokenRejected, access.Check(GoodOrigin, ""));
    }

    [Fact]
    public void A_Wrong_Token_Is_Refused()
    {
        var (access, token) = Ready();

        Assert.Equal(WebSocketGateResult.TokenRejected, access.Check(GoodOrigin, token + "x"));
        Assert.Equal(WebSocketGateResult.TokenRejected, access.Check(GoodOrigin, "guess"));
    }

    [Fact]
    public void Revoking_A_Token_Refuses_It_Again()
    {
        var (access, token) = Ready();
        Assert.Equal(WebSocketGateResult.Allowed, access.Check(GoodOrigin, token));

        Assert.True(access.RevokeToken("demo"));

        Assert.Equal(WebSocketGateResult.TokenRejected, access.Check(GoodOrigin, token));
    }

    [Fact]
    public void A_Missing_Origin_Skips_Only_The_Origin_Check_Never_The_Token()
    {
        // A caller with no Origin is not a browser, which means it is the local-process case
        // the token exists to stop. Absent must never read as trusted.
        var (access, token) = Ready();

        Assert.Equal(WebSocketGateResult.TokenRejected, access.Check(origin: null, token: null));
        Assert.Equal(WebSocketGateResult.Allowed, access.Check(origin: null, token: token));
    }

    [Fact]
    public void Each_Issued_Token_Is_Distinct()
    {
        var access = HubWebSocketAccess.Open(Path_);

        var a = access.IssueToken("one");
        var b = access.IssueToken("two");

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 32, "a token should carry real entropy");
    }

    [Fact]
    public void Token_Values_Are_Never_Listed()
    {
        var access = HubWebSocketAccess.Open(Path_);
        var token = access.IssueToken("demo");

        Assert.Equal(["demo"], access.TokenLabels);
        Assert.DoesNotContain(token, access.TokenLabels);
    }

    [Fact]
    public void Both_Tokens_Stay_Valid_When_Two_Are_Issued()
    {
        // Guards the fixed-time matcher: it must not stop at the first entry it compares.
        var access = HubWebSocketAccess.Open(Path_);
        access.SetEnabled(true);
        access.AllowOrigin(GoodOrigin);

        var first = access.IssueToken("first");
        var second = access.IssueToken("second");

        Assert.Equal(WebSocketGateResult.Allowed, access.Check(GoodOrigin, first));
        Assert.Equal(WebSocketGateResult.Allowed, access.Check(GoodOrigin, second));
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public void Policy_Round_Trips_Through_Disk()
    {
        var (_, token) = Ready();

        var reloaded = HubWebSocketAccess.Open(Path_);

        Assert.True(reloaded.Enabled);
        Assert.Equal([GoodOrigin], reloaded.AllowedOrigins);
        Assert.Equal(WebSocketGateResult.Allowed, reloaded.Check(GoodOrigin, token));
    }

    [Fact]
    public void A_Corrupt_Policy_File_Fails_Closed()
    {
        // The opposite of HubSettings, deliberately. An unreadable preference should fall back
        // to the friendly default; an unreadable *access policy* must fall back to refusing.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ not valid json");

        var access = HubWebSocketAccess.Open(Path_);

        Assert.False(access.Enabled);
        Assert.Empty(access.AllowedOrigins);
        Assert.Equal(WebSocketGateResult.Disabled, access.Check(GoodOrigin, "anything"));
    }
}
