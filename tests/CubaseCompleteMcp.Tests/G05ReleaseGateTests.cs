using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CubaseCompleteMcp.Core;
using CubaseCompleteMcp.Tests.Fakes;

namespace CubaseCompleteMcp.Tests;

public sealed class G05ReleaseGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbmcp-g05-" + Guid.NewGuid().ToString("N"));

    private static string RepositoryRoot() => RepositoryPaths.Root;

    private static string CliAssembly()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        var bin = Path.Combine(RepositoryRoot(), "src", "CubaseCompleteMcp.Cli", "bin", configuration);
        return Directory.EnumerateFiles(bin, "CubaseCompleteMcp.Cli.dll", SearchOption.AllDirectories).First();
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(CliAssembly());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = Path.GetDirectoryName(Environment.ProcessPath ?? "dotnet") ?? string.Empty; // no codex on PATH
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000));
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void REL06_RegisterRequiresAnExplicitCandidate_ValidateOnlyWritesNothing()
    {
        var release = Path.Combine(_root, "rel");
        Directory.CreateDirectory(Path.Combine(release, "driver"));
        foreach (var name in new[] { "CubaseCompleteMcp.Server.dll", "CubaseCompleteMcp.Server.exe", "CubaseCompleteMcp.Broker.dll",
                     "CubaseCompleteMcp.Cli.dll", "CubaseCompleteMcp.Core.dll", "driver/CBMCP_CompleteBridge.js", "driver/command_catalog.json" })
            File.WriteAllText(Path.Combine(release, name), name);
        ReleaseManifestService.Write(release, ReleaseManifestService.Build(release, "rel", new string('a', 40), false, "Release",
            "8.0.301", "v24.11.1", "build", new string('c', 64), new string('a', 40)));

        var noCandidate = RunCli("register-codex", "--validate-only");
        Assert.NotEqual(0, noCandidate.ExitCode);
        Assert.Contains("--release", noCandidate.Stderr, StringComparison.Ordinal);

        var validated = RunCli("register-codex", "--release", release, "--validate-only");
        Assert.Equal(0, validated.ExitCode);
        var json = JsonNode.Parse(validated.Stdout)!;
        Assert.Equal("validated_only", json["action"]!.GetValue<string>());
        Assert.False(json["configuration_written"]!.GetValue<bool>());

        File.WriteAllText(Path.Combine(release, "extra.dll"), "unlisted");
        var tampered = RunCli("validate-release", release);
        Assert.NotEqual(0, tampered.ExitCode);
    }

    [Fact]
    public async Task REL07_OldLoadedDriverIsReportedAndWritesAreRefused()
    {
        using var harness = new ControllerHarness();
        harness.Driver.ContractVersion = 1;
        var status = await harness.SendAsync("status");
        Assert.Equal("driver_contract_mismatch", status.Data!["connection_state"]!.GetValue<string>());
        Assert.Equal(1, status.Data["driver_contract_version"]!.GetValue<int>());
        var write = await harness.SendAsync("transport", new JsonObject { ["action"] = "play" });
        Assert.Equal(ErrorCodes.DriverContractMismatch, write.Error!.Code);
        Assert.Equal(0, harness.Driver.WriteCount("transport.set"));
    }

    [Fact]
    public async Task REL08_CiRequiresRealTestsAndPinsActions_LedgerSeparatesSupportLevels()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"));
        Assert.Contains("runs-on: windows-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", workflow, StringComparison.Ordinal);
        Assert.Contains("Skipped tests", workflow, StringComparison.Ordinal);
        Assert.Contains("Zero tests", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error: true", workflow, StringComparison.Ordinal);
        var uses = Regex.Matches(workflow, @"uses:\s*(\S+)@(\S+)").Select(match => match.Groups[2].Value).ToArray();
        Assert.NotEmpty(uses);
        Assert.All(uses, revision => Assert.Matches("^[0-9a-f]{40}$", revision));

        using var harness = new ControllerHarness();
        var capabilities = await harness.SendAsync("capabilities.list");
        var levels = capabilities.Data!["capabilities"]!.AsArray().Select(node => node!["support_level"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("mock_validated", levels);
        Assert.Contains("best_effort", levels);
        Assert.DoesNotContain("live_validated", levels); // nothing is claimed as live-validated without live evidence
        Assert.Equal("Confirm", capabilities.Data["summary"]!["unclassified_effective_risk"]!.GetValue<string>());
    }

    [Fact]
    public void REL03_PublishScriptRejectsSkippedAndZeroTestRuns()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "publish.ps1"));
        Assert.Contains("Tests were skipped", script, StringComparison.Ordinal);
        Assert.Contains("ran zero tests", script, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", script, StringComparison.Ordinal);
        Assert.Contains("Move-Item", script, StringComparison.Ordinal); // atomic publish from a staging directory
        Assert.DoesNotContain("Get-Date -Format 'yyyyMMdd-HHmmss'\r\n$output", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task API02_UnavailableFeaturesAreReportedHonestly()
    {
        using var harness = new ControllerHarness();
        var status = await harness.SendAsync("status");
        Assert.False(status.Data!["api13_available"]!.GetValue<bool>());
        Assert.Contains(status.Data["warnings"]!.AsArray(), warning => warning!.GetValue<string>().Contains("API 1.3", StringComparison.Ordinal));
        var catalog = await harness.SendAsync("plugin", new JsonObject { ["action"] = "catalog" });
        Assert.Equal(ResultStatus.Unsupported, catalog.Status);
        Assert.EndsWith(".v2", CubaseConstants.BrokerMutexName, StringComparison.Ordinal);
        Assert.EndsWith(".v2", CubaseConstants.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void REL07_InstallationCheckRequiresBothFileHashAndLoadedBuildId()
    {
        var receipt = new InstallationReceipt(1, new string('a', 40), new string('b', 64), DateTimeOffset.UtcNow, new string('c', 64),
            new string('d', 64), new string('e', 64), "build-new", null, "redacted", "backup");
        Assert.True(InstallationCheck.Evaluate(receipt, new string('d', 64), "build-new", 2).Ok);
        var oldLoaded = InstallationCheck.Evaluate(receipt, new string('d', 64), "build-old", 2);
        Assert.True(oldLoaded.DriverFileMatches);
        Assert.False(oldLoaded.Ok); // file installed but Cubase still runs the previous driver
        Assert.False(InstallationCheck.Evaluate(receipt, new string('9', 64), "build-new", 2).Ok); // file replaced
        Assert.False(InstallationCheck.Evaluate(receipt, new string('d', 64), null, null).Ok); // protocol-1 driver without build ID
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
