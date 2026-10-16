using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed record NodeRunResult(int? ExitCode, string Stdout, string Stderr, bool TimedOut, TimeSpan Elapsed);

/// <summary>
/// Runs a Node.js script as a child process. stdout and stderr are drained concurrently, the whole run (start,
/// output and exit) is bounded by one timeout, and on timeout only the process tree started here is killed.
/// </summary>
public static class NodeHarnessRunner
{
    public static string? FindNode(string? pathVariable)
    {
        foreach (var directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "node.exe", "node" })
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>Node is a required test dependency. Its absence is a failure with an explicit reason, never a pass.</summary>
    public static string RequireNode(string? pathVariable) => FindNode(pathVariable)
        ?? throw new InvalidOperationException(
            "Node.js was not found on PATH. The generated-driver contract harness is a required test; install Node.js (the version recorded in the CI workflow) and re-run. It is never skipped silently.");

    public static async Task<NodeRunResult> RunAsync(string node, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Node.js could not be started.");
        using var cancellation = new CancellationTokenSource(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var stderr = process.StandardError.ReadToEndAsync(cancellation.Token);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return new NodeRunResult(process.ExitCode, await stdout, await stderr, false, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            return new NodeRunResult(null, string.Empty, string.Empty, true, clock.Elapsed);
        }
    }
}

/// <summary>
/// Executes the real generated ES5 MIDI Remote driver inside a Node vm with a mock host (test layer B). A pass proves
/// the driver's own contract logic; it is not evidence that the installed Cubase API behaves identically.
/// </summary>
public sealed class DriverContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbmcp-driver-contract-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GeneratedDriver_SatisfiesMockHostContracts()
    {
        var node = NodeHarnessRunner.RequireNode(Environment.GetEnvironmentVariable("PATH"));
        var harness = ResolveHarness();
        Assert.True(harness is not null, "driver-contract.mjs was not found next to this test file.");

        var catalog = KeyCommandCatalog.Load(WriteXml(
            "<item><string name=\"Name\" value=\"Transport\"/><list name=\"Commands\"><item><string name=\"Name\" value=\"Start\"/></item></list></item>"));
        var generated = new MidiRemoteDriverGenerator().Generate(catalog, Path.Combine(_root, "out"), "test-source-sha");
        Assert.Equal(MidiRemoteDriverGenerator.ComputeDriverBuildId("test-source-sha", generated.CatalogInputSha256), generated.DriverBuildId);
        Assert.Contains(generated.DriverBuildId, File.ReadAllText(generated.DriverPath), StringComparison.Ordinal);

        var result = await NodeHarnessRunner.RunAsync(node, [harness!, generated.DriverPath], Path.GetDirectoryName(harness!)!, TimeSpan.FromSeconds(120));
        Assert.False(result.TimedOut, "driver-contract.mjs did not finish within 120 seconds.");
        var summaryLine = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'));
        Assert.True(summaryLine is not null, $"driver-contract.mjs printed no JSON summary.\nstdout: {result.Stdout}\nstderr: {result.Stderr}");

        using var summary = JsonDocument.Parse(summaryLine!);
        var checks = summary.RootElement.GetProperty("checks").GetInt32();
        var failures = summary.RootElement.GetProperty("failures").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.True(failures.Length == 0, $"driver contract failures: {string.Join(" | ", failures)}\nstderr: {result.Stderr}");
        Assert.True(checks >= 100, $"expected the harness to run its full scenario set, ran {checks} checks.");
        var groups = summary.RootElement.GetProperty("groups").EnumerateObject().Select(item => item.Name).ToArray();
        foreach (var required in new[] { "SES-08 activation resets the mirror", "SES-05/06 stale session and request cache", "TGT-01 target-addressed mixer read and write",
                     "TGT-03 reorder and replacement", "TGT-07 parameter resolution", "TGT-02 selection guard in the command callback", "VER-05 record target states" })
            Assert.Contains(required, groups);
        Assert.Equal(0, result.ExitCode);
        File.WriteAllText(Path.Combine(_root, "summary.json"), summaryLine);
    }

    [Fact]
    public void REL02_MissingNode_FailsWithAnExplicitReason()
    {
        var empty = Path.Combine(_root, "empty-path");
        Directory.CreateDirectory(empty);
        var error = Assert.Throws<InvalidOperationException>(() => NodeHarnessRunner.RequireNode(empty));
        Assert.Contains("never skipped silently", error.Message, StringComparison.Ordinal);
        Assert.Null(NodeHarnessRunner.FindNode(string.Empty));
    }

    [Fact]
    public async Task REL03_HangingHarness_TimesOutAndOnlyItsOwnChildIsKilled()
    {
        var node = NodeHarnessRunner.RequireNode(Environment.GetEnvironmentVariable("PATH"));
        Directory.CreateDirectory(_root);
        var script = Path.Combine(_root, "hang.mjs");
        // Floods stdout and stderr, then never exits: a sequential ReadToEnd would deadlock before the timeout.
        File.WriteAllText(script, "const big='x'.repeat(200000); process.stdout.write(big); process.stderr.write(big); setInterval(()=>{}, 1000);");
        var unrelated = Process.Start(new ProcessStartInfo(node) { ArgumentList = { "-e", "setTimeout(()=>{}, 20000)" }, UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var result = await NodeHarnessRunner.RunAsync(node, [script], _root, TimeSpan.FromSeconds(2));
            Assert.True(result.TimedOut);
            Assert.True(result.Elapsed < TimeSpan.FromSeconds(15), $"timeout took {result.Elapsed}");
            Assert.False(unrelated.HasExited, "a process the test did not start must not be killed");
        }
        finally
        {
            try { unrelated.Kill(); } catch (InvalidOperationException) { }
            unrelated.Dispose();
        }
    }

    private static string? ResolveHarness()
    {
        var beside = Path.Combine(Path.GetDirectoryName(SourceFile())!, "DriverContract", "driver-contract.mjs");
        if (File.Exists(beside)) return beside;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "CubaseCompleteMcp.Tests", "DriverContract", "driver-contract.mjs");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;

    private string WriteXml(string categories)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, $"<preferences><list name=\"Categories\">{categories}</list></preferences>");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
