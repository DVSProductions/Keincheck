using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// Pipe names have to fit inside a macOS domain socket path.
///
/// On Unix, .NET implements named pipes as sockets at <c>$TMPDIR/CoreFxPipe_&lt;name&gt;</c>, and
/// macOS caps a domain socket path at 104 characters while handing each user a <c>TMPDIR</c>
/// like <c>/var/folders/xx/…/T/</c> — 49 of them. That leaves about 44 for the whole name, and
/// exceeding it throws <see cref="ArgumentOutOfRangeException"/> from inside the socket layer,
/// naming a path and nothing else. It is invisible on Windows and on Linux (<c>/tmp/</c>, and a
/// 108-character limit), which is exactly why it reached CI before anyone saw it.
/// </summary>
public sealed class PipeNameLengthTests
{
    /// <summary>The worst realistic macOS temp directory, measured from a real runner.</summary>
    private const int MacTmpDirLength = 49;

    private const int MacSocketPathLimit = 104;

    /// <summary>What a name may occupy once the temp dir and the BCL's prefix are spent.</summary>
    private static int Budget => MacSocketPathLimit - MacTmpDirLength - "CoreFxPipe_".Length;

    [Fact]
    public void The_Control_Pipe_Fits()
    {
        Assert.True(PipeNames.ControlPipe.Length <= Budget,
            $"'{PipeNames.ControlPipe}' is {PipeNames.ControlPipe.Length} chars, over the {Budget} budget");
    }

    [Fact]
    public void An_Mcp_Session_Pipe_Fits()
    {
        var name = PipeNames.McpSessionPipe("default");

        Assert.True(name.Length <= Budget, $"'{name}' is {name.Length} chars, over the {Budget} budget");
    }

    [Fact]
    public void A_Long_Token_Is_Shortened_Rather_Than_Overflowing()
    {
        var name = PipeNames.McpSessionPipe(new string('t', 200));

        Assert.True(name.Length <= Budget, $"'{name}' is {name.Length} chars, over the {Budget} budget");
    }

    [Fact]
    public void Shortening_Is_Stable_Across_Calls()
    {
        // The hub and the client compute the name independently from the same inputs. A
        // shortening that varied per call would have them listening and dialling different
        // sockets, which is a hang rather than an error.
        var token = new string('x', 100);

        Assert.Equal(PipeNames.McpSessionPipe(token), PipeNames.McpSessionPipe(token));
    }

    [Fact]
    public void Two_Long_Tokens_Sharing_A_Prefix_Do_Not_Collide()
    {
        // Plain truncation would map every token with the same first 20 characters onto one
        // socket, silently attaching a client to the wrong session.
        var a = PipeNames.McpSessionPipe(new string('y', 60) + "-alpha");
        var b = PipeNames.McpSessionPipe(new string('y', 60) + "-bravo");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_Short_Token_Is_Left_Exactly_As_It_Was()
    {
        // Wire compatibility: shortening must engage only when it has to, or an upgraded hub
        // and an older client would disagree about where to meet.
        Assert.Equal($"Keincheck.mcp.{PipeNames.UserScope}.default", PipeNames.McpSessionPipe("default"));
    }
}
