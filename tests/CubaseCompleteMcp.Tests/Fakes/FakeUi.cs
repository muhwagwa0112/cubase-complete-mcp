using System.Text.Json.Nodes;
using CubaseCompleteMcp.Broker;

namespace CubaseCompleteMcp.Tests.Fakes;

/// <summary>
/// Scripted UI state machine for workflow tests. Each dialog kind can be made to appear or not; actions are
/// counted so tests can prove that no key/click reached a window the workflow did not own.
/// </summary>
public sealed class FakeUi : IWorkflowUi
{
    public HashSet<string> AvailableDialogs { get; } = new(StringComparer.Ordinal);
    public List<string> Actions { get; } = [];
    public Func<string, string, bool>? OnSubmitPath { get; set; }
    public Func<string, bool>? OnFillAddTrack { get; set; }
    public Func<string, bool>? OnRenameText { get; set; }
    public bool OverwritePromptOnSave { get; set; }
    public bool? LastPromptAnswer { get; private set; }
    public string MidiStageAfterCommand { get; set; } = "midi_file_dialog";
    public string MidiStageAfterSubmit { get; set; } = "none";
    public Action<string>? OnMidiSubmit { get; set; }
    public int MenuFallbacks { get; private set; }
    public int MidiSubmissions { get; private set; }
    public int OptionsAccepted { get; private set; }
    private readonly Dictionary<string, UiDialogRef> _open = new(StringComparer.Ordinal);

    public Task<JsonObject> InspectAsync(JsonObject? arguments, string clientSessionId) =>
        Task.FromResult(new JsonObject { ["snapshot_id"] = "snap" });

    public HashSet<string> LiveSnapshots { get; } = new(StringComparer.Ordinal) { "snap" };

    public bool IsInputTargetValid(JsonObject arguments, string clientSessionId, out string reason)
    {
        var ok = arguments["snapshot_id"]?.GetValue<string>() is { } id && LiveSnapshots.Contains(id);
        reason = ok ? string.Empty : "expired";
        return ok;
    }

    public JsonObject Invoke(JsonObject arguments, string clientSessionId)
    {
        Actions.Add("invoke:" + arguments["action"]);
        return new JsonObject { ["invoked"] = true };
    }

    public Task<JsonObject> ObserveProjectTextAsync(CancellationToken cancellationToken) => Task.FromResult(new JsonObject { ["text"] = "" });
    public bool HasOpenFileDialog() => false;
    public bool HasOpenMidiImportFileDialog() => false;

    public Task<JsonObject> OpenMidiImportDialogViaMenuAsync(CancellationToken cancellationToken)
    {
        MenuFallbacks++;
        Actions.Add("menu_fallback");
        return Task.FromResult(new JsonObject { ["opened"] = true });
    }

    private bool _submittedThisImport;

    /// <summary>Called at the start of every import workflow; resets the per-import stage script.</summary>
    public Task<JsonObject?> DismissProjectFolderDialogAsync(CancellationToken cancellationToken)
    {
        _submittedThisImport = false;
        return Task.FromResult<JsonObject?>(null);
    }

    public TimeSpan SubmitDelay { get; set; }

    public async Task<JsonObject> SubmitFileDialogAsync(string absolutePath, CancellationToken cancellationToken)
    {
        MidiSubmissions++;
        if (SubmitDelay > TimeSpan.Zero) await Task.Delay(SubmitDelay, cancellationToken);
        _submittedThisImport = true;
        Actions.Add("midi_submit");
        OnMidiSubmit?.Invoke(absolutePath);
        return new JsonObject { ["dialog_closed"] = true, ["path"] = absolutePath };
    }

    public Task<JsonObject?> ChooseExistingProjectForMidiImportAsync(CancellationToken cancellationToken) =>
        Task.FromResult<JsonObject?>(new JsonObject { ["existing_project_selected"] = true });

    public Task<JsonObject> WaitForMidiImportStageAsync(int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var stage = _submittedThisImport ? MidiStageAfterSubmit : MidiStageAfterCommand;
        return Task.FromResult(new JsonObject { ["stage"] = stage });
    }

    public Task<JsonObject?> AcceptImportOptionsAsync(CancellationToken cancellationToken)
    {
        OptionsAccepted++;
        Actions.Add("accept_options");
        MidiStageAfterSubmit = "none";
        return Task.FromResult<JsonObject?>(new JsonObject { ["accepted"] = true });
    }

