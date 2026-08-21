using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Automation;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace CubaseCompleteMcp.Broker;

public sealed class CubaseUiAutomation(CubaseProcessInspector processInspector, CubasePaths paths)
{
    private readonly ConcurrentDictionary<string, UiToken> _tokens = new(StringComparer.Ordinal);

    public async Task<JsonObject> InspectAsync(JsonObject? arguments)
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

            windowNodes.Add(BuildElement(window, process.ProcessId.Value, snapshotId, 0, maxDepth, ref remaining));
        }

        string? screenshotPath = null;
        if (includeScreenshot)
        {
            var handle = new IntPtr(windows[0].Current.NativeWindowHandle);
            if (handle != IntPtr.Zero)
            {
                screenshotPath = CaptureWindow(handle, snapshotId);
            }
        }

        var ocr = screenshotPath is null ? new JsonArray() : await RecognizeTextAsync(screenshotPath).ConfigureAwait(false);
        return new JsonObject
        {
            ["snapshot_id"] = snapshotId,
            ["process_id"] = process.ProcessId,
            ["window_count"] = windows.Length,
            ["elements_truncated"] = remaining <= 0,
            ["capture_method"] = screenshotPath is null ? "none" : "PrintWindow.PW_RENDERFULLCONTENT",
            ["ocr_available"] = OcrEngine.TryCreateFromUserProfileLanguages() is not null,
            ["ocr_lines"] = ocr,
            ["screenshot_path"] = screenshotPath,
            ["windows"] = windowNodes
        };
    }

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

    public JsonObject Invoke(JsonObject arguments)
    {
        var tokenValue = arguments["element_token"]?.GetValue<string>()
            ?? throw new ArgumentException("element_token is required.");
        var action = arguments["action"]?.GetValue<string>()?.ToLowerInvariant()
            ?? throw new ArgumentException("action is required.");
        if (!_tokens.TryRemove(tokenValue, out var token) || token.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The UI element token is unknown or expired. Inspect the window again.");
        }

        var process = processInspector.Inspect();
        if (!process.Running || process.ProcessId != token.ProcessId)
        {
            throw new InvalidOperationException("Cubase restarted after this UI token was issued.");
        }

        var element = token.Element;
        switch (action)
        {
            case "invoke":
                ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                break;
            case "toggle":
                ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
                break;
            case "select":
                ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                break;
            case "expand":
                ((ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                break;
            case "collapse":
                ((ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
                break;
            case "set_value":
                ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(
                    arguments["value"]?.GetValue<string>() ?? string.Empty);
                break;
            case "focus":
                element.SetFocus();
                break;
            case "click":
                ClickElement(element, token.ProcessId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(arguments), action, "Unsupported UI action.");
        }

        foreach (var pair in _tokens.Where(pair => pair.Value.SnapshotId == token.SnapshotId).ToArray())
        {
            _tokens.TryRemove(pair.Key, out _);
        }

        return new JsonObject
        {
            ["action"] = action,
            ["invoked"] = true,
            ["verified"] = false,
            ["next_step"] = "Call cubase_ui_inspect to verify the resulting window state."
        };
    }

    private JsonObject BuildElement(AutomationElement element, int processId, string snapshotId,
        int depth, int maxDepth, ref int remaining)
    {
        remaining--;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _tokens[token] = new UiToken(element, processId, snapshotId, DateTimeOffset.UtcNow.AddSeconds(30));
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
                children.Add(BuildElement(child, processId, snapshotId, depth + 1, maxDepth, ref remaining));
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
    }

    private sealed record UiToken(AutomationElement Element, int ProcessId, string SnapshotId, DateTimeOffset ExpiresAt);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr handle, IntPtr targetDeviceContext, uint flags);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
}
