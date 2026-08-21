// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
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
        case "register-codex":
            Print(RegisterCodex(false));
            break;
        case "unregister-codex":
            Print(RegisterCodex(true));
            break;
        case "help":
        case "--help":
        case "-h":
            Console.WriteLine("Cubase Complete MCP CLI\n\n  generate [directory]\n  backup [label]\n  restore <backup-directory> --confirm\n  setup [--deactivate-legacy]\n  doctor\n  register-codex\n  unregister-codex");
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
        return new
        {
            product = CubaseConstants.ProductName,
            cubase_running = process is not null,
            cubase_version = process?.MainModule?.FileVersionInfo.ProductVersion,
            key_commands = catalog?.Commands.Count ?? 0,
            key_command_categories = catalog?.CategoryCount ?? 0,
            active_driver = Directory.Exists(paths.ActiveDriverDirectory),
            legacy_driver = Directory.Exists(Path.Combine(paths.LocalDriverRoot, "FIGStudio")),
            paths,
            broker_status = brokerStatus,
            broker_error = brokerError
        };
    }
}

object RegisterCodex(bool remove)
{
    var codex = FindExecutable("codex.cmd") ?? FindExecutable("codex.exe")
        ?? throw new FileNotFoundException("codex command was not found on PATH.");
    var start = new ProcessStartInfo(codex) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("mcp");
    if (remove)
    {
        start.ArgumentList.Add("remove");
        start.ArgumentList.Add(CubaseConstants.ServerName);
    }
    else
    {
        var publishRoot = Path.Combine(paths.RepositoryRoot, "artifacts", "publish");
        var publishedExe = Directory.Exists(publishRoot)
            ? Directory.EnumerateDirectories(publishRoot)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(directory => Path.Combine(directory, "CubaseCompleteMcp.Server.exe"))
                .FirstOrDefault(File.Exists)
            : null;
        var debugDll = Path.Combine(paths.RepositoryRoot, "src", "CubaseCompleteMcp.Server", "bin", "Debug",
            "net8.0-windows10.0.19041.0", "CubaseCompleteMcp.Server.dll");
        var server = publishedExe is not null && File.Exists(publishedExe) ? publishedExe : debugDll;
        if (!File.Exists(server)) throw new FileNotFoundException("Build or publish the MCP server before registration.", server);
        start.ArgumentList.Add("add");
        start.ArgumentList.Add(CubaseConstants.ServerName);
        start.ArgumentList.Add("--");
        if (server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("dotnet");
            start.ArgumentList.Add(server);
        }
        else start.ArgumentList.Add(server);
    }
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Codex CLI.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"Codex registration failed: {stderr.Trim()}");
    return new { action = remove ? "unregistered" : "registered", name = CubaseConstants.ServerName, output = stdout.Trim() };
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
