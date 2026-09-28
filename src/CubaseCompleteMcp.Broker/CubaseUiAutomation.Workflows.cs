using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace CubaseCompleteMcp.Broker;

/// <summary>
/// Workflow dialog stages for tracks, projects and export. A dialog is accepted only when the window ownership
/// policy accepts it for the declared stage AND both its title role and its control signature match; the found
/// window is then bound to an opaque ID so later steps cannot drift to another window.
/// Status: implemented against UI Automation, not validated on a live Cubase in this change (implemented_unvalidated).
/// </summary>
public sealed partial class CubaseUiAutomation
{
    private readonly ConcurrentDictionary<string, OwnedDialog> _dialogs = new(StringComparer.Ordinal);

    private sealed record OwnedDialog(string Id, string Kind, AutomationElement Element, IntPtr Handle, CubaseWindowTarget Target);

    private sealed record DialogSignature(CubaseDialogStage Stage, string[] TitleFragments, Func<AutomationElement, bool> Structure);

    private static DialogSignature SignatureFor(string kind) => kind switch
    {
        DialogKinds.FileOpen => new(CubaseDialogStage.FileDialog, ["Open Project", "프로젝트 열기", "Open", "열기"],
            window => FindFileNameEdit(window) is not null && FindButton(window, ["open", "열기"]) is not null),
        DialogKinds.FileSave => new(CubaseDialogStage.FileDialog, ["Save As", "다른 이름으로 저장", "Save Project", "프로젝트 저장"],
            window => FindFileNameEdit(window) is not null && FindButton(window, ["save", "저장"]) is not null),
        DialogKinds.ExportAudio => new(CubaseDialogStage.GenericModal, ["Export Audio Mixdown", "오디오 믹스다운 내보내기", "Audio Mixdown", "믹스다운"],
            window => FindButton(window, ["export audio", "export", "오디오 내보내기", "내보내기"]) is not null),
        DialogKinds.AddTrack => new(CubaseDialogStage.GenericModal, ["Add Track", "트랙 추가"],
            window => FindButton(window, ["add track", "트랙 추가"]) is not null),
        DialogKinds.OverwritePrompt => new(CubaseDialogStage.GenericModal, ["Replace", "already exists", "Confirm Save As", "바꾸", "이미 있", "덮어쓰"],
            window => FindFileNameEdit(window) is null
                && FindButton(window, ["replace", "yes", "바꾸기", "예"]) is not null
                && FindButton(window, ["no", "cancel", "아니요", "취소"]) is not null),
        DialogKinds.SaveChangesPrompt => new(CubaseDialogStage.GenericModal, ["Cubase", "Save", "저장"],
            window => FindButton(window, ["don't save", "do not save", "저장 안 함", "저장하지 않음"]) is not null
                && FindButton(window, ["save", "저장"]) is not null),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown workflow dialog kind.")
    };

