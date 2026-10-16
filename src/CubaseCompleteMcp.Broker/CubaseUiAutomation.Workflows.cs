using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Broker;

/// <summary>
/// Workflow dialog stages for tracks, projects and export, driven through native window handles.
/// Live findings on Cubase 15.0.10 (Korean Windows): the Windows common file dialogs expose their file-name field and
/// Save/Open/Cancel buttons to UI Automation only as unnamed/localized panes, and Cubase's own dialogs (Add Track,
/// Export Audio Mixdown) expose no controls at all. Common dialogs are therefore driven by their stable Win32 control IDs
/// (file name 1001/1148, IDOK 1, IDCANCEL 2, IDYES 6, IDNO 7) with WM_SETTEXT/WM_COMMAND, which needs no focus; Cubase
/// dialogs are driven by keyboard only while they are the verified foreground window. A dialog is accepted only when
/// it belongs to the identity-verified Cubase process and its title matches the stage; it is then bound to an opaque ID.
/// </summary>
public sealed partial class CubaseUiAutomation
{
    private readonly ConcurrentDictionary<string, OwnedDialog> _dialogs = new(StringComparer.Ordinal);

    private sealed record OwnedDialog(string Id, string Kind, IntPtr Handle, CubaseWindowTarget Target, string Title);

    private sealed record DialogSignature(string[] TitleFragments, bool CommonDialog, Func<IntPtr, bool> Structure);

    private const int IdOk = 1, IdCancel = 2, IdYes = 6, IdNo = 7;

