// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CubaseCompleteMcp.Core;

var paths = CubasePaths.Discover(FindRepositoryRoot());
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
try
{
    switch (command)
    {
        case "generate":
            Print(Generate(args.Skip(1).FirstOrDefault() ?? Path.Combine(paths.RepositoryRoot, "artifacts", "driver")));
            break;
        case "backup":
            Print(new BackupService(paths).Create(args.Skip(1).FirstOrDefault() ?? "manual"));
            break;
        case "restore":
            RequireArgument(args, 1, "restore <backup-directory> --confirm");
            new BackupService(paths).Restore(args[1], args.Contains("--confirm", StringComparer.OrdinalIgnoreCase));
            Print(new { restored = Path.GetFullPath(args[1]) });
            break;
        case "setup":
            Print(Setup(args));
            break;
        case "doctor":
            Print(await DoctorAsync());
            break;
        case "cleanup-sessions":
            Print(CleanupSessions(args));
            break;
        case "register-codex":
            Print(RegisterCodex(false, args));
            break;
        case "unregister-codex":
            Print(RegisterCodex(true, args));
            break;
        case "help":
        case "--help":
        case "-h":
            Console.WriteLine("Cubase Complete MCP CLI\n\n  generate [directory]\n  backup [label]\n  restore <backup-directory> --confirm\n  setup [--deactivate-legacy]\n  doctor\n  cleanup-sessions --older-than-days <N> [--confirmation-id <ID>]\n  register-codex [--allow-debug]\n  unregister-codex");
            break;
        default:
            throw new ArgumentException($"Unknown command '{command}'. Run with --help.");
    }
}
catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException
    or ArgumentException or JsonException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "error", message = error.Message }, JsonOptions()));
    Environment.ExitCode = 1;
}

DriverGenerationResult Generate(string output)
{
    var catalog = KeyCommandCatalog.Load(paths.KeyCommandsFile);
    return new MidiRemoteDriverGenerator().Generate(catalog, Path.GetFullPath(output));
}

