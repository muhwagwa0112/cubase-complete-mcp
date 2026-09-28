using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

/// <summary>REL-04..07: manifest v2 lists the whole published payload and a candidate is only ever chosen explicitly.</summary>
public sealed class ReleaseManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbmcp-release-" + Guid.NewGuid().ToString("N"));
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private string WriteRelease(string id, bool dirty = false)
    {
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(Path.Combine(directory, "driver"));
        Directory.CreateDirectory(Path.Combine(directory, "runtimes", "win"));
        foreach (var name in new[] { "CubaseCompleteMcp.Server.dll", "CubaseCompleteMcp.Server.exe", "CubaseCompleteMcp.Broker.dll",
                     "CubaseCompleteMcp.Broker.exe", "CubaseCompleteMcp.Cli.dll", "CubaseCompleteMcp.Core.dll",
                     "CubaseCompleteMcp.Server.deps.json", "CubaseCompleteMcp.Server.runtimeconfig.json", "ModelContextProtocol.dll",
                     "driver/CBMCP_CompleteBridge.js", "driver/command_catalog.json", "runtimes/win/native.dll" })
            File.WriteAllText(Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar)), "content of " + name);
        var manifest = ReleaseManifestService.Build(directory, id, Sha, dirty, "Release", "8.0.301", "v24.11.1",
            new string('b', 64), new string('c', 64), Sha);
        ReleaseManifestService.Write(directory, manifest);
        return directory;
    }

    [Fact]
    public void REL04_EveryPublishedFileIsListedAndAnyChangeIsDetected()
    {
        var release = WriteRelease("r1");
        var valid = ReleaseManifestService.Validate(release);
        Assert.Equal(12, valid.Manifest.Files.Count);
        Assert.All(ReleaseRoles.Required, role => Assert.Contains(valid.Manifest.Files, entry => entry.Role == role));
        Assert.Contains(valid.Manifest.Files, entry => entry.Path == "runtimes/win/native.dll" && entry.Role == ReleaseRoles.Payload);
        Assert.EndsWith("CubaseCompleteMcp.Server.exe", valid.ServerPath, StringComparison.Ordinal);

        var tamper = WriteRelease("t1");
        File.AppendAllText(Path.Combine(tamper, "CubaseCompleteMcp.Broker.dll"), "x");
        Assert.Contains("size", Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(tamper)).Message, StringComparison.OrdinalIgnoreCase);
        var sameSize = WriteRelease("t2");
        var sameSizePath = Path.Combine(sameSize, "ModelContextProtocol.dll");
        File.WriteAllText(sameSizePath, File.ReadAllText(sameSizePath)[..^1] + "#"); // same size, different bytes
        Assert.Contains("hash", Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(sameSize)).Message, StringComparison.OrdinalIgnoreCase);
        var removed = WriteRelease("t3");
        File.Delete(Path.Combine(removed, "runtimes", "win", "native.dll"));
        Assert.Contains("missing", Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(removed)).Message, StringComparison.OrdinalIgnoreCase);
        var added = WriteRelease("t4");
        File.WriteAllText(Path.Combine(added, "extra.dll"), "unlisted code");
        Assert.Contains("does not list", Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(added)).Message, StringComparison.OrdinalIgnoreCase);
        var managedOnly = WriteRelease("t5");
        File.WriteAllText(Path.Combine(managedOnly, "CubaseCompleteMcp.Core.dll"), "content of CubaseCompleteMcp.Core.dlX");
        Assert.Contains("hash", Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(managedOnly)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void REL05_PathEscapeDuplicatesAndReparsePointsAreRejected()
    {
        foreach (var bad in new[] { "../outside.dll", "/abs.dll", "C:/abs.dll", "a/./b.dll", "dir\\file.dll" })
        {
            var release = WriteRelease("p-" + Guid.NewGuid().ToString("N")[..6]);
            EditManifest(release, files => files.Add(new JsonObject { ["path"] = bad, ["bytes"] = 1, ["sha256"] = new string('a', 64), ["role"] = "payload" }));
            Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(release));
        }
        var duplicate = WriteRelease("dup");
        EditManifest(duplicate, files => files.Add(files[0]!.DeepClone()));
        Assert.Contains("Duplicate", Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(duplicate)).Message, StringComparison.Ordinal);
        var caseDuplicate = WriteRelease("dup2");
        EditManifest(caseDuplicate, files =>
        {
            var copy = files[0]!.DeepClone();
            copy["path"] = copy["path"]!.GetValue<string>().ToUpperInvariant();
            files.Add(copy);
        });
        Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(caseDuplicate));
        var wrongRole = WriteRelease("role");
        EditManifest(wrongRole, files => files.First(file => file!["path"]!.GetValue<string>() == "CubaseCompleteMcp.Core.dll")!["role"] = "payload");
        Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(wrongRole));

        var linked = WriteRelease("link");
        var outside = Path.Combine(_root, "outside-target");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(linked, "linked"), outside);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Creating symbolic links needs developer mode or elevation; junction creation is the fallback.
            var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(linked, "linked")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            junction.WaitForExit();
        }
        File.WriteAllText(Path.Combine(outside, "evil.dll"), "outside code");
        Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(linked));
    }

    [Fact]
    public void REL06_DirtySourceIsRefusedAndTheCandidateIsExplicit()
    {
        var dirty = WriteRelease("dirty", dirty: true);
        Assert.Contains("dirty", Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(dirty)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ReleaseManifestService.Validate(dirty, new ReleaseValidationOptions(AllowDirtySource: true)).Manifest.SourceDirty);
        var clean = WriteRelease("clean");
        var hash = ReleaseManifestService.Validate(clean).ManifestSha256;
        Assert.Equal(hash, ReleaseManifestService.Validate(clean, new ReleaseValidationOptions(ExpectedManifestSha256: hash)).ManifestSha256);
        Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(clean, new ReleaseValidationOptions(ExpectedManifestSha256: new string('0', 64))));
        Assert.Null(typeof(ReleaseManifestService).GetMethod("FindLatestValid")); // no automatic newest-timestamp selection
        var schemaOne = WriteRelease("schema1");
        File.WriteAllText(Path.Combine(schemaOne, ReleaseManifestService.ManifestFileName), """{"schema_version":1,"release_id":"schema1"}""");
        Assert.Throws<InvalidDataException>(() => ReleaseManifestService.Validate(schemaOne));
    }

    [Fact]
    public void REL07_DriverBuildIdIsDeterministicAndSeparatesReferenceFromInstalledCatalogs()
    {
        var reference = KeyCommandCatalog.Load(WriteCatalog("Transport", "Start"));
        var local = KeyCommandCatalog.Load(WriteCatalog("Transport", "Stop"));
        var generator = new MidiRemoteDriverGenerator();
        var first = generator.Generate(reference, Path.Combine(_root, "g1"), Sha);
        var second = generator.Generate(reference, Path.Combine(_root, "g2"), Sha);
        var installed = generator.Generate(local, Path.Combine(_root, "g3"), Sha);
        var otherSource = generator.Generate(reference, Path.Combine(_root, "g4"), new string('f', 40));
        Assert.Equal(first.DriverBuildId, second.DriverBuildId);
        Assert.Equal(first.DriverSha256, second.DriverSha256);
        Assert.NotEqual(first.DriverBuildId, installed.DriverBuildId);
        Assert.NotEqual(first.CatalogInputSha256, installed.CatalogInputSha256);
        Assert.NotEqual(first.DriverBuildId, otherSource.DriverBuildId);
        Assert.Equal(MidiRemoteDriverGenerator.ComputeDriverBuildId(Sha, first.CatalogInputSha256), first.DriverBuildId);
        Assert.Contains($"var DRIVER_BUILD_ID = \"{first.DriverBuildId}\";", File.ReadAllText(first.DriverPath), StringComparison.Ordinal);
        var catalogJson = JsonNode.Parse(File.ReadAllText(first.CatalogPath))!;
        Assert.Equal(first.DriverBuildId, catalogJson["driver_build_id"]!.GetValue<string>());
        Assert.Equal(2, catalogJson["driver_contract_version"]!.GetValue<int>());
    }

    private string WriteCatalog(string category, string name)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, $"<preferences><list name=\"Categories\"><item><string name=\"Name\" value=\"{category}\"/><list name=\"Commands\"><item><string name=\"Name\" value=\"{name}\"/></item></list></item></list></preferences>");
        return path;
    }

    private static void EditManifest(string release, Action<JsonArray> edit)
    {
        var path = Path.Combine(release, ReleaseManifestService.ManifestFileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        edit(json["files"]!.AsArray());
        File.WriteAllText(path, json.ToJsonString());
    }

    public void Dispose()
    {
        try
        {
            foreach (var link in Directory.Exists(_root) ? Directory.EnumerateDirectories(_root, "linked", SearchOption.AllDirectories).ToArray() : [])
                Directory.Delete(link);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }
}
