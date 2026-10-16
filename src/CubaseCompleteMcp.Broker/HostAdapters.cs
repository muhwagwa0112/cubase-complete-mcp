using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Broker;

public interface IHostProcessProbe
{
    CubaseProcessState Inspect();
}

public static class DialogKinds
{
    public const string FileOpen = "file_open";
    public const string FileSave = "file_save";
    public const string ExportAudio = "export_audio";
    public const string AddTrack = "add_track";
    public const string OverwritePrompt = "overwrite_prompt";
    /// <summary>A Cubase-rendered question box (title "Cubase Pro"); its text is read by OCR before any answer.</summary>
    public const string CubasePrompt = "cubase_prompt";
}

/// <summary>A dialog found for a workflow stage. The ID is bound to that native window; it is not a coordinate.</summary>
public sealed record UiDialogRef(string Id, string Kind, string Title);

public sealed record UiStageResult(bool Ok, string Stage, string? ErrorCode = null, string? Message = null,
    JsonObject? Evidence = null)
{
    public static UiStageResult Success(string stage, JsonObject? evidence = null) => new(true, stage, null, null, evidence);
    public static UiStageResult Failure(string stage, string code, string message, JsonObject? evidence = null) =>
        new(false, stage, code, message, evidence);
}

/// <summary>
/// Cubase-owned UI used by workflows. Every implementation must apply the window ownership policy (verified Cubase
/// PID and start time, owner chain, stage role) and must never act on a window it did not find for the declared stage.
/// </summary>
public interface IWorkflowUi
{
    Task<JsonObject> InspectAsync(JsonObject? arguments, string clientSessionId);
    JsonObject Invoke(JsonObject arguments, string clientSessionId);
    /// <summary>Checks, without consuming anything, that the token/snapshot of a UI input is live and owned by the caller.</summary>
    bool IsInputTargetValid(JsonObject arguments, string clientSessionId, out string reason);
    Task<JsonObject> ObserveProjectTextAsync(CancellationToken cancellationToken);

    bool HasOpenFileDialog();
    bool HasOpenMidiImportFileDialog();
    Task<JsonObject> OpenMidiImportDialogViaMenuAsync(CancellationToken cancellationToken);
    Task<JsonObject?> DismissProjectFolderDialogAsync(CancellationToken cancellationToken);
    Task<JsonObject> SubmitFileDialogAsync(string absolutePath, CancellationToken cancellationToken);
    Task<JsonObject?> ChooseExistingProjectForMidiImportAsync(CancellationToken cancellationToken);
    Task<JsonObject> WaitForMidiImportStageAsync(int timeoutMilliseconds, CancellationToken cancellationToken);
    Task<JsonObject?> AcceptImportOptionsAsync(CancellationToken cancellationToken);

    Task<UiDialogRef?> WaitForDialogAsync(string kind, TimeSpan timeout, CancellationToken cancellationToken);
    bool IsDialogOpen(UiDialogRef dialog);
    Task<UiStageResult> SubmitPathAsync(UiDialogRef dialog, string absolutePath, CancellationToken cancellationToken);
    Task<UiStageResult> AnswerPromptAsync(UiDialogRef prompt, bool accept, CancellationToken cancellationToken);
    Task<UiStageResult> FillAddTrackDialogAsync(UiDialogRef dialog, string name, CancellationToken cancellationToken);
    Task<UiStageResult> TypeRenameTextAsync(string name, CancellationToken cancellationToken);
    /// <summary>Sets the export file name and verifies the dialog's settings by read-back; starts nothing.</summary>
    Task<UiStageResult> ConfigureExportAsync(UiDialogRef dialog, string directory, string fileName,
        CancellationToken cancellationToken);
    Task<UiStageResult> CancelDialogAsync(UiDialogRef dialog, CancellationToken cancellationToken);
    /// <summary>Recognized text of a bound dialog (PrintWindow + Windows OCR); null when it cannot be read.</summary>
    Task<string?> ReadDialogTextAsync(UiDialogRef dialog, CancellationToken cancellationToken);
    /// <summary>Presses the default button (Enter) of a bound dialog while it is the verified foreground window.</summary>
    Task<UiStageResult> PressDefaultButtonAsync(UiDialogRef dialog, CancellationToken cancellationToken);
}

public sealed record PluginCatalogEntry(string Uid, string Name, string? Vendor, string? Category, bool Available);

public sealed record PluginCatalogSnapshot(bool Available, string Source, IReadOnlyList<PluginCatalogEntry> Entries,
    string? UnavailableReason);

/// <summary>
/// Installed plug-in UIDs and slot identity read-back. The production provider reports the catalog as unavailable:
/// the installed MIDI Remote API declares no plug-in enumeration member, and UIDs are never derived from names.
/// </summary>
public interface IPluginCatalogProvider
{
    Task<PluginCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken);
    /// <summary>The UID actually loaded in a slot, or null when the host offers no way to read it back.</summary>
    Task<string?> ObserveSlotPluginUidAsync(TargetIdentity target, long slotObjectId, CancellationToken cancellationToken);
}

public sealed class UnavailablePluginCatalogProvider : IPluginCatalogProvider
{
    public Task<PluginCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PluginCatalogSnapshot(false, "midi_remote_api", [],
            "The installed MIDI Remote API declares no plug-in enumeration member, so installed plug-in UIDs cannot be listed or verified."));

    public Task<string?> ObserveSlotPluginUidAsync(TargetIdentity target, long slotObjectId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}

/// <summary>Timing policy (D08). Tests shorten these; production uses the defaults.</summary>
public sealed record ControllerTimings(
    ObservationTimings TransportAndMixer,
    ObservationTimings TrackAndPlugin,
    ObservationTimings Project,
    ObservationTimings Export,
    TimeSpan DialogStage,
    TimeSpan MidiImportTotal)
{
    public static readonly ControllerTimings Default = new(ObservationTimings.TransportAndMixer,
        ObservationTimings.TrackAndPlugin, ObservationTimings.Project, ObservationTimings.Export,
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
}
