using System.Text.Json;

namespace CubaseCompleteMcp.Core;

public sealed record BuildIdentity(
    int PipeProtocolVersion,
    string? ReleaseId,
    string? GitSha,
    string ExecutablePath)
{
    public bool IsRelease => !string.IsNullOrWhiteSpace(ReleaseId);
}

public static class BuildIdentityService
{
    public static BuildIdentity Current()
    {
        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "release-manifest.json");
        if (!File.Exists(manifestPath))
            return new BuildIdentity(CubaseConstants.PipeProtocolVersion, null, null, executablePath);
        try
        {
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(
                File.ReadAllText(manifestPath).TrimStart('\uFEFF'), PipeProtocol.JsonOptions);
            return manifest?.SchemaVersion == 1
                ? new BuildIdentity(CubaseConstants.PipeProtocolVersion, manifest.ReleaseId, manifest.GitSha, executablePath)
                : new BuildIdentity(CubaseConstants.PipeProtocolVersion, null, null, executablePath);
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return new BuildIdentity(CubaseConstants.PipeProtocolVersion, null, null, executablePath);
        }
    }
}
