using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CubaseCompleteMcp.Core;

public static class ReleaseRoles
{
    public const string ServerManaged = "server_managed";
    public const string BrokerManaged = "broker_managed";
    public const string CliManaged = "cli_managed";
    public const string CoreManaged = "core_managed";
    public const string DriverScript = "driver_script";
    public const string CommandCatalog = "command_catalog";
    public const string Payload = "payload";

    public static readonly string[] Required = [ServerManaged, BrokerManaged, CliManaged, CoreManaged, DriverScript, CommandCatalog];

    public static string For(string relativePath) => relativePath.Replace('\\', '/') switch
    {
        "CubaseCompleteMcp.Server.dll" => ServerManaged,
        "CubaseCompleteMcp.Broker.dll" => BrokerManaged,
        "CubaseCompleteMcp.Cli.dll" => CliManaged,
        "CubaseCompleteMcp.Core.dll" => CoreManaged,
        "driver/" + MidiRemoteDriverGenerator.DriverFileName => DriverScript,
        "driver/" + MidiRemoteDriverGenerator.CatalogFileName => CommandCatalog,
        _ => Payload
    };
}

public sealed record ReleaseFileEntry(string Path, long Bytes, string Sha256, string Role);

/// <summary>
/// Source release manifest v2: every file that is actually published, with size, SHA-256 and role. The manifest
/// does not list itself. Hashes provide integrity, not publisher authentication (the manifest is unsigned).
/// </summary>
public sealed record ReleaseManifest(
    int SchemaVersion,
    string ReleaseId,
    DateTimeOffset CreatedAtUtc,
    string BaselineSha,
    string CandidateGitSha,
    bool SourceDirty,
    string Configuration,
    string DotnetSdk,
    string NodeVersion,
    int PipeProtocolVersion,
    int MidiWireProtocolVersion,
    int DriverContractVersion,
    string DriverBuildId,
    string CatalogInputSha256,
    string GeneratorSourceSha,
    IReadOnlyList<ReleaseFileEntry> Files)
{
    [JsonIgnore] public string GitSha => CandidateGitSha;
}

public sealed record ValidatedRelease(string Root, ReleaseManifest Manifest, string ServerPath, string ManifestSha256);

public sealed record ReleaseValidationOptions(bool AllowDirtySource = false, string? ExpectedManifestSha256 = null);

public static class ReleaseManifestService
{
    public const string ManifestFileName = "release-manifest.json";
    public const string BaselineSha = "1feeca09cddd4fcdec41a07d12d4310317fa5907";

    private static readonly JsonSerializerOptions WriteOptions = new(PipeProtocol.JsonOptions) { WriteIndented = true };

