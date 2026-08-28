using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CubaseCompleteMcp.Core;

public static class CubaseConstants
{
    public const string ProductName = "Cubase Complete MCP";
    public const string DeviceVendor = "CBMCP";
    public const string DeviceName = "CompleteBridge";
    public const string PipeName = "CubaseCompleteMcp.v1";
    public const string InputPortName = "CubaseMCP To Cubase";
    public const string OutputPortName = "CubaseMCP From Cubase";
    public const string ServerName = "cubase-complete";
    public const int PipeProtocolVersion = 1;
    public const int MinimumCubaseBuildForApi13 = 20;
}

[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    None,
    Low,
    Confirm,
    Forbidden
}

[JsonConverter(typeof(JsonStringEnumConverter<ResultStatus>))]
public enum ResultStatus
{
    Ok,
    Unverified,
    RequiresConfirmation,
    Unsupported,
    Offline,
    Error
}

public sealed record BrokerRequest(
    string Id,
    string Operation,
    JsonObject? Arguments = null,
    string? ConfirmationId = null,
    string? ClientSessionId = null,
    int? ClientProtocolVersion = null,
    string? ClientReleaseId = null);

public sealed record BrokerResponse(
    string Id,
    ResultStatus Status,
    JsonNode? Data = null,
    BrokerError? Error = null,
    OperationMetadata? Metadata = null)
{
    public static BrokerResponse Success(string id, JsonNode? data, string path, bool verified = true,
        JsonNode? before = null, JsonNode? after = null, IReadOnlyList<string>? warnings = null,
        long sessionRevision = 0) =>
        new(id, verified ? ResultStatus.Ok : ResultStatus.Unverified, data, null,
            new OperationMetadata(path, verified, before, after, warnings ?? [], sessionRevision));

    public static BrokerResponse Failure(string id, ResultStatus status, string code, string message,
        JsonNode? details = null, string path = "broker") =>
        new(id, status, null, new BrokerError(code, message, details),
            new OperationMetadata(path, false, null, null, [], 0));
}

public sealed record BrokerError(string Code, string Message, JsonNode? Details = null);

public sealed record OperationMetadata(
    string PathUsed,
    bool Verified,
    JsonNode? Before,
    JsonNode? After,
    IReadOnlyList<string> Warnings,
    long SessionRevision);

public sealed record CapabilityDescriptor(
    string Id,
    string Name,
    string Category,
    string Path,
    RiskLevel Risk,
    string SupportLevel,
    string[] Preconditions,
    bool Verified);

public sealed record CommandCapabilityDescriptor(
    string Id,
    string Category,
    string Name,
    int Index,
    string Path,
    RiskLevel Risk,
    string SupportLevel,
    string[] Preconditions,
    bool CanPerformAvailable,
    bool Verified);

public sealed record DirectRootDescriptor(
    string Id,
    string Name,
    string SupportLevel,
    string[] Preconditions);

public sealed record UiActionDescriptor(
    string Action,
    string Target,
    RiskLevel Risk,
    string SupportLevel,
    string[] RequiredArguments,
    string VerificationContract);

public sealed record MidiCompositionActionDescriptor(
    string Action,
    string Category,
    RiskLevel Risk,
    string SupportLevel,
    string[] RequiredArguments,
    string Description);

public sealed record CubaseStatusSnapshot(
    bool CubaseRunning,
    string? CubaseVersion,
    int? CubaseProcessId,
    string? WindowTitle,
    bool BrokerOnline,
    bool MidiInputAvailable,
    bool MidiOutputAvailable,
    bool DriverOnline,
    bool Api13Available,
    int CommandCount,
    int CommandCategoryCount,
    long SessionRevision,
    string[] Warnings,
    string ConnectionState,
    [property: JsonPropertyName("loopmidi_installed")] bool LoopMidiInstalled,
    [property: JsonPropertyName("loopmidi_process_running")] bool LoopMidiProcessRunning,
    [property: JsonPropertyName("loopmidi_autostart_attempted")] bool LoopMidiAutostartAttempted,
    [property: JsonPropertyName("loopmidi_autostart_result")] string LoopMidiAutostartResult,
    [property: JsonPropertyName("configured_loopmidi_ports")] string[] ConfiguredLoopMidiPorts,
    MidiPortSnapshot[] ObservedMidiPorts,
    bool CubaseRestartRequired,
    string? ClientReleaseId,
    string? BrokerReleaseId,
    bool ReleaseMismatch);

public sealed record MidiPortSnapshot(uint Id, string Name, bool IsInput);

public sealed record KeyCommandDescriptor(string Id, string Category, string Name, int Index);

public sealed record ConfirmationProposal(
    string ConfirmationId,
    string Operation,
    RiskLevel Risk,
    string Summary,
    DateTimeOffset ExpiresAt,
    string ArgumentsHash);
