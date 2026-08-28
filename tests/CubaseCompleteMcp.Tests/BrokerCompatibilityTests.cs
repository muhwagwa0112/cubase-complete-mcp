using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class BrokerCompatibilityTests
{
    private static readonly BuildIdentity Broker = new(1, "release-b", "sha", "broker.exe");

    [Fact]
    public void SameProtocolDifferentReleaseIsAllowedWithWarning()
    {
        var result = BrokerCompatibility.Evaluate(1, "release-a", Broker);
        Assert.True(result.ProtocolCompatible);
        Assert.True(result.ReleaseMismatch);
        Assert.Contains(result.Warnings, warning => warning.Contains("release-a", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferentProtocolIsRejected()
    {
        var result = BrokerCompatibility.Evaluate(2, "release-b", Broker);
        Assert.False(result.ProtocolCompatible);
        Assert.False(result.ReleaseMismatch);
    }

    [Fact]
    public void MissingProtocolUsesLegacyCompatibilityMode()
    {
        var result = BrokerCompatibility.Evaluate(null, null, Broker);
        Assert.True(result.ProtocolCompatible);
        Assert.True(result.LegacyClient);
        Assert.Contains(result.Warnings, warning => warning.Contains("compatibility mode", StringComparison.Ordinal));
    }
}
