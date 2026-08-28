namespace CubaseCompleteMcp.Core;

public sealed record BrokerCompatibilityResult(
    bool ProtocolCompatible,
    bool LegacyClient,
    bool ReleaseMismatch,
    string[] Warnings);

public static class BrokerCompatibility
{
    public static BrokerCompatibilityResult Evaluate(int? clientProtocolVersion, string? clientReleaseId,
        BuildIdentity broker)
    {
        var legacy = clientProtocolVersion is null;
        var protocolCompatible = legacy || clientProtocolVersion == broker.PipeProtocolVersion;
        var releaseMismatch = !string.IsNullOrWhiteSpace(clientReleaseId)
            && !string.IsNullOrWhiteSpace(broker.ReleaseId)
            && !clientReleaseId.Equals(broker.ReleaseId, StringComparison.Ordinal);
        var warnings = new List<string>();
        if (legacy) warnings.Add("The client did not identify its pipe protocol version; compatibility mode was used.");
        if (releaseMismatch)
            warnings.Add($"Client Release '{clientReleaseId}' differs from Broker Release '{broker.ReleaseId}'.");
        return new BrokerCompatibilityResult(protocolCompatible, legacy, releaseMismatch, warnings.ToArray());
    }
}
