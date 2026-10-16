using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CubaseCompleteMcp.Tests;

/// <summary>
/// Runs Acceptance/acceptance-evidence.mjs against scripts/acceptance-evidence.mjs: a live run joins the offline run of
/// the same candidate and release, produced files become hashed raw evidence, and the merge refuses anything that would
/// need a receipt to be rewritten.
/// </summary>
public sealed class AcceptanceEvidenceTests
{
    [Fact]
    public async Task LiveRunJoinsOfflineRun_AndMergeNeverRewritesEvidence()
    {
        var node = NodeHarnessRunner.RequireNode(Environment.GetEnvironmentVariable("PATH"));
        var script = Path.Combine(Path.GetDirectoryName(SourceFile())!, "Acceptance", "acceptance-evidence.mjs");
        Assert.True(File.Exists(script), $"{script} was not found next to this test file.");

        var result = await NodeHarnessRunner.RunAsync(node, [script], Path.GetDirectoryName(script)!, TimeSpan.FromSeconds(60));
        Assert.False(result.TimedOut, "acceptance-evidence.mjs did not finish within 60 seconds.");
        var summaryLine = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'));
        Assert.True(summaryLine is not null, $"acceptance-evidence.mjs printed no JSON summary.\nstdout: {result.Stdout}\nstderr: {result.Stderr}");

        using var summary = JsonDocument.Parse(summaryLine!);
        var failures = summary.RootElement.GetProperty("failures").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.True(failures.Length == 0, $"acceptance evidence failures: {string.Join(" | ", failures)}\nstderr: {result.Stderr}");
        var groups = summary.RootElement.GetProperty("groups").EnumerateObject().Select(item => item.Name).ToArray();
        foreach (var required in new[] { "joining the offline run", "artifact evidence", "merging offline and live runs", "merge refusals" })
            Assert.Contains(required, groups);
        Assert.True(summary.RootElement.GetProperty("checks").GetInt32() >= 25);
        Assert.Equal(0, result.ExitCode);
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
