using System.Security.Cryptography;
using System.Text.Json;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class ReleaseManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbmcp-release-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LatestValidRelease_WinsAndFlatPublishIsIgnored()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "CubaseCompleteMcp.Server.exe"), "stale-flat");
        WriteRelease("20260829-010000", valid: true);
        var expected = WriteRelease("20260829-020000", valid: true);
        var selected = ReleaseManifestService.FindLatestValid(_root);
        Assert.Equal("20260829-020000", selected.Manifest.ReleaseId);
        Assert.Equal(Path.Combine(expected, "CubaseCompleteMcp.Server.exe"), selected.ServerPath);
    }

    [Fact]
    public void InvalidHashAndMissingManifest_AreRejected()
    {
        var release = WriteRelease("20260829-030000", valid: true);
        File.AppendAllText(Path.Combine(release, "CubaseCompleteMcp.Core.dll"), "tampered");
        var error = Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(release));
        Assert.Contains("hash", error.Message, StringComparison.OrdinalIgnoreCase);

        var missing = Path.Combine(_root, "20260829-040000");
        Directory.CreateDirectory(missing);
        Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(missing));
    }

    [Fact]
    public void Utf8BomManifest_IsAcceptedForBackwardCompatibility()
    {
        var release = WriteRelease("20260829-050000", valid: true);
        var path = Path.Combine(release, "release-manifest.json");
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
        Assert.Equal("20260829-050000", ReleaseManifestService.Validate(release).Manifest.ReleaseId);
    }

    private string WriteRelease(string id, bool valid)
    {
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);
        var names = new Dictionary<string, string>
        {
            ["server"] = "CubaseCompleteMcp.Server.exe", ["broker"] = "CubaseCompleteMcp.Broker.exe",
            ["core"] = "CubaseCompleteMcp.Core.dll", ["cli"] = "CubaseCompleteMcp.Cli.exe"
        };
        var components = new Dictionary<string, ReleaseComponent>();
        foreach (var pair in names)
        {
            var path = Path.Combine(directory, pair.Value);
            File.WriteAllText(path, pair.Key);
            components[pair.Key] = new ReleaseComponent(pair.Value,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        }
        var manifest = new ReleaseManifest(1, id, DateTimeOffset.UtcNow, new string('a', 40),
            valid ? "Release" : "Debug", "8.0.301", components);
        File.WriteAllBytes(Path.Combine(directory, "release-manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, PipeProtocol.JsonOptions));
        return directory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
