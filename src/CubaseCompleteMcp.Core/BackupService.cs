using System.Security.Cryptography;
using System.Text.Json;

namespace CubaseCompleteMcp.Core;

public sealed record BackupResult(string Root, int FileCount, long TotalBytes, string ManifestPath);

public sealed class BackupService(CubasePaths paths)
{
    public BackupResult Create(string label = "manual")
    {
        var safeLabel = new string(label.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeLabel))
        {
            safeLabel = "manual";
        }

        var root = Path.Combine(paths.BackupsRoot, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{safeLabel}");
        Directory.CreateDirectory(root);
        CopyDirectory(paths.CubasePreferences, Path.Combine(root, "Cubase 15_64"));
        CopyDirectory(paths.LocalDriverRoot, Path.Combine(root, "MIDI Remote", "Local", CubaseConstants.DeviceVendor, CubaseConstants.DeviceName));

        var legacyRoot = Path.Combine(paths.LocalDriverRoot, "FIGStudio");
        legacyRoot = Path.GetFullPath(legacyRoot);
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
            schema_version = 1,
            created_at = DateTimeOffset.Now,
            machine = Environment.MachineName,
            user = Environment.UserName,
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

        ValidateManifest(backupRoot, manifest);

        if (System.Diagnostics.Process.GetProcessesByName("Cubase15").Length > 0)
        {
            throw new InvalidOperationException("Close Cubase before restoring its preferences or MIDI Remote scripts.");
        }

        CopyDirectory(Path.Combine(backupRoot, "Cubase 15_64"), paths.CubasePreferences, overwrite: true);
        CopyDirectory(Path.Combine(backupRoot, "MIDI Remote", "Local", "FIGStudio"),
            Path.Combine(paths.LocalDriverRoot, "FIGStudio"), overwrite: true);
        CopyDirectory(Path.Combine(backupRoot, "MIDI Remote", "Local", CubaseConstants.DeviceVendor, CubaseConstants.DeviceName),
            paths.ActiveDriverDirectory, overwrite: true);
        CopyDirectory(Path.Combine(backupRoot, "MIDI Remote", "User Settings"), paths.UserSettingsRoot, overwrite: true);
    }

    private static void ValidateManifest(string root, string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
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