object Setup(string[] input)
{
    EnsureCubaseClosed("setup");
    var backup = new BackupService(paths).Create("pre-setup");
    var staging = Path.Combine(paths.LocalStateRoot, "staging", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
    var generated = Generate(staging);
    try
    {
        new BackupService(paths).InstallActiveDriver(staging);
        string? disabledLegacy = null;
        if (input.Contains("--deactivate-legacy", StringComparer.OrdinalIgnoreCase))
        {
            var legacy = Path.Combine(paths.LocalDriverRoot, "FIGStudio");
            if (Directory.Exists(legacy))
            {
                disabledLegacy = Path.Combine(paths.LocalStateRoot, "disabled", $"FIGStudio-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
                Directory.CreateDirectory(Path.GetDirectoryName(disabledLegacy)!);
                Directory.Move(legacy, disabledLegacy);
            }
        }
        return new { installed = paths.ActiveDriverDirectory, backup, generated, disabled_legacy = disabledLegacy };
    }
    catch
    {
        new BackupService(paths).Restore(backup.Root, true);
        throw;
    }
}

async Task<object> DoctorAsync()
{
    BrokerResponse? brokerStatus = null;
    string? brokerError = null;
    try
    {
        await using var client = new BrokerClient();
        brokerStatus = await client.SendAsync(new BrokerRequest(Guid.NewGuid().ToString("N"), "diagnostics"));
    }
    catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException)
    {
        brokerError = error.Message;
    }
    var process = Process.GetProcessesByName("Cubase15").FirstOrDefault();
    using (process)
    {
        var catalog = File.Exists(paths.KeyCommandsFile) ? KeyCommandCatalog.Load(paths.KeyCommandsFile) : null;
        ValidatedRelease? release = null;
        string? releaseError = null;
        try { release = ReleaseManifestService.FindLatestValid(Path.Combine(paths.RepositoryRoot, "artifacts", "publish")); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { releaseError = error.Message; }
        var registeredCommand = GetRegisteredCodexCommand();
        var activeDriverScript = Path.Combine(paths.ActiveDriverDirectory, "CBMCP_CompleteBridge.js");
        var generatedDriverScript = Path.Combine(paths.RepositoryRoot, "artifacts", "driver", "CBMCP_CompleteBridge.js");
        var activeDriverHash = HashFile(activeDriverScript);
        var generatedDriverHash = HashFile(generatedDriverScript);
        var serverProcesses = RuntimeProcesses("CubaseCompleteMcp.Server", release?.Manifest.ReleaseId);
        var brokerProcesses = RuntimeProcesses("CubaseCompleteMcp.Broker", release?.Manifest.ReleaseId);
        var clientIdentity = BuildIdentityService.Current();
        return new
        {
            product = CubaseConstants.ProductName,
            cubase_running = process is not null,
            cubase_version = process?.MainModule?.FileVersionInfo.ProductVersion,
            key_commands = catalog?.Commands.Count ?? 0,
            key_command_categories = catalog?.CategoryCount ?? 0,
            active_driver = Directory.Exists(paths.ActiveDriverDirectory),
            active_driver_sha256 = activeDriverHash,
            generated_driver_sha256 = generatedDriverHash,
            active_driver_matches_generated = activeDriverHash is not null && activeDriverHash == generatedDriverHash,
            legacy_driver = Directory.Exists(Path.Combine(paths.LocalDriverRoot, "FIGStudio")),
            selected_release_id = release?.Manifest.ReleaseId,
            selected_release_git_sha = release?.Manifest.GitSha,
            selected_release_source_dirty = release?.Manifest.SourceDirty,
            selected_release_server = release?.ServerPath,
            release_error = releaseError,
            codex_registered_server = registeredCommand,
            codex_registration_matches_release = release is not null && registeredCommand is not null
                && Path.GetFullPath(registeredCommand).Equals(Path.GetFullPath(release.ServerPath), StringComparison.OrdinalIgnoreCase),
            requesting_client_release_id = clientIdentity.ReleaseId,
            active_broker_release_id = brokerStatus?.Data?["broker_release_id"]?.GetValue<string>(),
            active_broker_path = brokerStatus?.Data?["broker_executable_path"]?.GetValue<string>(),
            running_server_processes = serverProcesses,
            running_broker_processes = brokerProcesses,
            stale_server_processes = serverProcesses.Where(item => item.IsStale).ToArray(),
            catalog_matches_expected = catalog?.Commands.Count == 2777 && catalog.CategoryCount == 69,
            paths,
            broker_status = brokerStatus,
            broker_error = brokerError
        };
    }
}

object CleanupSessions(string[] input)
{
    var daysText = OptionValue(input, "--older-than-days")
        ?? throw new ArgumentException("Usage: cleanup-sessions --older-than-days <N> [--confirmation-id <ID>]");
    if (!int.TryParse(daysText, out var days)) throw new ArgumentException("--older-than-days must be an integer.");
    var confirmationId = OptionValue(input, "--confirmation-id");
    var service = new SessionCleanupService(paths.LocalStateRoot);
    if (confirmationId is null)
    {
        var proposal = service.Preview(days);
        return new
        {
            status = "requires_confirmation",
            proposal.ConfirmationId,
            proposal.Cutoff,
            proposal.ExpiresAt,
            session_count = proposal.Candidates.Length,
            file_count = proposal.Candidates.Sum(candidate => candidate.FileCount),
            total_bytes = proposal.Candidates.Sum(candidate => candidate.Bytes),
            proposal.Candidates,
            warning = "Only session-scoped composition documents and their renders are permanently deleted. Re-run with the same older-than-days and confirmation-id within five minutes."
        };
    }
    var result = service.Commit(days, confirmationId);
    return new { status = "deleted", result.ConfirmationId, result.DeletedSessions, result.DeletedFiles, result.DeletedBytes };
}

object RegisterCodex(bool remove, string[] input)
{
    var codex = FindExecutable("codex.cmd") ?? FindExecutable("codex.exe")
        ?? throw new FileNotFoundException("codex command was not found on PATH.");
    if (remove)
    {
        var removed = RunCodex(codex, ["mcp", "remove", CubaseConstants.ServerName], allowFailure: true);
        return new { action = "unregistered", name = CubaseConstants.ServerName, output = removed.Output.Trim() };
    }

    string server;
    ValidatedRelease? release = null;
    try
    {
        release = ReleaseManifestService.FindLatestValid(Path.Combine(paths.RepositoryRoot, "artifacts", "publish"));
        server = release.ServerPath;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
    {
        if (!input.Contains("--allow-debug", StringComparer.OrdinalIgnoreCase)) throw;
        server = Path.Combine(paths.RepositoryRoot, "src", "CubaseCompleteMcp.Server", "bin", "Debug",
            "net8.0-windows10.0.19041.0", "CubaseCompleteMcp.Server.dll");
        if (!File.Exists(server)) throw new FileNotFoundException("No valid Release or explicit Debug server was found.", server, error);
    }

    _ = RunCodex(codex, ["mcp", "remove", CubaseConstants.ServerName], allowFailure: true);
    var arguments = server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        ? new[] { "mcp", "add", CubaseConstants.ServerName, "--", "dotnet", server }
        : new[] { "mcp", "add", CubaseConstants.ServerName, "--", server };
    var added = RunCodex(codex, arguments);
    return new
    {
        action = "registered", name = CubaseConstants.ServerName, server,
        release_id = release?.Manifest.ReleaseId, git_sha = release?.Manifest.GitSha,
        activation_pending_new_task = true,
        stale_server_processes = RuntimeProcesses("CubaseCompleteMcp.Server", release?.Manifest.ReleaseId)
            .Where(item => item.IsStale).ToArray(),
        output = added.Output.Trim()
    };
}

(int ExitCode, string Output, string Error) RunCodex(string executable, string[] arguments, bool allowFailure = false)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Codex CLI.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (!allowFailure && process.ExitCode != 0) throw new InvalidOperationException($"Codex command failed: {stderr.Trim()}");
    return (process.ExitCode, stdout, stderr);
}

string? GetRegisteredCodexCommand()
{
    var codex = FindExecutable("codex.cmd") ?? FindExecutable("codex.exe");
    if (codex is null) return null;
    var result = RunCodex(codex, ["mcp", "get", CubaseConstants.ServerName], allowFailure: true);
    if (result.ExitCode != 0) return null;
    return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("command:", StringComparison.OrdinalIgnoreCase))
        ?["command:".Length..].Trim();
}

static string? HashFile(string path) => File.Exists(path)
    ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : null;

static RuntimeProcessInfo[] RuntimeProcesses(string processName, string? currentReleaseId)
{
    var output = new List<RuntimeProcessInfo>();
    foreach (var process in Process.GetProcessesByName(processName))
    {
        using (process)
        {
            string? executable = null;
            try { executable = process.MainModule?.FileName; }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            var releaseId = executable is null ? null : Path.GetFileName(Path.GetDirectoryName(executable));
            var stale = !string.IsNullOrWhiteSpace(currentReleaseId)
                && !string.Equals(releaseId, currentReleaseId, StringComparison.Ordinal);
            output.Add(new RuntimeProcessInfo(process.Id, executable, releaseId, stale));
        }
    }
    return output.ToArray();
}

static string? OptionValue(string[] values, string name)
{
    var index = Array.FindIndex(values, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

void EnsureCubaseClosed(string operation)
{
    if (Process.GetProcessesByName("Cubase15").Length > 0)
        throw new InvalidOperationException($"Close Cubase before {operation}; its preferences and MIDI Remote scripts are preserved until then.");
}

static string? FindExecutable(string name)
{
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        var candidate = Path.Combine(directory.Trim('"'), name);
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(Environment.CurrentDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "CubaseCompleteMcp.sln"))) return current.FullName;
        current = current.Parent;
    }
    return Environment.CurrentDirectory;
}

static void RequireArgument(string[] values, int index, string usage)
{
    if (values.Length <= index) throw new ArgumentException($"Usage: {usage}");
}

static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true
};

static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions()));

sealed record RuntimeProcessInfo(int ProcessId, string? ExecutablePath, string? ReleaseId, bool IsStale);
