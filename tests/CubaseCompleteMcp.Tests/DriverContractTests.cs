using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

/// <summary>
/// Executes the real generated ES5 MIDI Remote driver inside a Node vm with a mock host and asserts the
/// driver-side contracts that a text search cannot prove (plug-in slot assignment, transport record target
/// state, mixer target resolution and snapshot truncation accounting).
/// </summary>
public sealed class DriverContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbmcp-driver-contract-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GeneratedDriver_SatisfiesMockHostContracts()
    {
        var node = ResolveNode();
        if (node is null) return; // no Node.js on this machine; the harness cannot run here.

        var harness = ResolveHarness();
        Assert.True(harness is not null, "driver-contract.mjs was not found next to this test file.");

        var catalog = KeyCommandCatalog.Load(WriteXml(
            "<item><string name=\"Name\" value=\"Transport\"/><list name=\"Commands\"><item><string name=\"Name\" value=\"Start\"/></item></list></item>"));
        var generated = new MidiRemoteDriverGenerator().Generate(catalog, Path.Combine(_root, "out"));

        var start = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(harness!)!
        };
        start.ArgumentList.Add(harness!);
        start.ArgumentList.Add(generated.DriverPath);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        var exited = process.WaitForExit(120_000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("driver-contract.mjs did not finish within 120 seconds.");
        }

        var summaryLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'));
        Assert.True(summaryLine is not null, $"driver-contract.mjs printed no JSON summary.\nstdout: {stdout}\nstderr: {stderr}");

        using var summary = JsonDocument.Parse(summaryLine!);
        var checks = summary.RootElement.GetProperty("checks").GetInt32();
        var failures = summary.RootElement.GetProperty("failures")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.True(failures.Length == 0, $"driver contract failures: {string.Join(" | ", failures)}\nstderr: {stderr}");
        Assert.True(checks >= 20, $"expected the harness to run its full scenario set, ran {checks} checks.");
        Assert.Equal(0, process.ExitCode);
    }

    private static string? ResolveNode()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "node.exe", "node" })
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
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