    private static DialogSignature SignatureFor(string kind) => kind switch
    {
        DialogKinds.FileOpen => new(["Open Project", "프로젝트 열기", "Open", "열기"], true, handle => FileNameEdit(handle) != IntPtr.Zero && HasControl(handle, IdOk)),
        DialogKinds.FileSave => new(["Save As", "다른 이름으로 저장", "Save Project", "프로젝트 저장"], true, handle => FileNameEdit(handle) != IntPtr.Zero && HasControl(handle, IdOk)),
        DialogKinds.OverwritePrompt => new(["Confirm Save As", "다른 이름으로 저장 확인", "Save As", "다른 이름으로 저장", "Replace", "바꾸"], true,
            handle => FileNameEdit(handle) == IntPtr.Zero && HasControl(handle, IdYes) && HasControl(handle, IdNo)),
        DialogKinds.ExportAudio => new(["Export Audio Mixdown", "오디오 믹스다운 내보내기"], false, _ => true),
        DialogKinds.AddTrack => new(["Add Track", "트랙 추가"], false, _ => true),
        DialogKinds.CubasePrompt => new(["Cubase Pro", "Cubase"], false, handle => ClassName(handle) != "#32770"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown workflow dialog kind.")
    };

    public async Task<UiDialogRef?> WaitForDialogAsync(string kind, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signature = SignatureFor(kind);
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null) return null;
        var target = CreateTarget(process);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = RefreshTarget(target);
            if (current.IdentityVerified)
            {
                foreach (var (handle, title) in ProcessWindows(current.ProcessId))
                {
                    if (!TitleMatches(kind, title, signature) || !signature.Structure(handle)) continue;
                    if (signature.CommonDialog && ClassName(handle) != "#32770") continue;
                    var id = "dlg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                    _dialogs[id] = new OwnedDialog(id, kind, handle, current, title);
                    return new UiDialogRef(id, kind, title);
                }
            }
            if (clock.Elapsed >= timeout) return null;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TitleMatches(string kind, string title, DialogSignature signature) => kind == DialogKinds.CubasePrompt
        ? signature.TitleFragments.Any(fragment => title.Equals(fragment, StringComparison.Ordinal))
        : signature.TitleFragments.Any(fragment => title.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public bool IsDialogOpen(UiDialogRef dialog) =>
        _dialogs.TryGetValue(dialog.Id, out var owned) && IsWindow(owned.Handle) && IsWindowVisible(owned.Handle);

    /// <summary>Re-validates that the bound window is still the same live dialog of the same Cubase process.</summary>
    private OwnedDialog? Revalidate(UiDialogRef dialog)
    {
        if (!_dialogs.TryGetValue(dialog.Id, out var owned) || owned.Kind != dialog.Kind) return null;
        if (!IsWindow(owned.Handle) || !IsWindowVisible(owned.Handle)) return null;
        if (!RefreshTarget(owned.Target).IdentityVerified) return null;
        _ = GetWindowThreadProcessId(owned.Handle, out var pid);
        return pid == owned.Target.ProcessId ? owned : null;
    }

    public async Task<UiStageResult> SubmitPathAsync(UiDialogRef dialog, string absolutePath, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) return UiStageResult.Failure("submit_path", "INVALID_ARGUMENT", "Path must be absolute.");
        var owned = Revalidate(dialog);
        if (owned is null) return UiStageResult.Failure("submit_path", "WORKFLOW_DIALOG_LOST", "The bound file dialog is no longer open.");
        var edit = FileNameEdit(owned.Handle);
        if (edit == IntPtr.Zero || !HasControl(owned.Handle, IdOk))
            return UiStageResult.Failure("submit_path", "WORKFLOW_CONTROL_MISSING", "The file dialog has no file-name field (1001/1148) or accept button (IDOK).");
        var full = Path.GetFullPath(absolutePath);
        // Live on Cubase 15.0.10 the Save As item dialog ignores text set with WM_SETTEXT and saves under its prefilled
        // name, so the path is typed while the file-name field itself has keyboard focus, then read back before IDOK.
        for (var attempt = 0; attempt < 10 && GetForegroundWindow() != owned.Handle; attempt++)
        {
            _ = SetForegroundWindow(owned.Handle);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        if (GetForegroundWindow() != owned.Handle)
            return UiStageResult.Failure("submit_path", "FOREGROUND_NOT_DIALOG", "The file dialog is not the foreground window; nothing was typed or submitted.");
        if (FocusedWindow(owned.Handle) != edit)
            return UiStageResult.Failure("submit_path", "FILE_NAME_NOT_FOCUSED", "The file-name field does not have keyboard focus; nothing was typed or submitted.");
        SendKeyChord("ctrl+a");
        SendUnicodeText(full);
        string echoed = string.Empty;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            echoed = WindowText(edit);
            if (string.Equals(echoed, full, StringComparison.Ordinal)) break;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        if (!string.Equals(echoed, full, StringComparison.Ordinal))
            return UiStageResult.Failure("submit_path", "WORKFLOW_CONTROL_MISSING", "The file-name field did not show the typed path; it was not submitted.",
                new JsonObject { ["field_text"] = echoed });
        PressDialogButton(owned.Handle, IdOk);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(owned.Handle) || !IsWindowVisible(owned.Handle))
                return UiStageResult.Success("submit_path", new JsonObject { ["dialog_closed"] = true, ["path"] = full });
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return UiStageResult.Success("submit_path", new JsonObject { ["dialog_closed"] = false, ["path"] = full });
    }

    public Task<UiStageResult> AnswerPromptAsync(UiDialogRef prompt, bool accept, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = Revalidate(prompt);
        if (owned is null) return Task.FromResult(UiStageResult.Failure("answer_prompt", "WORKFLOW_DIALOG_LOST", "The prompt is no longer open."));
        var button = accept ? IdYes : IdNo;
        if (!HasControl(owned.Handle, button))
            return Task.FromResult(UiStageResult.Failure("answer_prompt", "WORKFLOW_CONTROL_MISSING", "The prompt does not have the expected Yes/No buttons."));
        PressDialogButton(owned.Handle, button);
        return Task.FromResult(UiStageResult.Success("answer_prompt", new JsonObject { ["button_id"] = button, ["accepted"] = accept }));
    }

    public async Task<UiStageResult> FillAddTrackDialogAsync(UiDialogRef dialog, string name, CancellationToken cancellationToken)
    {
        var owned = Revalidate(dialog);
        if (owned is null) return UiStageResult.Failure("add_track", "WORKFLOW_DIALOG_LOST", "The Add Track dialog is no longer open.");
        // Cubase focuses the Name field when the dialog opens; Enter triggers the default Add Track button.
        var typed = await TypeIntoForegroundAsync(owned.Handle, name, pressEnter: true, cancellationToken).ConfigureAwait(false);
        if (!typed.Ok) return typed;
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
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Cubase's Export Audio Mixdown dialog exposes no controls to UI Automation or Win32. On Cubase 15 it opens with the
    /// Name field focused and its text selected, so the file name is typed there while the dialog is the verified
    /// foreground window. The settings are then read back by OCR of the dialog and compared with a 48 kHz/24-bit WAV at
    /// the requested directory (Path follows "Use Project Mixdown Folder"). Nothing is started here.
    /// </summary>
    public async Task<UiStageResult> ConfigureExportAsync(UiDialogRef dialog, string directory, string fileName,
        CancellationToken cancellationToken)
    {
        var owned = Revalidate(dialog);
        if (owned is null) return UiStageResult.Failure("export_configure", "WORKFLOW_DIALOG_LOST", "The export dialog is no longer open.");
        var typed = await TypeIntoForegroundAsync(owned.Handle, fileName, pressEnter: false, cancellationToken).ConfigureAwait(false);
        if (!typed.Ok) return typed;
        SendKeyChord("tab"); // commits the name without pressing the dialog's default button
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        if (Revalidate(dialog) is null) return UiStageResult.Failure("export_configure", "WORKFLOW_DIALOG_LOST", "The export dialog closed while it was being configured.");
        var capture = CaptureWindow(owned.Handle, "export-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        if (capture is null) return UiStageResult.Failure("export_configure", "EXPORT_SETTINGS_UNREADABLE", "The export dialog could not be captured for read-back.");
        var reading = ExportDialogReading.Verify(await RecognizeTextAsync(capture, scale: 2).ConfigureAwait(false), directory, fileName);
        reading.Evidence["capture"] = capture;
        return reading.Ok
            ? UiStageResult.Success("export_configured", reading.Evidence)
            : UiStageResult.Failure("export_configure", "EXPORT_SETTINGS_MISMATCH", reading.Message!, reading.Evidence);
    }

    /// <summary>Cancels a dialog this workflow bound earlier. Any other window is refused.</summary>
    public async Task<UiStageResult> CancelDialogAsync(UiDialogRef dialog, CancellationToken cancellationToken)
    {
        var owned = Revalidate(dialog);
        if (owned is null) return UiStageResult.Failure("cancel", "WORKFLOW_DIALOG_LOST", "The bound dialog is no longer open.");
        if (ClassName(owned.Handle) == "#32770" && HasControl(owned.Handle, IdCancel)) PressDialogButton(owned.Handle, IdCancel);
        else
        {
            var escaped = await TypeIntoForegroundAsync(owned.Handle, null, pressEnter: false, cancellationToken, escape: true).ConfigureAwait(false);
            if (!escaped.Ok) return escaped;
        }
        _dialogs.TryRemove(dialog.Id, out _);
        return UiStageResult.Success("cancel");
    }

    public async Task<string?> ReadDialogTextAsync(UiDialogRef dialog, CancellationToken cancellationToken)
    {
        var owned = Revalidate(dialog);
        if (owned is null) return null;
        var path = CaptureWindow(owned.Handle, "prompt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        if (path is null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var lines = await RecognizeTextAsync(path).ConfigureAwait(false);
        return string.Join(" ", lines.OfType<JsonObject>().Select(line => line["text"]?.GetValue<string>()).Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    public async Task<UiStageResult> PressDefaultButtonAsync(UiDialogRef dialog, CancellationToken cancellationToken)
    {
        var owned = Revalidate(dialog);
        if (owned is null) return UiStageResult.Failure("default_button", "WORKFLOW_DIALOG_LOST", "The bound dialog is no longer open.");
        for (var attempt = 0; attempt < 10 && GetForegroundWindow() != owned.Handle; attempt++)
        {
            _ = SetForegroundWindow(owned.Handle);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        if (GetForegroundWindow() != owned.Handle)
            return UiStageResult.Failure("default_button", "FOREGROUND_NOT_DIALOG", "The owned dialog is not the foreground window; no key was sent.");
        SendKeyChord("enter");
        return UiStageResult.Success("default_button");
    }

    private async Task<UiStageResult> TypeIntoForegroundAsync(IntPtr dialog, string? text, bool pressEnter, CancellationToken cancellationToken,
        bool escape = false)
    {
        for (var attempt = 0; attempt < 10 && GetForegroundWindow() != dialog; attempt++)
        {
            _ = SetForegroundWindow(dialog);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        if (GetForegroundWindow() != dialog)
            return UiStageResult.Failure("keyboard", "FOREGROUND_NOT_DIALOG", "The owned dialog is not the foreground window; no keys were sent.");
        if (escape) { SendKeyChord("escape"); return UiStageResult.Success("keyboard"); }
        SendKeyChord("ctrl+a");
        if (!string.IsNullOrEmpty(text)) SendUnicodeText(text);
        if (pressEnter) SendKeyChord("enter");
        return UiStageResult.Success("keyboard", new JsonObject { ["characters"] = text?.Length ?? 0 });
    }

    /// <summary>
    /// Submits the MIDI import file dialog through its Win32 controls. Returns null when no such dialog is found,
    /// so the caller can fall back to UI Automation.
    /// </summary>
    private async Task<JsonObject?> SubmitMidiFileDialogNativeAsync(CubaseWindowTarget target, string absolutePath, CancellationToken cancellationToken)
    {
        IntPtr dialog = IntPtr.Zero;
        string title = string.Empty;
        for (var attempt = 0; attempt < 30 && dialog == IntPtr.Zero; attempt++)
        {
            var current = RefreshTarget(target);
            if (!current.IdentityVerified) return null;
            (dialog, title) = NativeFileDialog(current.ProcessId, midiImportOnly: true);
            if (dialog == IntPtr.Zero) await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        if (dialog == IntPtr.Zero) return null;
        var full = Path.GetFullPath(absolutePath);
        var edit = FileNameEdit(dialog);
        _ = SendMessage(edit, 0x000C /* WM_SETTEXT */, IntPtr.Zero, full);
        if (!string.Equals(WindowText(edit), full, StringComparison.Ordinal)) return null;
        PressDialogButton(dialog, IdOk);
        var consecutiveAbsent = 0;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            consecutiveAbsent = IsWindow(dialog) && IsWindowVisible(dialog) ? 0 : consecutiveAbsent + 1;
            if (Core.MidiImportWorkflowPolicy.IsFileDialogClosureStable(consecutiveAbsent)) break;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        var closed = !IsWindow(dialog) || !IsWindowVisible(dialog);
        return new JsonObject
        {
            ["dialog_title"] = title, ["button"] = "IDOK", ["path"] = full, ["submitted"] = true, ["dialog_closed"] = closed,
            ["method"] = "win32_control_ids", ["unverified_reason"] = Core.MidiImportWorkflowPolicy.SubmissionUnverifiedReason(closed)
        };
    }

    /// <summary>
    /// A visible common file dialog of the process (class #32770 with a file-name Edit and IDOK), optionally only the
    /// MIDI import one. UI Automation does not list these dialogs on Cubase 15, so stage detection uses this.
    /// </summary>
    private static (IntPtr Handle, string Title) NativeFileDialog(int processId, bool midiImportOnly)
    {
        foreach (var (handle, windowTitle) in ProcessWindows(processId))
        {
            if (ClassName(handle) != "#32770" || FileNameEdit(handle) == IntPtr.Zero || !HasControl(handle, IdOk)) continue;
            if (midiImportOnly && !IsMidiImportDialogTitle(windowTitle)) continue;
            return (handle, windowTitle);
        }
        return (IntPtr.Zero, string.Empty);
    }

    // ---- native helpers ----------------------------------------------------------------------------------------

    private static List<(IntPtr Handle, string Title)> ProcessWindows(int processId)
    {
        var windows = new List<(IntPtr, string)>();
        _ = EnumWindows((handle, parameter) =>
        {
            _ = GetWindowThreadProcessId(handle, out var owner);
            if (owner == processId && IsWindowVisible(handle)) windows.Add((handle, WindowText(handle)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static List<IntPtr> ChildWindows(IntPtr parent)
    {
        var children = new List<IntPtr>();
        _ = EnumChildWindows(parent, (handle, _) => { children.Add(handle); return true; }, IntPtr.Zero);
        return children;
    }

    private static bool HasControl(IntPtr dialog, int id) => ChildWindows(dialog).Any(child => GetDlgCtrlID(child) == id && IsWindowVisible(child));

    /// <summary>The Edit control of the common dialog file-name box (1001 in the item dialog, 1148 in the legacy one).</summary>
    private static IntPtr FileNameEdit(IntPtr dialog)
    {
        var children = ChildWindows(dialog);
        var direct = children.FirstOrDefault(child => ClassName(child) == "Edit" && GetDlgCtrlID(child) is 1001 or 1148 && IsWindowVisible(child));
        if (direct != IntPtr.Zero) return direct;
        foreach (var box in children.Where(child => GetDlgCtrlID(child) is 1001 or 1148 && ClassName(child).StartsWith("ComboBox", StringComparison.Ordinal)))
        {
            var edit = ChildWindows(box).FirstOrDefault(child => ClassName(child) == "Edit");
            if (edit != IntPtr.Zero) return edit;
        }
        return IntPtr.Zero;
    }

    private static void PressDialogButton(IntPtr dialog, int id)
    {
        var button = ChildWindows(dialog).FirstOrDefault(child => GetDlgCtrlID(child) == id);
        _ = PostMessage(dialog, 0x0111 /* WM_COMMAND */, new IntPtr(id), button); // BN_CLICKED (0) in the high word
    }

    private static string ClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string WindowText(IntPtr handle)
    {
        var length = (int)SendMessage(handle, 0x000E /* WM_GETTEXTLENGTH */, IntPtr.Zero, IntPtr.Zero);
        var builder = new StringBuilder(Math.Max(length + 1, 256));
        _ = SendMessage(handle, 0x000D /* WM_GETTEXT */, new IntPtr(builder.Capacity), builder);
        return builder.ToString();
    }

    /// <summary>The window with keyboard focus in the dialog's GUI thread, or zero.</summary>
    private static IntPtr FocusedWindow(IntPtr dialog)
    {
        var thread = GetWindowThreadProcessId(dialog, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    private delegate bool EnumChildCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, StringBuilder lParam);
}