    public async Task<UiDialogRef?> WaitForDialogAsync(string kind, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signature = SignatureFor(kind);
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null) return null;
        var target = CreateTarget(process);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = RefreshTarget(target);
                var window = FindProcessWindowCandidates(current, signature.Stage, signature.TitleFragments, signature.Structure)
                    .Where(candidate => MatchedTitleFragments(candidate.Current.Name, signature.TitleFragments).Count > 0
                        && ProbeStructuralRole(signature.Structure, candidate))
                    .LastOrDefault();
                if (window is not null)
                {
                    var id = "dlg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                    var dialog = new OwnedDialog(id, kind, window, new IntPtr(window.Current.NativeWindowHandle), current);
                    _dialogs[id] = dialog;
                    return new UiDialogRef(id, kind, window.Current.Name);
                }
            }
            catch (Exception ex) when (IsUnavailableElement(ex)) { }
            if (clock.Elapsed >= timeout) return null;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        while (true);
    }

    public bool IsDialogOpen(UiDialogRef dialog) =>
        _dialogs.TryGetValue(dialog.Id, out var owned) && owned.Handle != IntPtr.Zero && IsWindow(owned.Handle)
        && IsWindowVisible(owned.Handle);

    /// <summary>Re-validates that the bound window is still the same live dialog of the same Cubase process.</summary>
    private OwnedDialog? Revalidate(UiDialogRef dialog)
    {
        if (!_dialogs.TryGetValue(dialog.Id, out var owned) || owned.Kind != dialog.Kind) return null;
        if (owned.Handle == IntPtr.Zero || !IsWindow(owned.Handle)) return null;
        if (!RefreshTarget(owned.Target).IdentityVerified) return null;
        try
        {
            return ProbeStructuralRole(SignatureFor(owned.Kind).Structure, owned.Element) ? owned : null;
        }
        catch (Exception ex) when (IsUnavailableElement(ex)) { return null; }
    }

    public async Task<UiStageResult> SubmitPathAsync(UiDialogRef dialog, string absolutePath, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) return UiStageResult.Failure("submit_path", "INVALID_ARGUMENT", "Path must be absolute.");
        var owned = Revalidate(dialog);
        if (owned is null) return UiStageResult.Failure("submit_path", "WORKFLOW_DIALOG_LOST", "The bound file dialog is no longer open or no longer matches its stage.");
        try
        {
            var edit = FindFileNameEdit(owned.Element);
            var accept = dialog.Kind == DialogKinds.FileSave ? FindButton(owned.Element, ["save", "저장"]) : FindButton(owned.Element, ["open", "열기"]);
            if (edit is null || accept is null)
                return UiStageResult.Failure("submit_path", "WORKFLOW_CONTROL_MISSING", "The file dialog did not expose a file-name field and its accept button.");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(Path.GetFullPath(absolutePath));
            InvokeButton(accept);
        }
        catch (Exception ex) when (IsUnavailableElement(ex))
        {
            return UiStageResult.Failure("submit_path", "WORKFLOW_DIALOG_LOST", "The file dialog changed while the path was entered: " + ex.Message);
        }
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(owned.Handle) || !IsWindowVisible(owned.Handle))
                return UiStageResult.Success("submit_path", new JsonObject { ["dialog_closed"] = true, ["path"] = Path.GetFullPath(absolutePath) });
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return UiStageResult.Success("submit_path", new JsonObject { ["dialog_closed"] = false, ["path"] = Path.GetFullPath(absolutePath) });
    }

    public Task<UiStageResult> AnswerPromptAsync(UiDialogRef prompt, bool accept, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = Revalidate(prompt);
        if (owned is null) return Task.FromResult(UiStageResult.Failure("answer_prompt", "WORKFLOW_DIALOG_LOST", "The prompt is no longer open."));
        try
        {
            var button = accept ? FindButton(owned.Element, ["replace", "yes", "바꾸기", "예"]) : FindButton(owned.Element, ["no", "cancel", "아니요", "취소"]);
            if (button is null) return Task.FromResult(UiStageResult.Failure("answer_prompt", "WORKFLOW_CONTROL_MISSING", "The prompt did not expose the expected button."));
            var name = button.Current.Name;
            InvokeButton(button);
            return Task.FromResult(UiStageResult.Success("answer_prompt", new JsonObject { ["button"] = name, ["accepted"] = accept }));
        }
        catch (Exception ex) when (IsUnavailableElement(ex))
        {
            return Task.FromResult(UiStageResult.Failure("answer_prompt", "WORKFLOW_DIALOG_LOST", ex.Message));
        }
    }

    public async Task<UiStageResult> FillAddTrackDialogAsync(UiDialogRef dialog, string name, CancellationToken cancellationToken)
    {
        var owned = Revalidate(dialog);
        if (owned is null) return UiStageResult.Failure("add_track", "WORKFLOW_DIALOG_LOST", "The Add Track dialog is no longer open.");
        try
        {
            var edits = owned.Element.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>()
                .Where(element => element.TryGetCurrentPattern(ValuePattern.Pattern, out _)).ToArray();
            var nameEdit = edits.FirstOrDefault(element => element.Current.Name.Contains("name", StringComparison.OrdinalIgnoreCase)
                || element.Current.Name.Contains("이름", StringComparison.OrdinalIgnoreCase))
                ?? (edits.Length == 1 ? edits[0] : null);
            if (nameEdit is null)
                return UiStageResult.Failure("add_track", "WORKFLOW_CONTROL_MISSING",
                    $"The Add Track dialog exposes {edits.Length} editable fields and none is identifiable as the name field.");
            var countEdit = edits.FirstOrDefault(element => element.Current.Name.Contains("count", StringComparison.OrdinalIgnoreCase)
                || element.Current.Name.Contains("개수", StringComparison.OrdinalIgnoreCase));
            if (countEdit is not null) ((ValuePattern)countEdit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("1");
            ((ValuePattern)nameEdit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(name);
            var add = FindButton(owned.Element, ["add track", "트랙 추가"])
                ?? throw new InvalidOperationException("The Add Track button disappeared.");
            InvokeButton(add);
        }
        catch (Exception ex) when (IsUnavailableElement(ex))
        {
            return UiStageResult.Failure("add_track", "WORKFLOW_DIALOG_LOST", ex.Message);
        }
        for (var attempt = 0; attempt < 50 && IsWindow(owned.Handle) && IsWindowVisible(owned.Handle); attempt++)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        return UiStageResult.Success("add_track", new JsonObject { ["dialog_closed"] = !IsWindow(owned.Handle) || !IsWindowVisible(owned.Handle) });
    }

    /// <summary>
    /// Types into the rename field opened by the guarded rename command. Keys are sent only while the foreground
    /// window belongs to the identity-verified Cubase process.
    /// </summary>
    public async Task<UiStageResult> TypeRenameTextAsync(string name, CancellationToken cancellationToken)
    {
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null) return UiStageResult.Failure("rename_text", "CUBASE_NOT_RUNNING", "Cubase is not running.");
        var target = CreateTarget(process);
        var foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out var foregroundProcess);
        if (!target.IdentityVerified || foregroundProcess != target.ProcessId)
            return UiStageResult.Failure("rename_text", "FOREGROUND_NOT_CUBASE",
                "The foreground window does not belong to the verified Cubase process; no keys were sent.");
        SendKeyChord("ctrl+a");
        SendUnicodeText(name);
        SendKeyChord("enter");
        return UiStageResult.Success("rename_text", new JsonObject { ["characters"] = name.Length });
    }

    public async Task<UiStageResult> ConfigureAndStartExportAsync(UiDialogRef dialog, string directory, string fileName,
        CancellationToken cancellationToken)
    {
        var owned = Revalidate(dialog);
        if (owned is null) return UiStageResult.Failure("export_configure", "WORKFLOW_DIALOG_LOST", "The export dialog is no longer open.");
        try
        {
            var edits = owned.Element.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>()
                .Where(element => element.TryGetCurrentPattern(ValuePattern.Pattern, out _)).ToArray();
            var nameEdit = edits.FirstOrDefault(element => element.Current.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)
                || element.Current.Name.Contains("이름", StringComparison.OrdinalIgnoreCase));
            var pathEdit = edits.FirstOrDefault(element => element.Current.Name.Contains("path", StringComparison.OrdinalIgnoreCase)
                || element.Current.Name.Contains("경로", StringComparison.OrdinalIgnoreCase));
            if (nameEdit is null || pathEdit is null)
                return UiStageResult.Failure("export_configure", "WORKFLOW_CONTROL_MISSING",
                    "The export dialog did not expose identifiable name and path fields through UI Automation; format fields are verified from the WAV header afterwards.");
            ((ValuePattern)pathEdit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(directory);
            ((ValuePattern)nameEdit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(fileName);
            var export = FindButton(owned.Element, ["export audio", "export", "오디오 내보내기", "내보내기"])
                ?? throw new InvalidOperationException("The export button disappeared.");
            InvokeButton(export);
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            return UiStageResult.Success("export_started", new JsonObject { ["directory"] = directory, ["file_name"] = fileName });
        }
        catch (Exception ex) when (IsUnavailableElement(ex))
        {
            return UiStageResult.Failure("export_configure", "WORKFLOW_DIALOG_LOST", ex.Message);
        }
    }

    /// <summary>Cancels a dialog this workflow bound earlier. Any other window is refused.</summary>
    public Task<UiStageResult> CancelDialogAsync(UiDialogRef dialog, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = Revalidate(dialog);
        if (owned is null) return Task.FromResult(UiStageResult.Failure("cancel", "WORKFLOW_DIALOG_LOST", "The bound dialog is no longer open."));
        try
        {
            var cancel = FindButton(owned.Element, ["cancel", "취소"]);
            if (cancel is null) return Task.FromResult(UiStageResult.Failure("cancel", "WORKFLOW_CONTROL_MISSING", "The dialog has no Cancel button."));
            InvokeButton(cancel);
            _dialogs.TryRemove(dialog.Id, out _);
            return Task.FromResult(UiStageResult.Success("cancel"));
        }
        catch (Exception ex) when (IsUnavailableElement(ex))
        {
            return Task.FromResult(UiStageResult.Failure("cancel", "WORKFLOW_DIALOG_LOST", ex.Message));
        }
    }
}
