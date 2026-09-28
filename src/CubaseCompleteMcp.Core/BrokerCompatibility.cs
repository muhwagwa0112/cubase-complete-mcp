namespace CubaseCompleteMcp.Core;

public sealed record BrokerCompatibilityResult(
    bool ProtocolCompatible,
    bool LegacyClient,
    bool ReleaseMismatch,
    string[] Warnings);

/// <summary>
/// Pipe protocol 2 changed target, confirmation and outcome semantics, so a client that does not declare
/// protocol 2 is refused before any work is done. There is no v1 compatibility mode for writes.
/// </summary>
public static class BrokerCompatibility
{
    public static BrokerCompatibilityResult Evaluate(int? clientProtocolVersion, string? clientReleaseId,
        BuildIdentity broker)
    {
        var legacy = clientProtocolVersion is null || clientProtocolVersion < broker.PipeProtocolVersion;
        var protocolCompatible = clientProtocolVersion == broker.PipeProtocolVersion;
        var releaseMismatch = !string.IsNullOrWhiteSpace(clientReleaseId)
            && !string.IsNullOrWhiteSpace(broker.ReleaseId)
            && !clientReleaseId.Equals(broker.ReleaseId, StringComparison.Ordinal);
        var warnings = new List<string>();
        if (clientProtocolVersion is null)
            warnings.Add("The client did not identify its pipe protocol version; protocol 2 requires it, so the request was refused.");
        if (releaseMismatch)
            warnings.Add($"Client Release '{clientReleaseId}' differs from Broker Release '{broker.ReleaseId}'.");
        return new BrokerCompatibilityResult(protocolCompatible, legacy, releaseMismatch, warnings.ToArray());
    }
}
