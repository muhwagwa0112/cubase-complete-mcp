using System.Text.RegularExpressions;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class CatalogAndGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbmcp-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Catalog_ParsesCategoriesAndStableIds()
    {
        var catalog = LoadCatalog();
        Assert.Equal(3, catalog.Commands.Count);
        Assert.Equal(2, catalog.CategoryCount);
        Assert.All(catalog.Commands, command => Assert.Matches("^cmd-[0-9a-f]{20}$", command.Id));
        Assert.Equal(catalog.Find("Transport", "Start")!.Id, KeyCommandCatalog.CreateStableId("Transport", "Start"));
    }

    [Fact]
    public void Catalog_SearchesNameCategoryAndId()
    {
        var catalog = LoadCatalog();
        var command = catalog.Find("Transport", "Stop")!;
        Assert.Single(catalog.Search("Stop"));
        Assert.Equal(2, catalog.Search(null, "Transport").Count);
        Assert.Same(command, catalog.Search(command.Id).Single());
    }

    [Fact]
    public void Catalog_RejectsDuplicateQualifiedNames()
    {
        var path = WriteXml("<item><string name=\"Name\" value=\"A\"/><list name=\"Commands\"><item><string name=\"Name\" value=\"X\"/></item><item><string name=\"Name\" value=\"X\"/></item></list></item>");
        Assert.Throws<InvalidDataException>(() => KeyCommandCatalog.Load(path));
    }

    [Fact]
    public void Generator_EmitsEveryCommandAndManifest()
    {
        var catalog = LoadCatalog();
        var result = new MidiRemoteDriverGenerator().Generate(catalog, Path.Combine(_root, "out"));
        var script = File.ReadAllText(result.DriverPath);
        Assert.Equal(3, result.CommandCount);
        Assert.Equal(2, result.CategoryCount);
        Assert.Contains("var PROTOCOL_VERSION = 2", script, StringComparison.Ordinal);
        Assert.Contains("MAX_PENDING_REQUESTS", script, StringComparison.Ordinal);
        Assert.Contains("REQUEST_CACHE_LIMIT", script, StringComparison.Ordinal);
        Assert.Contains("client_nonce", script, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(script, "registerCommand\\(\"cmd-").Count);
        Assert.DoesNotContain("=>", script, StringComparison.Ordinal);
        Assert.DoesNotContain("const ", script, StringComparison.Ordinal);
        Assert.True(File.Exists(result.CatalogPath));
        Assert.Equal(64, result.DriverSha256.Length);
    }

    [Fact]
    public void Generator_IsDeterministic()
    {
        var generator = new MidiRemoteDriverGenerator();
        var catalog = LoadCatalog();
        var a = generator.Generate(catalog, Path.Combine(_root, "a"));
        var b = generator.Generate(catalog, Path.Combine(_root, "b"));
        Assert.Equal(a.DriverSha256, b.DriverSha256);
        Assert.Equal(a.CatalogSha256, b.CatalogSha256);
    }

    [Fact]
    public void CapabilityRegistry_ClassifiesEveryCommand()
    {
        var catalog = LoadCatalog();
        var capabilities = catalog.Commands.Select(command => CapabilityRegistry.DescribeCommand(command, false)).ToArray();
        Assert.Equal(catalog.Commands.Count, capabilities.Length);
        Assert.All(capabilities, capability =>
        {
            Assert.Equal("command_backed", capability.SupportLevel);
            Assert.Equal("midi_remote.command_binding", capability.Path);
            Assert.False(capability.CanPerformAvailable);
            Assert.False(capability.Verified);
            Assert.NotEmpty(capability.Preconditions);
        });
    }

    [Theory]
    [InlineData("File", "Save", RiskLevel.Confirm)]
    [InlineData("Project", "Remove Selected Tracks", RiskLevel.Confirm)]
    [InlineData("Transport", "Record", RiskLevel.Confirm)]
    [InlineData("Transport", "Start", RiskLevel.Low)]
    public void CapabilityRegistry_ClassifiesCommandRisk(string category, string name, RiskLevel expected) =>
        Assert.Equal(expected, CapabilityRegistry.ClassifyCommand(
            new KeyCommandDescriptor(KeyCommandCatalog.CreateStableId(category, name), category, name, 0)));

    [Fact]
    public void CapabilityRegistry_ExposesAllGenericControlSurfaces()
    {
        Assert.Equal(4, CapabilityRegistry.DirectRoots().Select(root => root.Id).Distinct().Count());
        var ui = CapabilityRegistry.UiActions();
        Assert.Contains(ui, action => action.Action == "drag" && action.Target == "snapshot_coordinate");
        Assert.Contains(ui, action => action.Action == "key" && action.Target == "snapshot_window");
        Assert.Contains(ui, action => action.Action == "text" && action.Target == "snapshot_window");
        Assert.All(ui.Where(action => action.Action != "focus"), action => Assert.Equal(RiskLevel.Confirm, action.Risk));
    }

    private KeyCommandCatalog LoadCatalog() => KeyCommandCatalog.Load(WriteXml(
        "<item><string name=\"Name\" value=\"Transport\"/><list name=\"Commands\"><item><string name=\"Name\" value=\"Start\"/></item><item><string name=\"Name\" value=\"Stop\"/></item></list></item>" +
        "<item><string name=\"Name\" value=\"File\"/><list name=\"Commands\"><item><string name=\"Name\" value=\"Save\"/></item></list></item>"));

    private string WriteXml(string categories)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, $"<preferences><list name=\"Categories\">{categories}</list></preferences>");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
