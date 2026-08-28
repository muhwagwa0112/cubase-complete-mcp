using System.Diagnostics;
using System.IO;
using CubaseCompleteMcp.Core;
using Microsoft.Win32;

namespace CubaseCompleteMcp.Broker;

public sealed record LoopMidiRuntimeSnapshot(
    bool Installed,
    bool ProcessRunning,
    bool AutostartAttempted,
    string AutostartResult,
    string[] ConfiguredPorts,
    MidiPortSnapshot[] ObservedPorts,
    bool PortsAvailable,
    bool StartedByBroker);

public interface ILoopMidiEnvironment
{
    bool IsProcessRunning();
    bool FileExists(string path);
    (string? ProductName, string? CompanyName) GetProductIdentity(string path);
    void Start(string path);
    string[] GetConfiguredPorts();
}

public sealed class WindowsLoopMidiEnvironment : ILoopMidiEnvironment
{
    public bool IsProcessRunning()
    {
        var processes = Process.GetProcessesByName("loopMIDI");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public bool FileExists(string path) => File.Exists(path);

    public (string? ProductName, string? CompanyName) GetProductIdentity(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return (info.ProductName, info.CompanyName);
    }

    public void Start(string path)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("loopMIDI did not return a process handle.");
    }

    public string[] GetConfiguredPorts()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Tobias Erichsen\loopMIDI\Ports", false);
            return key?.GetValueNames().Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

public sealed class LoopMidiRuntime
{
    public const string ExecutablePath = @"C:\Program Files (x86)\Tobias Erichsen\loopMIDI\loopMIDI.exe";
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PortWait = TimeSpan.FromSeconds(8);
    private readonly ILoopMidiEnvironment _environment;
    private readonly TimeSpan _retryCooldown;
    private readonly TimeSpan _portWait;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private DateTimeOffset _lastStartAttempt = DateTimeOffset.MinValue;
    private bool _startedByBroker;

    public LoopMidiRuntime() : this(new WindowsLoopMidiEnvironment(), RetryCooldown, PortWait) { }

    public LoopMidiRuntime(ILoopMidiEnvironment environment, TimeSpan? retryCooldown = null,
        TimeSpan? portWait = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _environment = environment;
        _retryCooldown = retryCooldown ?? RetryCooldown;
        _portWait = portWait ?? PortWait;
        _delay = delay ?? Task.Delay;
    }

    public async Task<LoopMidiRuntimeSnapshot> EnsureAvailableAsync(
        Func<IReadOnlyList<MidiPortSnapshot>> enumeratePorts,
        CancellationToken cancellationToken = default)
    {
        var initialPorts = enumeratePorts().ToArray();
        if (HasRequiredPorts(initialPorts))
            return Snapshot(initialPorts, false, "ready_existing");

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ports = enumeratePorts().ToArray();
            if (HasRequiredPorts(ports)) return Snapshot(ports, false, "ready_existing");

            var installed = _environment.FileExists(ExecutablePath);
            if (!installed) return Snapshot(ports, false, "loopmidi_missing", installed: false);
            (string? ProductName, string? CompanyName) identity;
            try { identity = _environment.GetProductIdentity(ExecutablePath); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
            {
                return Snapshot(ports, false, "invalid_executable");
            }
            if (!string.Equals(identity.ProductName, "loopMIDI", StringComparison.Ordinal)
                || !string.Equals(identity.CompanyName, "Tobias Erichsen", StringComparison.Ordinal))
                return Snapshot(ports, false, "invalid_executable");
            if (_environment.IsProcessRunning())
            {
                if (DateTimeOffset.UtcNow - _lastStartAttempt < _retryCooldown)
                    return Snapshot(ports, false, "autostart_cooldown");
                _lastStartAttempt = DateTimeOffset.UtcNow;
                ports = await WaitForPortsAsync(enumeratePorts, cancellationToken).ConfigureAwait(false);
                return Snapshot(ports, false, HasRequiredPorts(ports)
                    ? "ready_existing" : "process_running_ports_missing");
            }
            if (Environment.GetEnvironmentVariable("CUBASE_COMPLETE_MCP_AUTO_START_LOOPMIDI") == "0")
                return Snapshot(ports, false, "autostart_disabled");
            if (DateTimeOffset.UtcNow - _lastStartAttempt < _retryCooldown)
                return Snapshot(ports, false, "autostart_cooldown");

            _lastStartAttempt = DateTimeOffset.UtcNow;
            try
            {
                _environment.Start(ExecutablePath);
                _startedByBroker = true;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return Snapshot(ports, true, "loopmidi_start_failed");
            }

            ports = await WaitForPortsAsync(enumeratePorts, cancellationToken).ConfigureAwait(false);
            if (HasRequiredPorts(ports)) return Snapshot(ports, true, "started_ports_ready");
            return Snapshot(ports, true, "ports_timeout");
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<MidiPortSnapshot[]> WaitForPortsAsync(
        Func<IReadOnlyList<MidiPortSnapshot>> enumeratePorts, CancellationToken cancellationToken)
    {
        var ports = enumeratePorts().ToArray();
        var deadline = DateTimeOffset.UtcNow + _portWait;
        while (!HasRequiredPorts(ports) && DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            ports = enumeratePorts().ToArray();
        }
        return ports;
    }

    private LoopMidiRuntimeSnapshot Snapshot(MidiPortSnapshot[] ports, bool attempted, string result,
        bool? installed = null) => new(
        installed ?? _environment.FileExists(ExecutablePath),
        _environment.IsProcessRunning(),
        attempted,
        result,
        _environment.GetConfiguredPorts(),
        ports,
        HasRequiredPorts(ports),
        _startedByBroker);

    private static bool HasRequiredPorts(IEnumerable<MidiPortSnapshot> ports)
    {
        var values = ports.ToArray();
        return values.Any(port => port.IsInput
                && port.Name.Equals(CubaseConstants.OutputPortName, StringComparison.OrdinalIgnoreCase))
            && values.Any(port => !port.IsInput
                && port.Name.Equals(CubaseConstants.InputPortName, StringComparison.OrdinalIgnoreCase));
    }
}
