using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CubaseCompleteMcp.Broker;

public sealed record CubaseProcessState(bool Running, int? ProcessId, string? Version, string? WindowTitle, IntPtr WindowHandle);

public sealed class CubaseProcessInspector
{
    public CubaseProcessState Inspect()
    {
        var process = Process.GetProcessesByName("Cubase15")
            .OrderByDescending(static item => item.StartTime)
            .FirstOrDefault();
        if (process is null)
        {
            return new CubaseProcessState(false, null, null, null, IntPtr.Zero);
        }

        using (process)
        {
            var version = process.MainModule?.FileVersionInfo.ProductVersion
                ?? process.MainModule?.FileVersionInfo.FileVersion;
            var window = FindTopLevelWindow(process.Id);
            return new CubaseProcessState(true, process.Id, version, window.Title, window.Handle);
        }
    }

    private static (IntPtr Handle, string? Title) FindTopLevelWindow(int processId)
    {
        var candidates = new List<(IntPtr Handle, string Title)>();
        _ = EnumWindows((handle, parameter) =>
        {
            _ = GetWindowThreadProcessId(handle, out var owner);
            if (owner != processId || !IsWindowVisible(handle))
            {
                return true;
            }

            var length = GetWindowTextLength(handle);
            var builder = new StringBuilder(Math.Max(length + 1, 256));
            _ = GetWindowText(handle, builder, builder.Capacity);
            candidates.Add((handle, builder.ToString()));
            return true;
        }, IntPtr.Zero);

        var preferred = candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Title));
        return preferred == default ? (IntPtr.Zero, null) : preferred;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);
}