    public Task<UiDialogRef?> WaitForDialogAsync(string kind, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (kind == DialogKinds.OverwritePrompt && OverwritePromptOnSave && Actions.Contains("submit:" + DialogKinds.FileSave))
        {
            OverwritePromptOnSave = false;
            var prompt = new UiDialogRef("dlg-prompt", kind, "Replace?");
            _open[prompt.Id] = prompt;
            return Task.FromResult<UiDialogRef?>(prompt);
        }
        if (!AvailableDialogs.Remove(kind)) return Task.FromResult<UiDialogRef?>(null);
        var dialog = new UiDialogRef("dlg-" + kind, kind, kind);
        _open[dialog.Id] = dialog;
        return Task.FromResult<UiDialogRef?>(dialog);
    }

    public bool IsDialogOpen(UiDialogRef dialog) => _open.ContainsKey(dialog.Id);

    public Task<UiStageResult> SubmitPathAsync(UiDialogRef dialog, string absolutePath, CancellationToken cancellationToken)
    {
        if (!_open.ContainsKey(dialog.Id)) return Task.FromResult(UiStageResult.Failure("submit_path", "WORKFLOW_DIALOG_LOST", "not owned"));
        Actions.Add("submit:" + dialog.Kind);
        var ok = OnSubmitPath?.Invoke(dialog.Kind, absolutePath) ?? true;
        if (!OverwritePromptOnSave) _open.Remove(dialog.Id);
        return Task.FromResult(ok ? UiStageResult.Success("submit_path", new JsonObject { ["path"] = absolutePath })
            : UiStageResult.Failure("submit_path", "WORKFLOW_CONTROL_MISSING", "no field"));
    }

    public Task<UiStageResult> AnswerPromptAsync(UiDialogRef prompt, bool accept, CancellationToken cancellationToken)
    {
        Actions.Add("prompt:" + accept);
        LastPromptAnswer = accept;
        _open.Remove(prompt.Id);
        return Task.FromResult(UiStageResult.Success("answer_prompt"));
    }

    public Task<UiStageResult> FillAddTrackDialogAsync(UiDialogRef dialog, string name, CancellationToken cancellationToken)
    {
        Actions.Add("fill_add_track");
        _open.Remove(dialog.Id);
        return Task.FromResult(OnFillAddTrack?.Invoke(name) != false ? UiStageResult.Success("add_track")
            : UiStageResult.Failure("add_track", "WORKFLOW_CONTROL_MISSING", "no name field"));
    }

    public Task<UiStageResult> TypeRenameTextAsync(string name, CancellationToken cancellationToken)
    {
        Actions.Add("rename_text");
        return Task.FromResult(OnRenameText?.Invoke(name) != false ? UiStageResult.Success("rename_text")
            : UiStageResult.Failure("rename_text", "FOREGROUND_NOT_CUBASE", "not foreground"));
    }

    public Task<UiStageResult> ConfigureExportAsync(UiDialogRef dialog, string directory, string fileName, CancellationToken cancellationToken)
    {
        Actions.Add("export_configure");
        return Task.FromResult(OnExportConfigure?.Invoke(directory, fileName) != false ? UiStageResult.Success("export_configured")
            : UiStageResult.Failure("export_configure", "EXPORT_SETTINGS_MISMATCH", "settings differ"));
    }

    /// <summary>Read-back of the export settings; false models settings that differ from the request.</summary>
    public Func<string, string, bool>? OnExportConfigure { get; set; }

    public string? PromptText { get; set; }

    public Task<string?> ReadDialogTextAsync(UiDialogRef dialog, CancellationToken cancellationToken) => Task.FromResult(PromptText);

    public Task<UiStageResult> PressDefaultButtonAsync(UiDialogRef dialog, CancellationToken cancellationToken)
    {
        Actions.Add("default_button:" + PromptText);
        _open.Remove(dialog.Id);
        OnDefaultButton?.Invoke();
        return Task.FromResult(UiStageResult.Success("default_button"));
    }

    public Action? OnDefaultButton { get; set; }

    public Task<UiStageResult> CancelDialogAsync(UiDialogRef dialog, CancellationToken cancellationToken)
    {
        if (!_open.Remove(dialog.Id)) return Task.FromResult(UiStageResult.Failure("cancel", "WORKFLOW_DIALOG_LOST", "not owned"));
        Actions.Add("cancel:" + dialog.Kind);
        return Task.FromResult(UiStageResult.Success("cancel"));
    }
}
