using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// The version handshake: a peer advertising a version outside
/// <c>[Minimum, Current]</c> must be detectably incompatible so the broker can
/// reject the connection before exchanging any other message.
/// </summary>
public class ProtocolVersionTests
{
    [Fact]
    public void Current_IsAtLeastMinimum()
    {
        Assert.True(ProtocolVersion.Current >= ProtocolVersion.Minimum);
    }

    [Fact]
    public void Magic_IsTheFourByteToken()
    {
        Assert.Equal("AMCP", ProtocolVersion.Magic);
        Assert.Equal(4, ProtocolVersion.Magic.Length);
    }

    [Fact]
    public void IsCompatible_AtMinimum_And_AtCurrent_ReturnsTrue()
    {
        // Both ends of the supported band handshake successfully. (Minimum and
        // Current may currently be equal; both endpoints must still be accepted.)
        Assert.True(ProtocolVersion.IsCompatible(ProtocolVersion.Minimum));
        Assert.True(ProtocolVersion.IsCompatible(ProtocolVersion.Current));
    }

    [Fact]
    public void IsCompatible_BelowMinimum_ReturnsFalse()
    {
        Assert.False(ProtocolVersion.IsCompatible(ProtocolVersion.Minimum - 1));
    }

    [Fact]
    public void IsCompatible_AboveCurrent_ReturnsFalse()
    {
        Assert.False(ProtocolVersion.IsCompatible(ProtocolVersion.Current + 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void IsCompatible_OutOfRangeValues_ReturnFalse(int version)
    {
        // Defensive: only versions inside the supported band handshake successfully.
        var expected = version >= ProtocolVersion.Minimum && version <= ProtocolVersion.Current;
        Assert.Equal(expected, ProtocolVersion.IsCompatible(version));
        // For all these probe values the band excludes them.
        Assert.False(ProtocolVersion.IsCompatible(version));
    }

    [Fact]
    public void Handshake_FromRegisterMessage_IsCheckable()
    {
        // A future broker reads RegisterMessage.ProtocolVersion and gates on it.
        var compatible = new RegisterMessage { ClientId = "ok", ProtocolVersion = ProtocolVersion.Current };
        var future = new RegisterMessage { ClientId = "too-new", ProtocolVersion = ProtocolVersion.Current + 1 };
        var ancient = new RegisterMessage { ClientId = "too-old", ProtocolVersion = ProtocolVersion.Minimum - 1 };

        Assert.True(ProtocolVersion.IsCompatible(compatible.ProtocolVersion));
        Assert.False(ProtocolVersion.IsCompatible(future.ProtocolVersion));
        Assert.False(ProtocolVersion.IsCompatible(ancient.ProtocolVersion));
    }
}
