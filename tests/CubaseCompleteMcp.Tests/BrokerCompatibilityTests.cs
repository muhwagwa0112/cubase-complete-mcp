using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class BrokerCompatibilityTests
{
    private static readonly BuildIdentity Broker = new(CubaseConstants.PipeProtocolVersion, "release-b", "sha", "broker.exe");

    [Fact]
    public void SameProtocolDifferentReleaseIsAllowedWithWarning()
    {
        var result = BrokerCompatibility.Evaluate(CubaseConstants.PipeProtocolVersion, "release-a", Broker);
        Assert.True(result.ProtocolCompatible);
        Assert.True(result.ReleaseMismatch);
        Assert.Contains(result.Warnings, warning => warning.Contains("release-a", StringComparison.Ordinal));
    }

    [Fact]
    public void ProtocolOneClientIsRejected()
    {
        var result = BrokerCompatibility.Evaluate(1, "release-b", Broker);
        Assert.False(result.ProtocolCompatible);
        Assert.True(result.LegacyClient);
        Assert.False(result.ReleaseMismatch);
    }

    [Fact]
    public void MissingProtocolIsRejectedInsteadOfUsingACompatibilityMode()
    {
        var result = BrokerCompatibility.Evaluate(null, null, Broker);
        Assert.False(result.ProtocolCompatible);
        Assert.True(result.LegacyClient);
        Assert.Contains(result.Warnings, warning => warning.Contains("protocol 2 requires it", StringComparison.Ordinal));
    }
}
