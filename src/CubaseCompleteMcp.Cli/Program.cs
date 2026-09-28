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
            Print(Generate(args.Skip(1).FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal))
                    ?? Path.Combine(paths.RepositoryRoot, "artifacts", "driver"),
                OptionValue(args, "--catalog"), OptionValue(args, "--source-sha")));
            break;
        case "release-manifest":
            Print(WriteReleaseManifest(args));
            break;
        case "validate-release":
            RequireArgument(args, 1, "validate-release <release-directory> [--manifest-sha256 <hash>] [--allow-dirty]");
            Print(DescribeRelease(ReleaseManifestService.Validate(args[1], new ReleaseValidationOptions(
                args.Contains("--allow-dirty", StringComparer.OrdinalIgnoreCase), OptionValue(args, "--manifest-sha256")))));
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
            Console.WriteLine("Cubase Complete MCP CLI\n\n  generate [directory] [--catalog <Key Commands.xml>] [--source-sha <sha>]\n  release-manifest <payload-dir> --release-id <id> --candidate-sha <sha> --source-dirty <true|false> --dotnet-sdk <v> --node-version <v> --catalog <xml>\n  validate-release <release-dir> [--manifest-sha256 <hash>] [--allow-dirty]\n  backup [label]\n  restore <backup-directory> --confirm\n  setup [--deactivate-legacy]\n  doctor [--release <release-dir>]\n  cleanup-sessions --older-than-days <N> [--confirmation-id <ID>]\n  register-codex --release <release-dir> [--manifest-sha256 <hash>] [--validate-only] | --debug-server <dll>\n  unregister-codex");
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

DriverGenerationResult Generate(string output, string? catalogPath = null, string? sourceSha = null)
{
    var catalog = KeyCommandCatalog.Load(catalogPath ?? paths.KeyCommandsFile);
    var identity = BuildIdentityService.Current();
    return new MidiRemoteDriverGenerator().Generate(catalog, Path.GetFullPath(output),
        sourceSha ?? identity.GitSha ?? "unversioned");
}

object WriteReleaseManifest(string[] input)
{
    RequireArgument(input, 1, "release-manifest <payload-dir> --release-id <id> --candidate-sha <sha> --source-dirty <true|false> --dotnet-sdk <v> --node-version <v> --catalog <xml>");
    string Required(string name) => OptionValue(input, name) ?? throw new ArgumentException($"{name} is required.");
    var payload = Path.GetFullPath(input[1]);
    var candidate = Required("--candidate-sha");
    // The source release carries a driver generated from the repository reference catalog, so its build ID is
    // reproducible; a user's local catalog is only used at installation time (see the installation receipt).
    var generated = Generate(Path.Combine(payload, "driver"), Required("--catalog"), candidate);
    var manifest = ReleaseManifestService.Build(payload, Required("--release-id"), candidate,
        bool.Parse(Required("--source-dirty")), "Release", Required("--dotnet-sdk"), Required("--node-version"),
        generated.DriverBuildId, generated.CatalogInputSha256, generated.GeneratorSourceSha);
    var path = ReleaseManifestService.Write(payload, manifest);
    return new
    {
        manifest = path, manifest_sha256 = ReleaseManifestService.HashFile(path), manifest.ReleaseId, manifest.CandidateGitSha,
        manifest.SourceDirty, file_count = manifest.Files.Count, manifest.DriverBuildId, manifest.CatalogInputSha256
    };
}

object DescribeRelease(ValidatedRelease release) => new
{
    status = "valid", release.Root, release.ManifestSha256, release.ServerPath, release.Manifest.ReleaseId,
    release.Manifest.CandidateGitSha, release.Manifest.SourceDirty, file_count = release.Manifest.Files.Count,
    release.Manifest.DriverBuildId, release.Manifest.PipeProtocolVersion, release.Manifest.DriverContractVersion
};

