using System.Security.Cryptography;
using System.Text.Json;

namespace CubaseCompleteMcp.Core;

public sealed record ReleaseComponent(string Path, string Sha256);
public sealed record ReleaseManifest(int SchemaVersion, string ReleaseId, DateTimeOffset CreatedAtUtc,
    string GitSha, string Configuration, string DotnetSdk, Dictionary<string, ReleaseComponent> Components,
    bool SourceDirty = false);
public sealed record ValidatedRelease(string Root, ReleaseManifest Manifest, string ServerPath);

public static class ReleaseManifestService
{
    private static readonly string[] RequiredComponents = ["server", "broker", "core", "cli"];

    public static ValidatedRelease FindLatestValid(string publishRoot)
    {
        if (!Directory.Exists(publishRoot))
            throw new DirectoryNotFoundException($"Publish root does not exist: {publishRoot}");
        var errors = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(publishRoot)
                     .Where(path => IsReleaseId(Path.GetFileName(path)))
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            try { return Validate(directory); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
            {
                errors.Add($"{Path.GetFileName(directory)}: {error.Message}");
            }
        }
        throw new InvalidDataException("No valid manifest-backed Release was found. " + string.Join("; ", errors));
    }

    public static ValidatedRelease Validate(string releaseRoot)
    {
        var root = Path.GetFullPath(releaseRoot);
        var manifestPath = Path.Combine(root, "release-manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("release-manifest.json is missing.");
        var manifestJson = File.ReadAllText(manifestPath).TrimStart('\uFEFF');
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestJson, PipeProtocol.JsonOptions)
            ?? throw new InvalidDataException("Release manifest deserialized to null.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported release manifest schema.");
        if (!manifest.ReleaseId.Equals(Path.GetFileName(root), StringComparison.Ordinal) || !IsReleaseId(manifest.ReleaseId))
            throw new InvalidDataException("Release ID does not match its directory.");
        if (!manifest.Configuration.Equals("Release", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only Release configuration can be registered by default.");
        if (string.IsNullOrWhiteSpace(manifest.GitSha) || string.IsNullOrWhiteSpace(manifest.DotnetSdk))
            throw new InvalidDataException("Release manifest provenance is incomplete.");
        foreach (var name in RequiredComponents)
        {
            if (!manifest.Components.TryGetValue(name, out var component))
                throw new InvalidDataException($"Release component '{name}' is missing.");
            var path = ResolveContained(root, component.Path);
            if (!File.Exists(path)) throw new InvalidDataException($"Release component '{name}' does not exist.");
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (!actual.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Release component '{name}' hash does not match the manifest.");
        }
        var serverPath = ResolveContained(root, manifest.Components["server"].Path);
        return new ValidatedRelease(root, manifest, serverPath);
    }

    private static string ResolveContained(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Release component paths must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Release component escaped the release directory.");
        return path;
    }

    private static bool IsReleaseId(string value) => value.Length == 15 && value[8] == '-'
        && value.Where((_, index) => index != 8).All(char.IsAsciiDigit);
}