    /// <summary>Enumerates the finished payload directory and returns the manifest that describes exactly it.</summary>
    public static ReleaseManifest Build(string payloadRoot, string releaseId, string candidateGitSha, bool sourceDirty,
        string configuration, string dotnetSdk, string nodeVersion, string driverBuildId, string catalogInputSha256,
        string generatorSourceSha)
    {
        var root = Path.GetFullPath(payloadRoot);
        var files = new List<ReleaseFileEntry>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(item => item, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative == ManifestFileName) continue;
            RejectReparse(root, path);
            files.Add(new ReleaseFileEntry(relative, new FileInfo(path).Length, HashFile(path), ReleaseRoles.For(relative)));
        }
        return new ReleaseManifest(2, releaseId, DateTimeOffset.UtcNow, BaselineSha, candidateGitSha, sourceDirty, configuration,
            dotnetSdk, nodeVersion, CubaseConstants.PipeProtocolVersion, CubaseConstants.MidiWireProtocolVersion,
            CubaseConstants.DriverContractVersion, driverBuildId, catalogInputSha256, generatorSourceSha, files);
    }

    public static string Write(string payloadRoot, ReleaseManifest manifest)
    {
        var path = Path.Combine(payloadRoot, ManifestFileName);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, WriteOptions));
        return path;
    }

    /// <summary>
    /// Validates one explicitly named release directory. There is no "latest timestamp" selection: callers pass the
    /// candidate they intend to use (and, when known, the manifest hash they expect).
    /// </summary>
    public static ValidatedRelease Validate(string releaseRoot, ReleaseValidationOptions? options = null)
    {
        options ??= new ReleaseValidationOptions();
        var root = Path.GetFullPath(releaseRoot);
        if (!Directory.Exists(root)) throw new InvalidDataException($"Release directory does not exist: {root}");
        RejectReparse(root, root);
        var manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath)) throw new InvalidDataException("release-manifest.json is missing.");
        var manifestSha = HashFile(manifestPath);
        if (options.ExpectedManifestSha256 is { } expected && !expected.Equals(manifestSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The release manifest hash differs from the expected candidate manifest hash.");
        ReleaseManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(manifestPath).TrimStart('﻿'), PipeProtocol.JsonOptions)
                ?? throw new InvalidDataException("Release manifest deserialized to null.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Release manifest is not a valid schema 2 manifest: " + error.Message, error);
        }
        if (manifest.SchemaVersion != 2) throw new InvalidDataException("Unsupported release manifest schema; schema 2 is required.");
        if (!manifest.Configuration.Equals("Release", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only Release configuration can be registered.");
        if (manifest.SourceDirty && !options.AllowDirtySource)
            throw new InvalidDataException("The release was built from a dirty source tree; it is a development artifact and cannot be registered.");
        if (!IsSha1(manifest.CandidateGitSha)) throw new InvalidDataException("candidate_git_sha must be a 40-character commit SHA.");
        foreach (var (name, value) in new[] { ("dotnet_sdk", manifest.DotnetSdk), ("node_version", manifest.NodeVersion),
                     ("driver_build_id", manifest.DriverBuildId), ("release_id", manifest.ReleaseId) })
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"Release manifest provenance is incomplete: {name}.");
        if (!IsSha256(manifest.CatalogInputSha256)) throw new InvalidDataException("catalog_input_sha256 is invalid.");
        if (manifest.PipeProtocolVersion != CubaseConstants.PipeProtocolVersion || manifest.DriverContractVersion != CubaseConstants.DriverContractVersion)
            throw new InvalidDataException("Release pipe protocol or driver contract version does not match this client.");
        if (manifest.Files is null || manifest.Files.Count == 0) throw new InvalidDataException("Release manifest lists no files.");

        var listed = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Sha256) || string.IsNullOrWhiteSpace(entry.Role))
                throw new InvalidDataException("A release file entry lacks path, hash or role.");
            if (entry.Path == ManifestFileName || !listed.Add(entry.Path) || !folded.Add(entry.Path))
                throw new InvalidDataException($"Duplicate or self-referential release path: {entry.Path}");
            if (entry.Role != ReleaseRoles.For(entry.Path)) throw new InvalidDataException($"Release role for {entry.Path} is wrong.");
            var path = ResolveContained(root, entry.Path);
            if (!File.Exists(path)) throw new InvalidDataException($"Release file is missing: {entry.Path}");
            RejectReparse(root, path);
            if (new FileInfo(path).Length != entry.Bytes) throw new InvalidDataException($"Release file size does not match: {entry.Path}");
            if (!HashFile(path).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Release file hash does not match the manifest: {entry.Path}");
        }
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => path != ManifestFileName).ToHashSet(StringComparer.Ordinal);
        var unlisted = actual.Except(listed).ToArray();
        if (unlisted.Length > 0) throw new InvalidDataException($"Release contains files the manifest does not list: {string.Join(", ", unlisted.Take(5))}");
        var missingRoles = ReleaseRoles.Required.Except(manifest.Files.Select(entry => entry.Role)).ToArray();
        if (missingRoles.Length > 0) throw new InvalidDataException($"Release lacks required roles: {string.Join(", ", missingRoles)}");
        var server = manifest.Files.First(entry => entry.Role == ReleaseRoles.ServerManaged);
        var apphost = Path.ChangeExtension(ResolveContained(root, server.Path), ".exe");
        return new ValidatedRelease(root, manifest, File.Exists(apphost) && listed.Contains(Path.GetRelativePath(root, apphost).Replace('\\', '/'))
            ? apphost : ResolveContained(root, server.Path), manifestSha);
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void RejectReparse(string root, string path)
    {
        var current = new FileInfo(path).Exists ? (FileSystemInfo)new FileInfo(path) : new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Release paths must not be symbolic links or reparse points: {current.FullName}");
            if (string.Equals(current.FullName.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) break;
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
    }

    private static string ResolveContained(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\')
            || relativePath.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new InvalidDataException($"Release paths must be relative POSIX paths inside the release: {relativePath}");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Release path escaped the release directory.");
        return path;
    }

    private static bool IsSha1(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit) && value == value.ToLowerInvariant();
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit) && value == value.ToLowerInvariant();
}

/// <summary>
/// Local installation receipt: what was actually generated from the local Key Commands catalog and installed, as
/// distinct from the source release's reference driver. Live gates compare it with the driver's hello build ID.
/// </summary>
public sealed record InstallationReceipt(
    int SchemaVersion,
    string CandidateGitSha,
    string ReleaseManifestSha256,
    DateTimeOffset InstalledAtUtc,
    string CatalogInputSha256,
    string InstalledDriverSha256,
    string InstalledCatalogSha256,
    string ExpectedDriverBuildId,
    string? ObservedDriverBuildId,
    string InstallPathRedacted,
    string? BackupId);
