using System.Security.Cryptography;
using System.Text.Json;

namespace CubaseCompleteMcp.Core;

public sealed record BackupResult(string Root, int FileCount, long TotalBytes, string ManifestPath);

public sealed class BackupService
{
    private readonly CubasePaths paths;
    private readonly Func<bool> _isCubaseRunning;

    public BackupService(CubasePaths paths, Func<bool>? isCubaseRunning = null)
    {
        this.paths = paths;
        _isCubaseRunning = isCubaseRunning ?? (() => System.Diagnostics.Process.GetProcessesByName("Cubase15").Length > 0);
    }

    public BackupResult Create(string label = "manual")
    {
        var safeLabel = new string(label.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeLabel))
        {
            safeLabel = "manual";
        }

        var root = Path.Combine(paths.BackupsRoot, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{safeLabel}");
        Directory.CreateDirectory(root);
        var legacyRoot = Path.GetFullPath(Path.Combine(paths.LocalDriverRoot, "FIGStudio"));
        var components = new
        {
            cubase_preferences = Directory.Exists(paths.CubasePreferences),
            complete_driver = Directory.Exists(paths.ActiveDriverDirectory),
            legacy_driver = Directory.Exists(legacyRoot),
            user_settings = Directory.Exists(paths.UserSettingsRoot)
        };
        CopyDirectory(paths.CubasePreferences, Path.Combine(root, "Cubase 15_64"));
        CopyDirectory(paths.ActiveDriverDirectory, Path.Combine(root, "MIDI Remote", "Local", CubaseConstants.DeviceVendor, CubaseConstants.DeviceName));
        CopyDirectory(legacyRoot, Path.Combine(root, "MIDI Remote", "Local", "FIGStudio"));
        CopyDirectory(paths.UserSettingsRoot, Path.Combine(root, "MIDI Remote", "User Settings"));

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                path = Path.GetRelativePath(root, path).Replace('\\', '/'),
                bytes = new FileInfo(path).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            })
            .OrderBy(item => item.path, StringComparer.Ordinal)
            .ToArray();
        var manifestPath = Path.Combine(root, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            schema_version = 2,
            created_at = DateTimeOffset.Now,
            machine = Environment.MachineName,
            user = Environment.UserName,
            components,
            files
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        return new BackupResult(root, files.Length, files.Sum(static file => file.bytes), manifestPath);
    }

    public void Restore(string backupRoot, bool confirmed)
    {
        if (!confirmed)
        {
            throw new InvalidOperationException("Restore requires explicit confirmation.");
        }

        backupRoot = Path.GetFullPath(backupRoot);
        var manifest = Path.Combine(backupRoot, "manifest.json");
        if (!File.Exists(manifest))
        {
            throw new InvalidDataException("Backup manifest.json is missing.");
        }

        using var manifestDocument = ValidateManifest(backupRoot, manifest);
        if (!manifestDocument.RootElement.TryGetProperty("schema_version", out var schemaVersion)
            || schemaVersion.GetInt32() < 2
            || !manifestDocument.RootElement.TryGetProperty("components", out var components))
        {
            throw new InvalidDataException("Backup schema v2 with component presence metadata is required for an exact restore.");
        }

        if (_isCubaseRunning())
        {
            throw new InvalidOperationException("Close Cubase before restoring its preferences or MIDI Remote scripts.");
        }

        RestoreComponent(Path.Combine(backupRoot, "Cubase 15_64"), paths.CubasePreferences,
            components.GetProperty("cubase_preferences").GetBoolean());
        RestoreComponent(Path.Combine(backupRoot, "MIDI Remote", "Local", "FIGStudio"),
            Path.Combine(paths.LocalDriverRoot, "FIGStudio"), components.GetProperty("legacy_driver").GetBoolean());
        RestoreComponent(Path.Combine(backupRoot, "MIDI Remote", "Local", CubaseConstants.DeviceVendor, CubaseConstants.DeviceName),
            paths.ActiveDriverDirectory, components.GetProperty("complete_driver").GetBoolean());
        RestoreComponent(Path.Combine(backupRoot, "MIDI Remote", "User Settings"), paths.UserSettingsRoot,
            components.GetProperty("user_settings").GetBoolean());
    }

    public void InstallActiveDriver(string sourceDirectory)
    {
        if (_isCubaseRunning())
            throw new InvalidOperationException("Close Cubase before installing the MIDI Remote driver.");
        sourceDirectory = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Generated driver directory is missing: {sourceDirectory}");
        RestoreComponent(sourceDirectory, paths.ActiveDriverDirectory, true);
    }

    private static JsonDocument ValidateManifest(string root, string manifestPath)
    {
        var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Backup manifest does not contain a files array.");
        foreach (var item in files.EnumerateArray())
        {
            var relative = item.GetProperty("path").GetString() ?? throw new InvalidDataException("Manifest file path is null.");
            var expected = item.GetProperty("sha256").GetString() ?? throw new InvalidDataException("Manifest hash is null.");
            var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                throw new InvalidDataException($"Backup file is missing or escapes the backup root: {relative}");
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual),
                    System.Text.Encoding.ASCII.GetBytes(expected.ToLowerInvariant())))
                throw new InvalidDataException($"Backup hash mismatch: {relative}");
        }
        return document;
    }

    private void RestoreComponent(string source, string destination, bool existed)
    {
        destination = Path.GetFullPath(destination);
        EnsureKnownRestoreTarget(destination);
        var displaced = destination + ".cbmcp-restore-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(destination)) Directory.Move(destination, displaced);
        try
        {
            if (existed)
            {
                if (!Directory.Exists(source)) throw new InvalidDataException($"Backup component is missing: {source}");
                CopyDirectory(source, destination, overwrite: false);
            }
            if (Directory.Exists(displaced)) Directory.Delete(displaced, true);
        }
        catch
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            if (Directory.Exists(displaced)) Directory.Move(displaced, destination);
            throw;
        }
    }

    private void EnsureKnownRestoreTarget(string target)
    {
        var allowed = new[]
        {
            paths.CubasePreferences,
            paths.ActiveDriverDirectory,
            Path.Combine(paths.LocalDriverRoot, "FIGStudio"),
            paths.UserSettingsRoot
        }.Select(Path.GetFullPath);
        if (!allowed.Contains(target, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to restore an unknown target: {target}");
    }

    public static void CopyDirectory(string source, string destination, bool overwrite = false)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }
}
