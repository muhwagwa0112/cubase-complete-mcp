using System.Text.Json;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbmcp-backup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DiscoveredPaths_KeepDriverAndUserSettingsSeparate()
    {
        var paths = CubasePaths.Discover();
        Assert.EndsWith(Path.Combine("Driver Scripts", "Local", "CBMCP", "CompleteBridge"),
            paths.ActiveDriverDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("MIDI Remote", "User Settings"), paths.UserSettingsRoot,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(paths.ActiveDriverDirectory, paths.UserSettingsRoot);
    }

    [Fact]
    public void BackupAndRestore_PreservesPresenceAndRemovesStaleFiles()
    {
        var paths = CreatePaths();
        Write(Path.Combine(paths.CubasePreferences, "Defaults.xml"), "original-preferences");
        Write(Path.Combine(paths.LocalDriverRoot, "FIGStudio", "legacy.js"), "original-legacy");
        Write(Path.Combine(paths.UserSettingsRoot, "mapping.json"), "original-settings");
        var service = new BackupService(paths, () => false);
        var backup = service.Create("test");

        Write(Path.Combine(paths.CubasePreferences, "stale.xml"), "stale");
        Write(Path.Combine(paths.LocalDriverRoot, "FIGStudio", "legacy.js"), "modified");
        Write(Path.Combine(paths.ActiveDriverDirectory, "new-driver.js"), "new");
        service.Restore(backup.Root, true);

        Assert.Equal("original-preferences", File.ReadAllText(Path.Combine(paths.CubasePreferences, "Defaults.xml")));
        Assert.False(File.Exists(Path.Combine(paths.CubasePreferences, "stale.xml")));
        Assert.Equal("original-legacy", File.ReadAllText(Path.Combine(paths.LocalDriverRoot, "FIGStudio", "legacy.js")));
        Assert.False(Directory.Exists(paths.ActiveDriverDirectory));
        Assert.Equal("original-settings", File.ReadAllText(Path.Combine(paths.UserSettingsRoot, "mapping.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(backup.ManifestPath));
        Assert.Equal(2, manifest.RootElement.GetProperty("schema_version").GetInt32());
        Assert.False(manifest.RootElement.GetProperty("components").GetProperty("complete_driver").GetBoolean());

        var generated = Path.Combine(_root, "generated");
        Write(Path.Combine(generated, "bridge.js"), "v2");
        service.InstallActiveDriver(generated);
        Write(Path.Combine(paths.ActiveDriverDirectory, "stale.js"), "stale");
        service.InstallActiveDriver(generated);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(paths.ActiveDriverDirectory, "bridge.js")));
        Assert.False(File.Exists(Path.Combine(paths.ActiveDriverDirectory, "stale.js")));
    }

    private CubasePaths CreatePaths()
    {
        var state = Path.Combine(_root, "state");
        var midi = Path.Combine(_root, "MIDI Remote");
        var drivers = Path.Combine(midi, "Driver Scripts");
        return new CubasePaths(_root, state, Path.Combine(state, "backups"), Path.Combine(_root, "Cubase 15_64"),
            midi, drivers, Path.Combine(drivers, "Local"), Path.Combine(midi, "User Settings"),
            Path.Combine(drivers, "Local", "CBMCP", "CompleteBridge"), Path.Combine(_root, "Cubase 15_64", "Key Commands.xml"));
    }

    private static void Write(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
