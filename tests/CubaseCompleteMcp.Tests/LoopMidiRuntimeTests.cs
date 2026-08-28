using CubaseCompleteMcp.Broker;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class LoopMidiRuntimeTests
{
    private static readonly MidiPortSnapshot[] RequiredPorts =
    [
        new(0, CubaseConstants.OutputPortName, true),
        new(0, CubaseConstants.InputPortName, false)
    ];

    [Fact]
    public async Task ConcurrentRequestsStartLoopMidiOnlyOnce()
    {
        var environment = new FakeEnvironment();
        var runtime = CreateRuntime(environment);
        IReadOnlyList<MidiPortSnapshot> Ports() => environment.Running ? RequiredPorts : [];

        var results = await Task.WhenAll(runtime.EnsureAvailableAsync(Ports), runtime.EnsureAvailableAsync(Ports));

        Assert.Equal(1, environment.StartCount);
        Assert.All(results, result => Assert.True(result.PortsAvailable));
        Assert.Contains(results, result => result.AutostartAttempted && result.AutostartResult == "started_ports_ready");
    }

    [Fact]
    public async Task MissingExecutableIsReportedWithoutStart()
    {
        var environment = new FakeEnvironment { Exists = false };
        var result = await CreateRuntime(environment).EnsureAvailableAsync(() => []);
        Assert.Equal("loopmidi_missing", result.AutostartResult);
        Assert.False(result.Installed);
        Assert.Equal(0, environment.StartCount);
    }

    [Fact]
    public async Task InvalidProductIdentityIsRejected()
    {
        var environment = new FakeEnvironment { ProductName = "not-loopMIDI" };
        var result = await CreateRuntime(environment).EnsureAvailableAsync(() => []);
        Assert.Equal("invalid_executable", result.AutostartResult);
        Assert.Equal(0, environment.StartCount);
    }

    [Fact]
    public async Task RunningProcessWithoutPortsIsNotRelaunched()
    {
        var environment = new FakeEnvironment { Running = true };
        var result = await CreateRuntime(environment).EnsureAvailableAsync(() => []);
        Assert.Equal("process_running_ports_missing", result.AutostartResult);
        Assert.Equal(0, environment.StartCount);
    }

    [Fact]
    public async Task StartFailureIsReported()
    {
        var environment = new FakeEnvironment { StartError = new InvalidOperationException("failed") };
        var result = await CreateRuntime(environment).EnsureAvailableAsync(() => []);
        Assert.Equal("loopmidi_start_failed", result.AutostartResult);
        Assert.True(result.AutostartAttempted);
    }

    [Fact]
    public async Task PortCreationTimeoutIsReported()
    {
        var environment = new FakeEnvironment();
        var runtime = new LoopMidiRuntime(environment, TimeSpan.Zero, TimeSpan.FromMilliseconds(5),
            (_, _) => Task.Delay(1));
        var result = await runtime.EnsureAvailableAsync(() => []);
        Assert.Equal("ports_timeout", result.AutostartResult);
        Assert.False(result.PortsAvailable);
    }

    [Fact]
    public async Task EnvironmentVariableDisablesAutostart()
    {
        var previous = Environment.GetEnvironmentVariable("CUBASE_COMPLETE_MCP_AUTO_START_LOOPMIDI");
        try
        {
            Environment.SetEnvironmentVariable("CUBASE_COMPLETE_MCP_AUTO_START_LOOPMIDI", "0");
            var environment = new FakeEnvironment();
            var result = await CreateRuntime(environment).EnsureAvailableAsync(() => []);
            Assert.Equal("autostart_disabled", result.AutostartResult);
            Assert.Equal(0, environment.StartCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CUBASE_COMPLETE_MCP_AUTO_START_LOOPMIDI", previous);
        }
    }

    private static LoopMidiRuntime CreateRuntime(FakeEnvironment environment) =>
        new(environment, TimeSpan.Zero, TimeSpan.FromMilliseconds(20), (_, _) => Task.CompletedTask);

    private sealed class FakeEnvironment : ILoopMidiEnvironment
    {
        public bool Exists { get; init; } = true;
        public bool Running { get; set; }
        public string ProductName { get; init; } = "loopMIDI";
        public string CompanyName { get; init; } = "Tobias Erichsen";
        public Exception? StartError { get; init; }
        public int StartCount { get; private set; }
        public bool IsProcessRunning() => Running;
        public bool FileExists(string path) => Exists;
        public (string? ProductName, string? CompanyName) GetProductIdentity(string path) => (ProductName, CompanyName);
        public void Start(string path)
        {
            StartCount++;
            if (StartError is not null) throw StartError;
            Running = true;
        }
        public string[] GetConfiguredPorts() => [CubaseConstants.InputPortName, CubaseConstants.OutputPortName];
    }
}