object Setup(string[] input)
{
    EnsureCubaseClosed("setup");
    var backup = new BackupService(paths).Create("pre-setup");
    var staging = Path.Combine(paths.LocalStateRoot, "staging", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
    var generated = Generate(staging);
    try
    {
        new BackupService(paths).InstallActiveDriver(staging);
        var receipt = WriteInstallationReceipt(generated, backup.Root);
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
        return new { installed = paths.ActiveDriverDirectory, backup, generated, installation_receipt = receipt, disabled_legacy = disabledLegacy,
            next_step = "Restart Cubase so MIDI Remote loads the new driver, then run doctor to compare the loaded driver build ID with this receipt." };
    }
    catch
    {
        new BackupService(paths).Restore(backup.Root, true);
        throw;
    }
}

string WriteInstallationReceipt(DriverGenerationResult generated, string backupRoot)
{
    var identity = BuildIdentityService.Current();
    var manifestPath = Path.Combine(AppContext.BaseDirectory, ReleaseManifestService.ManifestFileName);
    var installedDriver = Path.Combine(paths.ActiveDriverDirectory, MidiRemoteDriverGenerator.DriverFileName);
    var installedCatalog = Path.Combine(paths.ActiveDriverDirectory, MidiRemoteDriverGenerator.CatalogFileName);
    var receipt = new InstallationReceipt(1, identity.GitSha ?? "unversioned",
        File.Exists(manifestPath) ? ReleaseManifestService.HashFile(manifestPath) : "development-build",
        DateTimeOffset.UtcNow, generated.CatalogInputSha256, HashFile(installedDriver) ?? string.Empty,
        HashFile(installedCatalog) ?? string.Empty, generated.DriverBuildId, null,
        "%USERPROFILE%/Documents/Steinberg/Cubase/MIDI Remote/Driver Scripts/Local/CBMCP/CompleteBridge",
        Path.GetFileName(backupRoot));
    var directory = Path.Combine(paths.LocalStateRoot, "installation-receipts");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{generated.DriverBuildId[..12]}.json");
    File.WriteAllText(path, JsonSerializer.Serialize(receipt, JsonOptions()));
    return path;
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
        var releaseOption = OptionValue(args, "--release");
        if (releaseOption is null) releaseError = "No --release <directory> was given; releases are never auto-selected by timestamp.";
        else
        {
            try { release = ReleaseManifestService.Validate(releaseOption); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { releaseError = error.Message; }
        }
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
            selected_release_manifest_sha256 = release?.ManifestSha256,
            requesting_client_release_id = clientIdentity.ReleaseId,
            active_broker_release_id = brokerStatus?.Data?["broker_release_id"]?.GetValue<string>(),
            active_broker_path = brokerStatus?.Data?["broker_executable_path"]?.GetValue<string>(),
            running_server_processes = serverProcesses,
            running_broker_processes = brokerProcesses,
            stale_server_processes = serverProcesses.Where(item => item.IsStale).ToArray(),
            latest_installation_receipt = LatestInstallationReceipt(),
            loaded_driver_build_id = brokerStatus?.Data?["status"]?["data"]?["driver_build_id"]?.GetValue<string>(),
            loaded_driver_contract_version = brokerStatus?.Data?["status"]?["data"]?["driver_contract_version"]?.ToJsonString(),
            paths,
            broker_status = brokerStatus,
            broker_error = brokerError
        };
    }
}

object? LatestInstallationReceipt()
{
    var directory = Path.Combine(paths.LocalStateRoot, "installation-receipts");
    if (!Directory.Exists(directory)) return null;
    var latest = Directory.EnumerateFiles(directory, "*.json").OrderByDescending(path => path, StringComparer.Ordinal).FirstOrDefault();
    return latest is null ? null : JsonDocument.Parse(File.ReadAllText(latest)).RootElement.Clone();
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
    if (!remove && input.Contains("--validate-only", StringComparer.OrdinalIgnoreCase))
    {
        // Validation only: no Codex call and no configuration write.
        var releaseRoot = OptionValue(input, "--release") ?? throw new ArgumentException("--release <release-directory> is required.");
        var validated = ReleaseManifestService.Validate(releaseRoot, new ReleaseValidationOptions(false, OptionValue(input, "--manifest-sha256")));
        return new { action = "validated_only", configuration_written = false, release = DescribeRelease(validated) };
    }
    var codex = FindExecutable("codex.cmd") ?? FindExecutable("codex.exe")
        ?? throw new FileNotFoundException("codex command was not found on PATH.");
    var previous = RunCodex(codex, ["mcp", "get", CubaseConstants.ServerName], allowFailure: true);
    var backupDirectory = Path.Combine(paths.LocalStateRoot, "registration-backups");
    Directory.CreateDirectory(backupDirectory);
    var backupPath = Path.Combine(backupDirectory, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{CubaseConstants.ServerName}.txt");
    File.WriteAllText(backupPath, previous.ExitCode == 0 ? previous.Output : "(no previous registration)");
    if (remove)
    {
        var removed = RunCodex(codex, ["mcp", "remove", CubaseConstants.ServerName], allowFailure: true);
        return new { action = "unregistered", name = CubaseConstants.ServerName, previous_registration_backup = backupPath, output = removed.Output.Trim() };
    }

    string server;
    ValidatedRelease? release = null;
    if (OptionValue(input, "--debug-server") is { } debugServer)
    {
        // Development registration: explicit build output only, reported as such.
        server = Path.GetFullPath(debugServer);
        if (!File.Exists(server)) throw new FileNotFoundException("The --debug-server file does not exist.", server);
    }
    else
    {
        var releaseRoot = OptionValue(input, "--release") ?? throw new ArgumentException(
            "--release <release-directory> is required; the newest timestamp is never selected automatically.");
        release = ReleaseManifestService.Validate(releaseRoot, new ReleaseValidationOptions(false, OptionValue(input, "--manifest-sha256")));
        server = release.ServerPath;
    }

    _ = RunCodex(codex, ["mcp", "remove", CubaseConstants.ServerName], allowFailure: true);
    var arguments = server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        ? new[] { "mcp", "add", CubaseConstants.ServerName, "--", "dotnet", server }
        : new[] { "mcp", "add", CubaseConstants.ServerName, "--", server };
    var added = RunCodex(codex, arguments);
    return new
    {
        action = release is null ? "registered_debug_build" : "registered", name = CubaseConstants.ServerName, server,
        release_id = release?.Manifest.ReleaseId, git_sha = release?.Manifest.CandidateGitSha, manifest_sha256 = release?.ManifestSha256,
        previous_registration_backup = backupPath, activation_pending_new_task = true,
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
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(60_000))
    {
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException("The Codex CLI did not finish within 60 seconds.");
    }
    var stdout = stdoutTask.GetAwaiter().GetResult();
    var stderr = stderrTask.GetAwaiter().GetResult();
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
