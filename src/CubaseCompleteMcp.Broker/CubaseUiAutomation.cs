using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Automation;
using System.Text;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace CubaseCompleteMcp.Broker;

public sealed class CubaseUiAutomation(CubaseProcessInspector processInspector, CubasePaths paths)
{
    private readonly ConcurrentDictionary<string, UiToken> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UiSnapshot> _snapshots = new(StringComparer.Ordinal);

    public bool HasOpenFileDialog()
    {
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null) return false;
        try { return FindFileDialog(CreateTarget(process)) is not null; }
        catch (Exception ex) when (IsUnavailableElement(ex)) { return false; }
    }

    public bool HasOpenMidiImportFileDialog()
    {
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null) return false;
        try { return FindMidiImportFileDialog(CreateTarget(process)) is not null; }
        catch (Exception ex) when (IsUnavailableElement(ex)) { return false; }
    }

    /// <summary>
    /// Binds a window lookup to the inspected Cubase process and re-verifies that the process id still
    /// belongs to it. Window ownership is never attributed to a Cubase process whose identity could not
    /// be confirmed.
    /// </summary>
    private CubaseWindowTarget CreateTarget(CubaseProcessState process)
    {
        var processId = process.ProcessId ?? throw new InvalidOperationException("Cubase is not running.");
        return new CubaseWindowTarget(processId, process.StartTime,
            processInspector.IsIdentityCurrent(processId, process.StartTime));
    }

    /// <summary>
    /// Re-verifies identity for the long polling loops. Unknown identity is never treated as trusted, so
    /// a process id reused after a Cubase restart stops being accepted in the middle of an operation.
    /// </summary>
    private CubaseWindowTarget RefreshTarget(CubaseWindowTarget target) => target with
    {
        IdentityVerified = processInspector.IsIdentityCurrent(target.ProcessId, target.StartTime)
    };

    public async Task<JsonObject> ObserveProjectTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null)
            throw new InvalidOperationException("Cubase is not running.");
        var projectWindow = FindProjectWindow(process.ProcessId.Value)
            ?? throw new InvalidOperationException("Cubase did not expose a visible project window for verification.");
        var handle = new IntPtr(projectWindow.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("The Cubase project window has no native handle.");
        var snapshotId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var screenshotPath = CaptureWindow(handle, snapshotId)
            ?? throw new InvalidOperationException("The Cubase project window could not be captured for verification.");
        var lines = await RecognizeTextAsync(screenshotPath).ConfigureAwait(false);
        var joined = string.Join("\n", lines.OfType<JsonObject>()
            .Select(line => line["text"]?.GetValue<string>()).Where(text => !string.IsNullOrWhiteSpace(text)));
        return new JsonObject
        {
            ["snapshot_id"] = snapshotId, ["window_title"] = projectWindow.Current.Name,
            ["capture_method"] = "PrintWindow.PW_RENDERFULLCONTENT+Windows.Media.Ocr",
            ["screenshot_path"] = screenshotPath, ["text"] = joined, ["lines"] = lines
        };
    }

    public async Task<JsonObject> OpenMidiImportDialogViaMenuAsync(CancellationToken cancellationToken)
    {
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null)
            throw new InvalidOperationException("Cubase is not running.");
        var target = CreateTarget(process);
        var processId = target.ProcessId;
        if (HasOpenMidiImportFileDialog())
            return new JsonObject { ["opened"] = true, ["already_open"] = true, ["path"] = "existing_midi_import_dialog" };
        if (HasOpenFileDialog())
            throw new InvalidOperationException("A non-MIDI Cubase file dialog is already open; close it before importing MIDI.");
        if (FindMidiImportProjectChoice(target) is not null)
            return new JsonObject { ["opened"] = true, ["already_open"] = true, ["path"] = "existing_project_choice" };

        var projectWindow = FindProjectWindow(processId)
            ?? throw new InvalidOperationException("Cubase did not expose a visible project window for MIDI import.");
        var handle = new IntPtr(projectWindow.Current.NativeWindowHandle);
        if (handle != IntPtr.Zero) _ = SetForegroundWindow(handle);

        var file = FindMenuItem(processId, projectWindow, ["file", "파일"])
            ?? throw new InvalidOperationException("Cubase did not expose the File menu through UI Automation.");
        var fileAction = InvokeMenuItem(file);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);

        var import = await WaitForMenuItemAsync(processId, projectWindow, ["import", "가져오기"], cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cubase did not expose File > Import through UI Automation.");
        var importAction = InvokeMenuItem(import);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);

        var midiFile = await WaitForMenuItemAsync(processId, projectWindow,
                ["midi file", "midi 파일"], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cubase did not expose File > Import > MIDI File through UI Automation.");
        var midiAction = InvokeMenuItem(midiFile);

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentTarget = RefreshTarget(target);
            if (FindMidiImportFileDialog(currentTarget) is not null
                || FindMidiImportProjectChoice(currentTarget) is not null)
            {
                return new JsonObject
                {
                    ["opened"] = true, ["already_open"] = false, ["path"] = "file_import_midi_menu",
                    ["file_action"] = fileAction, ["import_action"] = importAction, ["midi_file_action"] = midiAction
                };
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Cubase did not show a MIDI import dialog after invoking File > Import > MIDI File.");
    }

    public async Task<JsonObject?> DismissProjectFolderDialogAsync(CancellationToken cancellationToken)
    {
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null) return null;
        var target = CreateTarget(process);
        var nativeDialog = FindContainedDialogWindow(target, CubaseDialogStage.ProjectFolderChoice);
        if (nativeDialog != IntPtr.Zero)
        {
            if (!PostMessage(nativeDialog, 0x0010, IntPtr.Zero, IntPtr.Zero)) // WM_CLOSE
                throw new InvalidOperationException("The stale Cubase project-folder dialog could not be closed.");
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            return new JsonObject
            {
                ["dialog_title"] = "Set Project Folder", ["button"] = "WM_CLOSE", ["dismissed"] = !IsWindow(nativeDialog)
            };
        }
        var titleFragments = WindowOwnershipPolicy.TitleFragmentsFor(CubaseDialogStage.ProjectFolderChoice);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var window in FindProcessWindowCandidates(RefreshTarget(target),
                CubaseDialogStage.ProjectFolderChoice, titleFragments,
                candidate => FindButton(candidate, ["cancel", "취소"]) is not null))
            {
                try
                {
                    var title = window.Current.Name;
                    var cancel = FindButton(window, ["cancel", "취소"]);
                    var selectFolder = FindButton(window, ["select folder", "폴더 선택"]);
                    if (cancel is null || (selectFolder is null
                        && !titleFragments.Any(fragment =>
                            title.Contains(fragment, StringComparison.OrdinalIgnoreCase)))) continue;
                    var name = cancel.Current.Name;
                    var rectangle = cancel.Current.BoundingRectangle;
                    if (!rectangle.IsEmpty && rectangle.Width > 0 && rectangle.Height > 0)
                        ClickScreenPoint((int)(rectangle.X + rectangle.Width / 2),
                            (int)(rectangle.Y + rectangle.Height / 2));
                    else
                        InvokeButton(cancel);
                    return new JsonObject { ["dialog_title"] = title, ["button"] = name, ["dismissed"] = true };
                }
                catch (Exception ex) when (IsUnavailableElement(ex)) { }
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<JsonObject> SubmitFileDialogAsync(string absolutePath, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("File-dialog path must be absolute.");
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null) throw new InvalidOperationException("Cubase is not running.");
        var target = CreateTarget(process);
        Exception? unavailable = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentTarget = RefreshTarget(target);
            var dialog = FindMidiImportFileDialog(currentTarget);
            if (dialog is not null)
            {
                var submissionAttempted = false;
                var title = "Import MIDI File";
                var buttonName = "Open";
                try
                {
                    var edit = FindFileNameEdit(dialog)
                        ?? throw new InvalidOperationException("The Cubase file dialog did not expose a writable file-name field.");
                    title = dialog.Current.Name;
                    ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(Path.GetFullPath(absolutePath));
                    var button = FindMidiImportAcceptButton(dialog)
                        ?? throw new InvalidOperationException("The Cubase file dialog did not expose an Open/Import button.");
                    buttonName = button.Current.Name;
                    submissionAttempted = true;
                    InvokeButton(button);

                    var handle = new IntPtr(dialog.Current.NativeWindowHandle);
                    var closed = await WaitForFileDialogToCloseAsync(currentTarget, handle,
                        dialog, title, cancellationToken).ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["dialog_title"] = title, ["button"] = buttonName, ["path"] = Path.GetFullPath(absolutePath),
                        ["submitted"] = true, ["dialog_closed"] = closed,
                        ["unverified_reason"] = MidiImportWorkflowPolicy.SubmissionUnverifiedReason(closed)
                    };
                }
                catch (Exception ex) when (IsUnavailableElement(ex))
                {
                    unavailable = ex;
                    if (submissionAttempted)
                    {
                        var closed = await WaitForFileDialogToCloseAsync(currentTarget,
                            IntPtr.Zero, dialog, title, cancellationToken).ConfigureAwait(false);
                        return new JsonObject
                        {
                            ["dialog_title"] = title, ["button"] = buttonName,
                            ["path"] = Path.GetFullPath(absolutePath), ["submitted"] = true,
                            ["dialog_closed"] = closed, ["fallback"] = "uia_invoke_dialog_replaced",
                            ["unverified_reason"] = MidiImportWorkflowPolicy.SubmissionUnverifiedReason(closed)
                        };
                    }
                    var keyboardResult = await TrySubmitFileDialogByKeyboardAsync(
                        currentTarget, absolutePath, cancellationToken).ConfigureAwait(false);
                    if (keyboardResult is not null) return keyboardResult;
                }
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException(unavailable is null
            ? "Cubase did not open a targetable file dialog."
            : "Cubase file-dialog elements remained unavailable while the window was changing.", unavailable);
    }

    public async Task<JsonObject?> ChooseExistingProjectForMidiImportAsync(CancellationToken cancellationToken)
    {
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null) return null;
        var target = CreateTarget(process);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = FindMidiImportProjectChoice(RefreshTarget(target));
            if (window is not null)
            {
                try
                {
                    var existing = FindButton(window, ["no", "아니요"])
                        ?? throw new InvalidOperationException("The MIDI import project-choice dialog did not expose its No button.");
                    var title = window.Current.Name;
                    var name = existing.Current.Name;
                    InvokeButton(existing);
                    return new JsonObject
                    {
                        ["dialog_title"] = title, ["button"] = name, ["existing_project_selected"] = true
                    };
                }
                catch (Exception ex) when (IsUnavailableElement(ex)) { }
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<JsonObject> WaitForMidiImportStageAsync(int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null)
            throw new InvalidOperationException("Cubase is not running.");
        var target = CreateTarget(process);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentTarget = RefreshTarget(target);
            if (FindMidiImportProjectChoice(currentTarget) is not null)
                return new JsonObject { ["stage"] = "project_choice", ["elapsed_ms"] = stopwatch.ElapsedMilliseconds };
            if (FindMidiImportFileDialog(currentTarget) is not null)
                return new JsonObject { ["stage"] = "midi_file_dialog", ["elapsed_ms"] = stopwatch.ElapsedMilliseconds };
            if (FindImportOptionsDialog(currentTarget) is not null)
                return new JsonObject { ["stage"] = "import_options", ["elapsed_ms"] = stopwatch.ElapsedMilliseconds };
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return new JsonObject { ["stage"] = "none", ["elapsed_ms"] = stopwatch.ElapsedMilliseconds };
    }

    public async Task<JsonObject?> AcceptImportOptionsAsync(CancellationToken cancellationToken)
    {
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null) return null;
        var target = CreateTarget(process);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = FindImportOptionsDialog(RefreshTarget(target));
            if (dialog is not null)
            {
                var button = FindButton(dialog, ["ok", "확인", "import", "가져오기"]);
                if (button is null) return null;
                var title = dialog.Current.Name;
                var name = button.Current.Name;
                InvokeButton(button);
                return new JsonObject { ["dialog_title"] = title, ["button"] = name, ["accepted"] = true };
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<JsonObject> InspectAsync(JsonObject? arguments, string clientSessionId)
    {
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId is null)
        {
            throw new InvalidOperationException("Cubase is not running.");
        }

        PruneTokens();
        var titleFilter = arguments?["window_title"]?.GetValue<string>();
        var maxDepth = Math.Clamp(arguments?["max_depth"]?.GetValue<int>() ?? 5, 0, 12);
        var maxElements = Math.Clamp(arguments?["max_elements"]?.GetValue<int>() ?? 500, 1, 2000);
        var includeScreenshot = arguments?["include_screenshot"]?.GetValue<bool>() ?? true;
        var snapshotId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, process.ProcessId.Value);
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, condition).Cast<AutomationElement>().ToArray();
        if (!string.IsNullOrWhiteSpace(titleFilter))
        {
            windows = windows.Where(window => window.Current.Name.Contains(titleFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        if (windows.Length == 0)
        {
            throw new InvalidOperationException("No targetable Cubase window matched the request.");
        }

        var remaining = maxElements;
        var windowNodes = new JsonArray();
        foreach (var window in windows)
        {
            if (remaining <= 0)
            {
                break;
            }

            windowNodes.Add(BuildElement(window, process.ProcessId.Value, snapshotId, clientSessionId, 0, maxDepth, ref remaining));
        }

        string? screenshotPath = null;
        var captureHandle = new IntPtr(windows[0].Current.NativeWindowHandle);
        NativeRect captureRectangle = default;
        var hasCaptureTarget = captureHandle != IntPtr.Zero && GetWindowRect(captureHandle, out captureRectangle)
            && captureRectangle.Right > captureRectangle.Left && captureRectangle.Bottom > captureRectangle.Top;
        if (hasCaptureTarget)
        {
            _snapshots[snapshotId] = new UiSnapshot(process.ProcessId.Value, snapshotId, clientSessionId,
                captureHandle, captureRectangle, windows[0].Current.Name, DateTimeOffset.UtcNow.AddSeconds(30));
        }
        if (includeScreenshot && hasCaptureTarget)
        {
            screenshotPath = CaptureWindow(captureHandle, snapshotId);
        }

        var ocr = screenshotPath is null ? new JsonArray() : await RecognizeTextAsync(screenshotPath).ConfigureAwait(false);
        return new JsonObject
        {
            ["snapshot_id"] = snapshotId,
            ["snapshot_expires_at"] = hasCaptureTarget ? DateTimeOffset.UtcNow.AddSeconds(30) : null,
            ["process_id"] = process.ProcessId,
            ["window_count"] = windows.Length,
            ["elements_truncated"] = remaining <= 0,
            ["capture_method"] = screenshotPath is null ? "none" : "PrintWindow.PW_RENDERFULLCONTENT",
            ["ocr_available"] = OcrEngine.TryCreateFromUserProfileLanguages() is not null,
            ["ocr_lines"] = ocr,
            ["screenshot_path"] = screenshotPath,
            ["capture_window"] = hasCaptureTarget ? new JsonObject
            {
                ["title"] = windows[0].Current.Name,
                ["width"] = captureRectangle.Right - captureRectangle.Left,
                ["height"] = captureRectangle.Bottom - captureRectangle.Top,
                ["coordinate_space"] = "window_snapshot"
            } : null,
            ["windows"] = windowNodes
        };
    }

    private AutomationElement? FindFileDialog(CubaseWindowTarget target)
    {
        var candidates = FindProcessWindowCandidates(target, CubaseDialogStage.FileDialog, [],
            window => FindFileNameEdit(window) is not null && FindAcceptButton(window) is not null);
        return candidates.Count == 0 ? null : candidates[^1];
    }

    private AutomationElement? FindMidiImportFileDialog(CubaseWindowTarget target)
    {
        // Windows common-item dialogs can be owned by Cubase without appearing as a
        // RootElement child for the Cubase process. Resolve the native top-level
        // window first, then bridge back into UI Automation for the editable field
        // and Open button. The native lookup only accepts a window whose GW_OWNER
        // chain reaches the identity-verified Cubase process.
        var nativeDialog = FindContainedDialogWindow(target, CubaseDialogStage.MidiImportFileDialog);
        if (nativeDialog != IntPtr.Zero)
        {
            try
            {
                var candidate = AutomationElement.FromHandle(nativeDialog);
                if (candidate is not null && IsMidiImportDialogTitle(candidate.Current.Name)
                    && FindFileNameEdit(candidate) is not null && FindMidiImportAcceptButton(candidate) is not null)
                    return candidate;
            }
            catch (Exception ex) when (IsUnavailableElement(ex)) { }
        }

        var titleCondition = new OrCondition(
            new PropertyCondition(AutomationElement.NameProperty, "Import MIDI File"),
            new PropertyCondition(AutomationElement.NameProperty, "Import MIDI File..."),
            new PropertyCondition(AutomationElement.NameProperty, "Import MIDI File\u2026"),
            new PropertyCondition(AutomationElement.NameProperty, "MIDI 파일 가져오기"),
            new PropertyCondition(AutomationElement.NameProperty, "MIDI 파일 열기"));
        var declaredTitleFragments = WindowOwnershipPolicy.TitleFragmentsFor(CubaseDialogStage.MidiImportFileDialog);
        foreach (var window in FindProcessWindowCandidates(target, CubaseDialogStage.MidiImportFileDialog,
            declaredTitleFragments, candidate => FindFileNameEdit(candidate) is not null
                && FindMidiImportAcceptButton(candidate) is not null).Reverse())
        {
            try
            {
                var candidates = new List<AutomationElement>();
                if (IsMidiImportDialogTitle(window.Current.Name)) candidates.Add(window);
                candidates.AddRange(window.FindAll(TreeScope.Descendants, titleCondition).Cast<AutomationElement>());
                foreach (var candidate in candidates)
                {
                    if (FindFileNameEdit(candidate) is not null && FindMidiImportAcceptButton(candidate) is not null)
                        return candidate;
                }
            }
            catch (Exception ex) when (IsUnavailableElement(ex)) { }
        }
        return null;
    }

    private static bool IsMidiImportDialogTitle(string title)
    {
        var normalized = NormalizeMenuName(title);
        return normalized.Equals("Import MIDI File", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("MIDI 파일 가져오기", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("MIDI 파일 열기", StringComparison.OrdinalIgnoreCase);
    }

    private static AutomationElement? FindFileNameEdit(AutomationElement dialog)
    {
        var edits = dialog.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>()
            .Where(element => element.TryGetCurrentPattern(ValuePattern.Pattern, out _)).ToArray();
        return edits.OrderByDescending(element =>
        {
            var name = element.Current.Name;
            var automationId = element.Current.AutomationId;
            return automationId == "1148" ? 100
                : name.Contains("file name", StringComparison.OrdinalIgnoreCase) || name.Contains("파일 이름", StringComparison.OrdinalIgnoreCase) ? 50
                : name.Contains("search", StringComparison.OrdinalIgnoreCase) || name.Contains("검색", StringComparison.OrdinalIgnoreCase) ? -50 : 0;
        }).FirstOrDefault();
    }

    private static AutomationElement? FindAcceptButton(AutomationElement dialog) =>
        FindButton(dialog, ["open", "열기", "import", "가져오기", "save", "저장"]);

    private static AutomationElement? FindMidiImportAcceptButton(AutomationElement dialog) =>
        FindButton(dialog, ["open", "열기", "import", "가져오기"]);

    private async Task<JsonObject?> TrySubmitFileDialogByKeyboardAsync(
        CubaseWindowTarget target, string absolutePath, CancellationToken cancellationToken)
    {
        AutomationElement? dialog;
        try { dialog = FindMidiImportFileDialog(target); }
        catch (Exception ex) when (IsUnavailableElement(ex)) { return null; }
        if (dialog is null) return null;

        IntPtr handle;
        AutomationElement? edit;
        AutomationElement? button;
        string title;
        try
        {
            handle = new IntPtr(dialog.Current.NativeWindowHandle);
            title = dialog.Current.Name;
            edit = FindFileNameEdit(dialog);
            button = FindMidiImportAcceptButton(dialog);
        }
        catch (Exception ex) when (IsUnavailableElement(ex)) { return null; }

        if (edit is not null && button is not null)
        {
            try
            {
                var editHandle = new IntPtr(edit.Current.NativeWindowHandle);
                var buttonHandle = new IntPtr(button.Current.NativeWindowHandle);
                var nativeRoot = editHandle != IntPtr.Zero ? GetAncestor(editHandle, 2)
                    : buttonHandle != IntPtr.Zero ? GetAncestor(buttonHandle, 2) : IntPtr.Zero;
                if (nativeRoot != IntPtr.Zero) handle = nativeRoot;
                if (editHandle != IntPtr.Zero && buttonHandle != IntPtr.Zero)
                {
                    _ = SendMessage(editHandle, 0x000C, IntPtr.Zero, Path.GetFullPath(absolutePath)); // WM_SETTEXT
                    _ = SendMessage(buttonHandle, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
                    if (await WaitForFileDialogToCloseAsync(target, handle, dialog, title, cancellationToken).ConfigureAwait(false))
                    {
                        return new JsonObject
                        {
                            ["dialog_title"] = title, ["button"] = button.Current.Name,
                            ["path"] = Path.GetFullPath(absolutePath), ["submitted"] = true,
                            ["dialog_closed"] = true, ["fallback"] = "common_file_dialog_win32"
                        };
                    }
                    return new JsonObject
                    {
                        ["dialog_title"] = title, ["button"] = button.Current.Name,
                        ["path"] = Path.GetFullPath(absolutePath), ["submitted"] = true,
                        ["dialog_closed"] = false, ["fallback"] = "common_file_dialog_win32",
                        ["unverified_reason"] = MidiImportWorkflowPolicy.SubmissionUnverifiedReason(false)
                    };
                }

                var editRectangle = edit.Current.BoundingRectangle;
                var buttonRectangle = button.Current.BoundingRectangle;
                if (!editRectangle.IsEmpty && editRectangle.Width > 0 && editRectangle.Height > 0
                    && !buttonRectangle.IsEmpty && buttonRectangle.Width > 0 && buttonRectangle.Height > 0)
                {
                    if (handle != IntPtr.Zero) _ = SetForegroundWindow(handle);
                    ClickScreenPoint((int)(editRectangle.X + editRectangle.Width / 2),
                        (int)(editRectangle.Y + editRectangle.Height / 2));
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    SendKeyChord("CTRL+A");
                    SendUnicodeText(Path.GetFullPath(absolutePath));
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    ClickScreenPoint((int)(buttonRectangle.X + buttonRectangle.Width / 2),
                        (int)(buttonRectangle.Y + buttonRectangle.Height / 2));
                    var boundedClosed = await WaitForFileDialogToCloseAsync(target, handle,
                        dialog, title, cancellationToken).ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["dialog_title"] = title, ["button"] = button.Current.Name,
                        ["path"] = Path.GetFullPath(absolutePath), ["submitted"] = true,
                        ["dialog_closed"] = boundedClosed, ["fallback"] = "common_file_dialog_bounded_click",
                        ["unverified_reason"] = MidiImportWorkflowPolicy.SubmissionUnverifiedReason(boundedClosed)
                    };
                }
            }
            catch (Exception ex) when (IsUnavailableElement(ex)) { }
        }

        if (handle == IntPtr.Zero || !IsWindow(handle) || !SetForegroundWindow(handle) || GetForegroundWindow() != handle)
            return null;

        // Windows' common file dialog exposes Alt+N for the File name field even when
        // transient UIA descendants become stale while Cubase is creating the dialog.
        SendKeyChord("ALT+N");
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        SendKeyChord("CTRL+A");
        SendUnicodeText(Path.GetFullPath(absolutePath));
        SendKeyChord("ENTER");

        var closed = await WaitForFileDialogToCloseAsync(target, handle, dialog, title, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["dialog_title"] = title, ["button"] = "keyboard:Alt+N/Enter",
            ["path"] = Path.GetFullPath(absolutePath), ["submitted"] = true,
            ["dialog_closed"] = closed, ["fallback"] = "common_file_dialog_keyboard",
            ["unverified_reason"] = MidiImportWorkflowPolicy.SubmissionUnverifiedReason(closed)
        };
    }

    private async Task<bool> WaitForFileDialogToCloseAsync(
        CubaseWindowTarget target, IntPtr dialogHandle, AutomationElement dialog, string originalTitle,
        CancellationToken cancellationToken)
    {
        var consecutiveAbsent = 0;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { consecutiveAbsent = FindMidiImportFileDialog(target) is null ? consecutiveAbsent + 1 : 0; }
            catch (Exception ex) when (IsUnavailableElement(ex)) { consecutiveAbsent = 0; }
            if (MidiImportWorkflowPolicy.IsFileDialogClosureStable(consecutiveAbsent)) return true;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Generic modal lookup for callers that declare their own title fragments. It has no production call
    /// site today; it stays wired through the ownership policy so it cannot become an ownership bypass.
    /// </summary>
    private AutomationElement? FindModalDialog(CubaseWindowTarget target, string[] titleFragments) =>
        FindProcessWindowCandidates(target, CubaseDialogStage.GenericModal, titleFragments,
            window => MatchedTitleFragments(window.Current.Name, titleFragments).Count > 0).FirstOrDefault();

    private AutomationElement? FindMidiImportProjectChoice(CubaseWindowTarget target) =>
        FindProcessWindowCandidates(target, CubaseDialogStage.MidiImportProjectChoice, [],
            HasMidiImportProjectChoiceStructure).FirstOrDefault();

    /// <summary>The MIDI import project question exposes Create, Cancel, and No buttons.</summary>
    private static bool HasMidiImportProjectChoiceStructure(AutomationElement window) =>
        FindButton(window, ["create", "생성"]) is not null
        && FindButton(window, ["cancel", "취소"]) is not null
        && FindButton(window, ["no", "아니요"]) is not null;

    private AutomationElement? FindImportOptionsDialog(CubaseWindowTarget target)
    {
        var declaredTitleFragments = WindowOwnershipPolicy.TitleFragmentsFor(CubaseDialogStage.MidiImportOptions);
        foreach (var window in FindProcessWindowCandidates(target, CubaseDialogStage.MidiImportOptions,
            declaredTitleFragments, HasImportOptionsStructure))
        {
            try
            {
                if (HasImportOptionsStructure(window)
                    && MatchedTitleFragments(window.Current.Name, declaredTitleFragments).Count > 0)
                    return window;
            }
            catch (Exception ex) when (IsUnavailableElement(ex)) { }
        }
        return null;
    }

    /// <summary>
    /// Import-options dialogs expose an OK/Import button and are neither the MIDI file dialog nor the
    /// MIDI import project question.
    /// </summary>
    private static bool HasImportOptionsStructure(AutomationElement window)
    {
        if (IsMidiImportDialogTitle(window.Current.Name) || FindFileNameEdit(window) is not null) return false;
        if (FindButton(window, ["create", "생성"]) is not null
            && FindButton(window, ["no", "아니요"]) is not null) return false;
        return FindButton(window, ["ok", "확인", "import", "가져오기"]) is not null;
    }

    private static AutomationElement? FindProjectWindow(int processId)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        return AutomationElement.RootElement.FindAll(TreeScope.Children, condition).Cast<AutomationElement>()
            .Where(window =>
            {
                try
                {
                    var rectangle = window.Current.BoundingRectangle;
                    return !window.Current.IsOffscreen && !rectangle.IsEmpty && rectangle.Width > 0 && rectangle.Height > 0
                        && (window.Current.Name.Contains("Cubase Pro Project", StringComparison.OrdinalIgnoreCase)
                            || window.Current.Name.Contains("Cubase 프로젝트", StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex) when (IsUnavailableElement(ex)) { return false; }
            })
            .OrderByDescending(window => window.Current.BoundingRectangle.Width * window.Current.BoundingRectangle.Height)
            .FirstOrDefault();
    }

    private static async Task<AutomationElement?> WaitForMenuItemAsync(
        int processId, AutomationElement projectWindow, string[] names, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = FindMenuItem(processId, projectWindow, names);
            if (item is not null) return item;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static AutomationElement? FindMenuItem(int processId, AutomationElement projectWindow, string[] names)
    {
        var menuCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
        var candidates = new List<AutomationElement>();
        try { candidates.AddRange(projectWindow.FindAll(TreeScope.Descendants, menuCondition).Cast<AutomationElement>()); }
        catch (Exception ex) when (IsUnavailableElement(ex)) { }
        try
        {
            var condition = new AndCondition(menuCondition,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
            candidates.AddRange(AutomationElement.RootElement.FindAll(TreeScope.Descendants, condition)
                .Cast<AutomationElement>());
        }
        catch (Exception ex) when (IsUnavailableElement(ex)) { }
        return candidates.FirstOrDefault(item =>
        {
            try
            {
                if (item.Current.IsOffscreen) return false;
                var actual = NormalizeMenuName(item.Current.Name);
                return names.Any(name => actual.Equals(NormalizeMenuName(name), StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (IsUnavailableElement(ex)) { return false; }
        });
    }

    private static string InvokeMenuItem(AutomationElement item)
    {
        if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return "invoke";
        }
        if (item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        {
            ((ExpandCollapsePattern)expand).Expand();
            return "expand";
        }
        var rectangle = item.Current.BoundingRectangle;
        if (!rectangle.IsEmpty && rectangle.Width > 0 && rectangle.Height > 0)
        {
            ClickScreenPoint((int)(rectangle.X + rectangle.Width / 2), (int)(rectangle.Y + rectangle.Height / 2));
            return "bounded_click";
        }
        throw new InvalidOperationException($"Menu item '{item.Current.Name}' exposes no usable action.");
    }

    private static string NormalizeMenuName(string value) => value.Replace("&", "", StringComparison.Ordinal)
        .Replace("_", "", StringComparison.Ordinal).Trim().TrimEnd('.', '\u2026');

    /// <summary>
    /// Resolves the top-level windows that may act as a dialog of the declared workflow stage. Windows
    /// running in the identity-verified Cubase process are accepted directly; a foreign window is only
    /// accepted when its owner chain reaches a Cubase window. Screen overlap never grants ownership and
    /// an unverifiable Cubase process identity is never trusted.
    /// </summary>
    private IReadOnlyList<AutomationElement> FindProcessWindowCandidates(
        CubaseWindowTarget target, CubaseDialogStage stage, IReadOnlyList<string> declaredTitleFragments,
        Func<AutomationElement, bool> structuralRoleProbe)
    {
        var currentTarget = RefreshTarget(target);
        var cubaseWindows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, currentTarget.ProcessId))
            .Cast<AutomationElement>().ToArray();
        var cubaseHandles = new HashSet<IntPtr>();
        var cubaseRectangles = new List<NativeRect>();
        foreach (var window in cubaseWindows)
        {
            try
            {
                var handle = new IntPtr(window.Current.NativeWindowHandle);
                if (handle == IntPtr.Zero) continue;
                cubaseHandles.Add(handle);
                var bounds = window.Current.BoundingRectangle;
                if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                    cubaseRectangles.Add(ToNativeRect(bounds));
            }
            catch (Exception ex) when (IsUnavailableElement(ex)) { }
        }

        var candidates = new List<AutomationElement>();
        foreach (var window in cubaseWindows)
            CollectAcceptedWindow(currentTarget, stage, declaredTitleFragments, structuralRoleProbe,
                cubaseRectangles, window, candidates);
        foreach (var window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition)
                     .Cast<AutomationElement>())
        {
            try
            {
                if (cubaseHandles.Contains(new IntPtr(window.Current.NativeWindowHandle))) continue;
            }
            catch (Exception ex) when (IsUnavailableElement(ex)) { continue; }
            CollectAcceptedWindow(currentTarget, stage, declaredTitleFragments, structuralRoleProbe,
                cubaseRectangles, window, candidates);
        }
        return candidates;
    }

    /// <summary>Applies the ownership policy to one top-level candidate and keeps it when accepted.</summary>
    private static void CollectAcceptedWindow(CubaseWindowTarget target, CubaseDialogStage stage,
        IReadOnlyList<string> declaredTitleFragments, Func<AutomationElement, bool> structuralRoleProbe,
        IReadOnlyList<NativeRect> cubaseRectangles, AutomationElement window, List<AutomationElement> candidates)
    {
        try
        {
            var facts = ReadUiWindowFacts(window);
            if (!IsOwnershipAccepted(target, stage, declaredTitleFragments, facts,
                ProbeStructuralRole(structuralRoleProbe, window), cubaseRectangles)) return;
            candidates.Add(window);
        }
        catch (Exception ex) when (IsUnavailableElement(ex)) { }
    }

    private static bool ProbeStructuralRole(Func<AutomationElement, bool> probe, AutomationElement window)
    {
        try { return probe(window); }
        catch (Exception ex) when (IsUnavailableElement(ex)) { return false; }
    }

    /// <summary>Reads the facts UI Automation exposes for one top-level element.</summary>
    private static CandidateWindowFacts ReadUiWindowFacts(AutomationElement window)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        _ = GetWindowThreadProcessId(handle, out var ownerProcessId);
        var bounds = window.Current.BoundingRectangle;
        return new CandidateWindowFacts(handle,
            ownerProcessId != 0 ? ownerProcessId : window.Current.ProcessId,
            window.Current.Name,
            !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 ? ToNativeRect(bounds) : null,
            !window.Current.IsOffscreen && (handle == IntPtr.Zero || IsWindowVisible(handle)));
    }

    /// <summary>
    /// Native top-level window lookup used before bridging back into UI Automation. The window must be
    /// visible, carry the title role of the declared stage, expose a usable rectangle, and either run in
    /// the identity-verified Cubase process or be owned by a top-level window of that process.
    /// </summary>
    private IntPtr FindContainedDialogWindow(CubaseWindowTarget target, CubaseDialogStage stage)
    {
        var currentTarget = RefreshTarget(target);
        var declaredTitleFragments = WindowOwnershipPolicy.TitleFragmentsFor(stage);
        if (declaredTitleFragments.Count == 0) return IntPtr.Zero;
        var cubaseRectangles = CollectCubaseWindowRectangles(currentTarget.ProcessId);
        var match = IntPtr.Zero;
        _ = EnumWindows((handle, enumerateParameter) =>
        {
            if (handle == IntPtr.Zero) return true;
            var length = GetWindowTextLength(handle);
            if (length <= 0) return true;
            var title = new StringBuilder(length + 1);
            _ = GetWindowText(handle, title, title.Capacity);
            var windowTitle = title.ToString();
            if (MatchedTitleFragments(windowTitle, declaredTitleFragments).Count == 0) return true;
            _ = GetWindowThreadProcessId(handle, out var ownerProcessId);
            NativeRect? rectangle = null;
            if (GetWindowRect(handle, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                rectangle = bounds;
            var facts = new CandidateWindowFacts(handle, ownerProcessId, windowTitle, rectangle,
                IsWindowVisible(handle));
            if (!IsOwnershipAccepted(currentTarget, stage, declaredTitleFragments, facts,
                structuralRoleConfirmed: false, cubaseRectangles)) return true;
            match = handle;
            return false;
        }, IntPtr.Zero);
        return match;
    }

    /// <summary>
    /// Applies <see cref="WindowOwnershipPolicy"/> to one candidate window on native facts: the window
    /// runs in the identity-verified Cubase process, or its bounded GW_OWNER chain reaches a top-level
    /// window of that process while the window stays inside a Cubase window rectangle.
    /// </summary>
    private static bool IsOwnershipAccepted(CubaseWindowTarget target, CubaseDialogStage stage,
        IReadOnlyList<string> declaredTitleFragments, CandidateWindowFacts facts, bool structuralRoleConfirmed,
        IReadOnlyList<NativeRect> cubaseRectangles)
    {
        var ownerChain = WalkOwnerChain(RootWindowHandle(facts.Handle), target.ProcessId);
        var evidence = new WindowOwnershipEvidence
        {
            CandidateHandle = facts.Handle,
            CandidateProcessId = facts.ProcessId,
            CubaseProcessId = target.ProcessId,
            CubaseIdentityVerified = target.IdentityVerified,
            OwnerChainProcessId = ownerChain.IsCubaseProcess ? target.ProcessId
                : ownerChain.OwnerHandle == IntPtr.Zero ? null : ownerChain.OwnerProcessId,
            OwnerChainReachedCubaseTopLevel = ownerChain.IsCubaseTopLevel,
            ExpectedStage = stage,
            MatchedTitleFragments = MatchedTitleFragments(facts.Title, declaredTitleFragments),
            DeclaredTitleFragments = declaredTitleFragments,
            StructuralRoleConfirmed = structuralRoleConfirmed,
            IsVisible = facts.IsVisible,
            HasNonEmptyTitle = !string.IsNullOrWhiteSpace(facts.Title),
            HasNonEmptyRectangle = facts.Rectangle is not null,
            RectangleInsideCubaseWindow = facts.Rectangle is { } rectangle
                && IsCenterInsideCubaseWindow(rectangle, cubaseRectangles)
        };
        return WindowOwnershipPolicy.Evaluate(evidence).Accepted;
    }

    /// <summary>
    /// Walks the GW_OWNER chain (bounded) to find the window that owns the candidate. The attribution is
    /// the first owner running in the Cubase process; otherwise the last owner reached is reported, so a
    /// foreign window is never attributed to Cubase by its screen position.
    /// </summary>
    private static OwnerChainAttribution WalkOwnerChain(IntPtr handle, int cubaseProcessId)
    {
        var ownerHandle = IntPtr.Zero;
        var ownerProcessId = 0;
        var current = handle;
        for (var depth = 0; depth < OwnerChainDepth && current != IntPtr.Zero; depth++)
        {
            current = GetWindow(current, 4); // GW_OWNER
            if (current == IntPtr.Zero) break;
            ownerHandle = current;
            _ = GetWindowThreadProcessId(current, out ownerProcessId);
            if (ownerProcessId == cubaseProcessId)
                return new OwnerChainAttribution(current, ownerProcessId, true, IsTopLevelWindow(current));
        }
        return new OwnerChainAttribution(ownerHandle, ownerProcessId, false, false);
    }

    /// <summary>Resolves the top-level ancestor so ownership is judged on the real dialog window.</summary>
    private static IntPtr RootWindowHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(handle, 2); // GA_ROOT
        return root == IntPtr.Zero ? handle : root;
    }

    private static bool IsTopLevelWindow(IntPtr handle) => GetAncestor(handle, 2) == handle; // GA_ROOT

    /// <summary>Rectangles of the top-level windows that currently belong to the Cubase process.</summary>
    private static List<NativeRect> CollectCubaseWindowRectangles(int processId)
    {
        var rectangles = new List<NativeRect>();
        _ = EnumWindows((handle, enumerateParameter) =>
        {
            _ = GetWindowThreadProcessId(handle, out var ownerProcessId);
            if (ownerProcessId == processId && GetWindowRect(handle, out var rectangle)
                && rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top)
                rectangles.Add(rectangle);
            return true;
        }, IntPtr.Zero);
        return rectangles;
    }

    /// <summary>Declared title fragments that the window title actually contains.</summary>
    private static IReadOnlyList<string> MatchedTitleFragments(string title,
        IReadOnlyList<string> declaredTitleFragments) =>
        declaredTitleFragments.Where(fragment => title.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>Whether the candidate's center lies inside one of the Cubase window rectangles.</summary>
    private static bool IsCenterInsideCubaseWindow(NativeRect candidate, IReadOnlyList<NativeRect> cubaseRectangles)
    {
        var centerX = candidate.Left + (candidate.Right - candidate.Left) / 2;
        var centerY = candidate.Top + (candidate.Bottom - candidate.Top) / 2;
        return cubaseRectangles.Any(rectangle => centerX >= rectangle.Left && centerX <= rectangle.Right
            && centerY >= rectangle.Top && centerY <= rectangle.Bottom);
    }

    private static NativeRect ToNativeRect(System.Windows.Rect rectangle) => new()
    {
        Left = (int)rectangle.X,
        Top = (int)rectangle.Y,
        Right = (int)rectangle.Right,
        Bottom = (int)rectangle.Bottom
    };

    private static AutomationElement? FindButton(AutomationElement dialog, string[] names)
    {
        var buttons = dialog.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>();
        return buttons.FirstOrDefault(button =>
        {
            var actual = NormalizeButtonName(button.Current.Name);
            return names.Any(name => actual.Equals(NormalizeButtonName(name), StringComparison.OrdinalIgnoreCase)
                || actual.StartsWith(NormalizeButtonName(name) + " ", StringComparison.OrdinalIgnoreCase));
        });
    }

    private static void InvokeButton(AutomationElement button)
    {
        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return;
        }
        throw new InvalidOperationException($"Button '{button.Current.Name}' does not expose an invokable accessibility pattern.");
    }

    private static string NormalizeButtonName(string value) => value.Replace("&", "", StringComparison.Ordinal)
        .Replace("_", "", StringComparison.Ordinal).Trim().TrimEnd('.');

    private static bool IsUnavailableElement(Exception exception) => exception is ElementNotAvailableException
        or InvalidOperationException or COMException;

    private static async Task<JsonArray> RecognizeTextAsync(string path)
    {
        var output = new JsonArray();
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return output;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            foreach (var line in result.Lines)
            {
                var words = new JsonArray();
                foreach (var word in line.Words)
                {
                    var box = word.BoundingRect;
                    words.Add(new JsonObject
                    {
                        ["text"] = word.Text,
                        ["x"] = box.X,
                        ["y"] = box.Y,
                        ["width"] = box.Width,
                        ["height"] = box.Height
                    });
                }
                output.Add(new JsonObject { ["text"] = line.Text, ["words"] = words });
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
            output.Add(new JsonObject { ["error"] = error.Message });
        }
        return output;
    }

    public JsonObject Invoke(JsonObject arguments, string clientSessionId)
    {
        var action = arguments["action"]?.GetValue<string>()?.ToLowerInvariant()
            ?? throw new ArgumentException("action is required.");
        var tokenValue = arguments["element_token"]?.GetValue<string>();
        var snapshotValue = arguments["snapshot_id"]?.GetValue<string>();
        string snapshotId;
        string target;
        if (!string.IsNullOrWhiteSpace(tokenValue))
        {
            if (!_tokens.TryRemove(tokenValue, out var token) || token.ExpiresAt < DateTimeOffset.UtcNow
                || !token.ClientSessionId.Equals(clientSessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("The UI element token is unknown, expired, or belongs to another client session. Inspect the window again.");
            InvokeElement(token, arguments, action);
            snapshotId = token.SnapshotId;
            target = "uia_element";
        }
        else if (!string.IsNullOrWhiteSpace(snapshotValue))
        {
            if (!_snapshots.TryRemove(snapshotValue, out var snapshot) || snapshot.ExpiresAt < DateTimeOffset.UtcNow
                || !snapshot.ClientSessionId.Equals(clientSessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("The UI snapshot is unknown, expired, already used, or belongs to another client session. Inspect the window again.");
            InvokeSnapshot(snapshot, arguments, action);
            snapshotId = snapshot.SnapshotId;
            target = "snapshot_window";
        }
        else
        {
            throw new ArgumentException("element_token or snapshot_id is required.");
        }

        foreach (var pair in _tokens.Where(pair => pair.Value.SnapshotId == snapshotId).ToArray())
        {
            _tokens.TryRemove(pair.Key, out _);
        }
        _snapshots.TryRemove(snapshotId, out _);

        return new JsonObject
        {
            ["action"] = action,
            ["target"] = target,
            ["snapshot_id_consumed"] = snapshotId,
            ["invoked"] = true,
            ["verified"] = false,
            ["next_step"] = "Call cubase_ui_inspect to verify the resulting window state."
        };
    }

    private void InvokeElement(UiToken token, JsonObject arguments, string action)
    {
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId != token.ProcessId)
            throw new InvalidOperationException("Cubase restarted after this UI token was issued.");
        var element = token.Element;
        switch (action)
        {
            case "invoke": ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); break;
            case "toggle": ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle(); break;
            case "select": ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); break;
            case "expand": ((ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand(); break;
            case "collapse": ((ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); break;
            case "set_value": ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(
                arguments["value"]?.GetValue<string>() ?? string.Empty); break;
            case "focus": element.SetFocus(); break;
            case "click": ClickElement(element, token.ProcessId); break;
            default: throw new ArgumentOutOfRangeException(nameof(arguments), action,
                "This action requires a snapshot_id or is unsupported.");
        }
    }

    private void InvokeSnapshot(UiSnapshot snapshot, JsonObject arguments, string action)
    {
        ValidateSnapshotWindow(snapshot);
        var x = arguments["x"]?.GetValue<double>();
        var y = arguments["y"]?.GetValue<double>();
        switch (action)
        {
            case "click": MouseClick(snapshot, RequireCoordinate(x, "x"), RequireCoordinate(y, "y"), false, false); break;
            case "double_click": MouseClick(snapshot, RequireCoordinate(x, "x"), RequireCoordinate(y, "y"), true, false); break;
            case "right_click": MouseClick(snapshot, RequireCoordinate(x, "x"), RequireCoordinate(y, "y"), false, true); break;
            case "drag": MouseDrag(snapshot, RequireCoordinate(x, "x"), RequireCoordinate(y, "y"),
                RequireCoordinate(arguments["x2"]?.GetValue<double>(), "x2"), RequireCoordinate(arguments["y2"]?.GetValue<double>(), "y2")); break;
            case "scroll": MouseScroll(snapshot, RequireCoordinate(x, "x"), RequireCoordinate(y, "y"),
                Math.Clamp(arguments["delta"]?.GetValue<int>() ?? 120, -12000, 12000)); break;
            case "key": SendKeyChord(arguments["key"]?.GetValue<string>()
                ?? throw new ArgumentException("key is required.")); break;
            case "text": SendUnicodeText(arguments["value"]?.GetValue<string>()
                ?? throw new ArgumentException("value is required.")); break;
            default: throw new ArgumentOutOfRangeException(nameof(arguments), action,
                "Snapshot actions are click, double_click, right_click, drag, scroll, key, and text.");
        }
    }

    private JsonObject BuildElement(AutomationElement element, int processId, string snapshotId, string clientSessionId,
        int depth, int maxDepth, ref int remaining)
    {
        remaining--;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _tokens[token] = new UiToken(element, processId, snapshotId, clientSessionId, DateTimeOffset.UtcNow.AddSeconds(30));
        var rectangle = element.Current.BoundingRectangle;
        var patterns = new JsonArray();
        AddPattern(element, patterns, InvokePattern.Pattern, "invoke");
        AddPattern(element, patterns, TogglePattern.Pattern, "toggle");
        AddPattern(element, patterns, SelectionItemPattern.Pattern, "select");
        AddPattern(element, patterns, ExpandCollapsePattern.Pattern, "expand_collapse");
        AddPattern(element, patterns, ValuePattern.Pattern, "value");
        var children = new JsonArray();

        if (depth < maxDepth && remaining > 0)
        {
            var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            while (child is not null && remaining > 0)
            {
                children.Add(BuildElement(child, processId, snapshotId, clientSessionId, depth + 1, maxDepth, ref remaining));
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
        }

        return new JsonObject
        {
            ["token"] = token,
            ["name"] = element.Current.Name,
            ["automation_id"] = element.Current.AutomationId,
            ["control_type"] = element.Current.ControlType.ProgrammaticName,
            ["class_name"] = element.Current.ClassName,
            ["enabled"] = element.Current.IsEnabled,
            ["offscreen"] = element.Current.IsOffscreen,
            ["native_window_handle"] = element.Current.NativeWindowHandle,
            ["bounds"] = new JsonObject
            {
                ["x"] = rectangle.X,
                ["y"] = rectangle.Y,
                ["width"] = rectangle.Width,
                ["height"] = rectangle.Height
            },
            ["patterns"] = patterns,
            ["children"] = children
        };
    }

    private static void AddPattern(AutomationElement element, JsonArray patterns,
        AutomationPattern pattern, string name)
    {
        if (element.TryGetCurrentPattern(pattern, out _))
        {
            patterns.Add(name);
        }
    }

    private string? CaptureWindow(IntPtr handle, string snapshotId)
    {
        if (!GetWindowRect(handle, out var rectangle))
        {
            return null;
        }

        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
        {
            return null;
        }

        var directory = Path.Combine(paths.LocalStateRoot, "snapshots");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"{snapshotId}.png");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            if (!PrintWindow(handle, hdc, 2))
            {
                return null;
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        bitmap.Save(output, ImageFormat.Png);
        foreach (var old in Directory.EnumerateFiles(directory, "*.png")
                     .Select(path => new FileInfo(path))
                     .Where(file => file.CreationTimeUtc < DateTime.UtcNow.AddHours(-1)))
        {
            try { old.Delete(); } catch (IOException) { }
        }

        return output;
    }

    private static void ClickElement(AutomationElement element, int expectedProcessId)
    {
        var rectangle = element.Current.BoundingRectangle;
        if (rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw new InvalidOperationException("UI element does not have a clickable rectangle.");
        }

        var window = FindOwnerWindow(element);
        _ = GetWindowThreadProcessId(window, out var owner);
        if (window == IntPtr.Zero || owner != expectedProcessId || !SetForegroundWindow(window))
        {
            throw new InvalidOperationException("Could not safely activate the owning Cubase window.");
        }

        if (GetForegroundWindow() != window)
        {
            throw new InvalidOperationException("Cubase did not become the foreground window; click was cancelled.");
        }

        _ = GetCursorPos(out var original);
        try
        {
            _ = SetCursorPos((int)(rectangle.X + rectangle.Width / 2), (int)(rectangle.Y + rectangle.Height / 2));
            var inputs = new[]
            {
                new Input { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = 0x0002 } } },
                new Input { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = 0x0004 } } }
            };
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != (uint)inputs.Length)
            {
                throw new InvalidOperationException("SendInput did not deliver the complete click.");
            }
        }
        finally
        {
            _ = SetCursorPos(original.X, original.Y);
        }
    }

    private void ValidateSnapshotWindow(UiSnapshot snapshot)
    {
        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId != snapshot.ProcessId || !IsWindow(snapshot.WindowHandle))
            throw new InvalidOperationException("Cubase or the captured window changed after this snapshot was issued.");
        _ = GetWindowThreadProcessId(snapshot.WindowHandle, out var owner);
        if (owner != snapshot.ProcessId || !SetForegroundWindow(snapshot.WindowHandle)
            || GetForegroundWindow() != snapshot.WindowHandle)
            throw new InvalidOperationException("Could not safely activate the captured Cubase window.");
    }

    private static double RequireCoordinate(double? value, string name) =>
        value is { } number && double.IsFinite(number) ? number : throw new ArgumentException($"{name} is required and must be finite.");

    private static Point SnapshotPoint(UiSnapshot snapshot, double x, double y)
    {
        var width = snapshot.Rectangle.Right - snapshot.Rectangle.Left;
        var height = snapshot.Rectangle.Bottom - snapshot.Rectangle.Top;
        if (x < 0 || y < 0 || x >= width || y >= height)
            throw new ArgumentOutOfRangeException(nameof(x), $"Coordinates must be inside the {width}x{height} snapshot.");
        return new Point { X = snapshot.Rectangle.Left + (int)Math.Round(x), Y = snapshot.Rectangle.Top + (int)Math.Round(y) };
    }

    private static void MouseClick(UiSnapshot snapshot, double x, double y, bool doubleClick, bool rightClick)
    {
        var point = SnapshotPoint(snapshot, x, y);
        _ = GetCursorPos(out var original);
        try
        {
            _ = SetCursorPos(point.X, point.Y);
            var down = rightClick ? 0x0008u : 0x0002u;
            var up = rightClick ? 0x0010u : 0x0004u;
            var clicks = doubleClick ? 2 : 1;
            var inputs = Enumerable.Range(0, clicks).SelectMany(_ => new[]
            {
                MouseInputEvent(down), MouseInputEvent(up)
            }).ToArray();
            SendAll(inputs, "mouse click");
        }
        finally
        {
            _ = SetCursorPos(original.X, original.Y);
        }
    }

    private static void MouseDrag(UiSnapshot snapshot, double x, double y, double x2, double y2)
    {
        var start = SnapshotPoint(snapshot, x, y);
        var end = SnapshotPoint(snapshot, x2, y2);
        _ = GetCursorPos(out var original);
        var buttonDown = false;
        try
        {
            _ = SetCursorPos(start.X, start.Y);
            SendAll([MouseInputEvent(0x0002)], "mouse down");
            buttonDown = true;
            _ = SetCursorPos(end.X, end.Y);
            SendAll([MouseInputEvent(0x0004)], "mouse up");
            buttonDown = false;
        }
        finally
        {
            if (buttonDown)
            {
                try { SendAll([MouseInputEvent(0x0004)], "mouse up recovery"); }
                catch { /* Preserve the original failure after a best-effort release. */ }
            }
            _ = SetCursorPos(original.X, original.Y);
        }
    }

    private static void MouseScroll(UiSnapshot snapshot, double x, double y, int delta)
    {
        var point = SnapshotPoint(snapshot, x, y);
        _ = GetCursorPos(out var original);
        try
        {
            _ = SetCursorPos(point.X, point.Y);
            SendAll([new Input
            {
                Type = 0,
                Union = new InputUnion { Mouse = new MouseInput { MouseData = unchecked((uint)delta), Flags = 0x0800 } }
            }], "mouse wheel");
        }
        finally
        {
            _ = SetCursorPos(original.X, original.Y);
        }
    }

    private static void SendKeyChord(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) throw new ArgumentException("key must not be empty.");
        var tokens = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = new List<ushort>();
        ushort? key = null;
        foreach (var token in tokens)
        {
            var normalized = token.ToUpperInvariant();
            if (normalized is "WIN" or "WINDOWS" or "META" or "SUPER" or "COMMAND" or "CMD")
                throw new ArgumentException("Windows-key shortcuts are not allowed.");
            if (normalized is "CTRL" or "CONTROL") modifiers.Add(0x11);
            else if (normalized == "SHIFT") modifiers.Add(0x10);
            else if (normalized is "ALT" or "MENU") modifiers.Add(0x12);
            else if (key is null) key = VirtualKey(normalized);
            else throw new ArgumentException("A key chord can contain only one non-modifier key.");
        }
        if (key is null) throw new ArgumentException("A key chord requires one non-modifier key.");
        var inputs = new List<Input>();
        inputs.AddRange(modifiers.Select(code => KeyboardInputEvent(code, false)));
        inputs.Add(KeyboardInputEvent(key.Value, false));
        inputs.Add(KeyboardInputEvent(key.Value, true));
        inputs.AddRange(modifiers.AsEnumerable().Reverse().Select(code => KeyboardInputEvent(code, true)));
        SendAll(inputs.ToArray(), "keyboard chord");
    }

    private static void SendUnicodeText(string value)
    {
        if (value.Length > 4096) throw new ArgumentOutOfRangeException(nameof(value), "Text input is limited to 4096 UTF-16 code units.");
        var inputs = new List<Input>(value.Length * 2);
        foreach (var character in value)
        {
            inputs.Add(new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 } } });
            inputs.Add(new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 | 0x0002 } } });
        }
        SendAll(inputs.ToArray(), "unicode text");
    }

    private static ushort VirtualKey(string name)
    {
        if (name.Length == 1 && char.IsLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var function) && function is >= 1 and <= 24)
            return (ushort)(0x70 + function - 1);
        return name switch
        {
            "BACKSPACE" => 0x08, "TAB" => 0x09, "ENTER" or "RETURN" => 0x0D, "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, "END" => 0x23, "HOME" => 0x24,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28, "INSERT" => 0x2D,
            "DELETE" => 0x2E, "PLUS" => 0xBB, "COMMA" => 0xBC, "MINUS" => 0xBD, "PERIOD" => 0xBE,
            _ => throw new ArgumentException($"Unsupported key name '{name}'.")
        };
    }

    private static Input MouseInputEvent(uint flags) =>
        new() { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = flags } } };

    private static void ClickScreenPoint(int x, int y)
    {
        _ = GetCursorPos(out var original);
        try
        {
            if (!SetCursorPos(x, y)) throw new InvalidOperationException("Could not position the pointer on the Cubase dialog.");
            SendAll([MouseInputEvent(0x0002), MouseInputEvent(0x0004)], "bounded dialog click");
        }
        finally
        {
            _ = SetCursorPos(original.X, original.Y);
        }
    }

    private static Input KeyboardInputEvent(ushort virtualKey, bool keyUp) =>
        new() { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = keyUp ? 0x0002u : 0 } } };

    private static void SendAll(Input[] inputs, string operation)
    {
        if (inputs.Length == 0) return;
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != (uint)inputs.Length)
            throw new InvalidOperationException($"SendInput did not deliver the complete {operation}.");
    }

    private static IntPtr FindOwnerWindow(AutomationElement element)
    {
        AutomationElement? current = element;
        while (current is not null)
        {
            var handle = current.Current.NativeWindowHandle;
            if (handle != 0)
            {
                return new IntPtr(handle);
            }

            current = TreeWalker.ControlViewWalker.GetParent(current);
        }

        return IntPtr.Zero;
    }

    private void PruneTokens()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _tokens)
        {
            if (pair.Value.ExpiresAt < now)
            {
                _tokens.TryRemove(pair.Key, out _);
            }
        }
        foreach (var pair in _snapshots)
        {
            if (pair.Value.ExpiresAt < now) _snapshots.TryRemove(pair.Key, out _);
        }
    }

    private sealed record UiToken(AutomationElement Element, int ProcessId, string SnapshotId,
        string ClientSessionId, DateTimeOffset ExpiresAt);
    private sealed record UiSnapshot(int ProcessId, string SnapshotId, string ClientSessionId,
        IntPtr WindowHandle, NativeRect Rectangle, string WindowTitle, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Cubase process identity one dialog lookup is bound to. <see cref="StartTime"/> is the value the
    /// process reported when it was inspected, so a reused process id is detected instead of trusted.
    /// </summary>
    private sealed record CubaseWindowTarget(int ProcessId, DateTimeOffset? StartTime, bool IdentityVerified);

    /// <summary>Native facts about one candidate top-level window.</summary>
    private sealed record CandidateWindowFacts(IntPtr Handle, int ProcessId, string Title,
        NativeRect? Rectangle, bool IsVisible);

    /// <summary>Window the bounded GW_OWNER walk attributed the candidate to.</summary>
    private readonly record struct OwnerChainAttribution(IntPtr OwnerHandle, int OwnerProcessId,
        bool IsCubaseProcess, bool IsCubaseTopLevel);

    private const int OwnerChainDepth = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);
    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr handle, IntPtr targetDeviceContext, uint flags);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, string lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
