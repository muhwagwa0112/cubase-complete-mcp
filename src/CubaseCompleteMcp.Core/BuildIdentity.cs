using System.Text.Json;

namespace CubaseCompleteMcp.Core;

public sealed record BuildIdentity(
    int PipeProtocolVersion,
    string? ReleaseId,
    string? GitSha,
    string ExecutablePath,
    string? DriverBuildId = null)
{
    public bool IsRelease => !string.IsNullOrWhiteSpace(ReleaseId);
}

public static class BuildIdentityService
{
    /// <summary>
    /// Identity of the running binaries. Only a schema-2 release manifest beside the executable counts; a missing or
    /// older manifest means a development build (no release ID).
    /// </summary>
    public static BuildIdentity Current()
    {
        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var manifestPath = Path.Combine(AppContext.BaseDirectory, ReleaseManifestService.ManifestFileName);
        if (!File.Exists(manifestPath))
            return new BuildIdentity(CubaseConstants.PipeProtocolVersion, null, null, executablePath);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath).TrimStart('﻿'));
            var root = document.RootElement;
            if (!root.TryGetProperty("schema_version", out var schema) || schema.GetInt32() != 2)
                return new BuildIdentity(CubaseConstants.PipeProtocolVersion, null, null, executablePath);
            return new BuildIdentity(CubaseConstants.PipeProtocolVersion,
                root.TryGetProperty("release_id", out var release) ? release.GetString() : null,
                root.TryGetProperty("candidate_git_sha", out var sha) ? sha.GetString() : null,
                executablePath,
                root.TryGetProperty("driver_build_id", out var build) ? build.GetString() : null);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        {
            return new BuildIdentity(CubaseConstants.PipeProtocolVersion, null, null, executablePath);
        }
    }
}
