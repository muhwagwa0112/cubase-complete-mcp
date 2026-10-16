using System.Text;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Broker;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests.Fakes;

/// <summary>Builds a real <see cref="CubaseController"/> over fake host adapters with short, deterministic timings.</summary>
public sealed class ControllerHarness : IDisposable
{
    public static readonly (string Category, string Name)[] CatalogCommands =
    [
        ("Transport", "Start"), ("Transport", "Stop"), ("Transport", "Record"),
        ("AddTrack", "MIDI"), ("AddTrack", "Instrument"), ("AddTrack", "Audio"), ("AddTrack", "Marker"),
        ("Edit", "Rename First Selected Track"), ("Project", "Remove Selected Tracks"), ("Project", "Duplicate Tracks"),
        ("Edit", "Freeze/Unfreeze Selected Tracks"), ("Audio", "Disable/Enable Track"),
        ("File", "Open"), ("File", "Save"), ("File", "Save As"), ("File", "Export Audio Mixdown"), ("Audio Export", "Perform Audio Export"),
        ("File", "Import MIDI File"), ("File", "New"), ("File", "Close"), ("Edit", "Mystery Command")
    ];

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cbmcp-harness-" + Guid.NewGuid().ToString("N"));
    public FakeDriver Driver { get; } = new();
    public FakeUi Ui { get; } = new();
    public FakeProcess Process { get; } = new();
    public ConfirmationStore Confirmations { get; } = new();
    public ExecutionJournal Journal { get; }
    public TargetHandleStore Targets { get; } = new();
    public MutationCoordinator Coordinator { get; }
    public MidiCompositionService Compositions { get; }
    public KeyCommandCatalog Catalog { get; }
    public CubaseController Controller { get; }
    public IPluginCatalogProvider Plugins { get; }

    public static readonly ControllerTimings FastTimings = new(
        new ObservationTimings(TimeSpan.FromMilliseconds(2500), TimeSpan.FromMilliseconds(20)),
        new ObservationTimings(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(20)),
        new ObservationTimings(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(20)),
        new ObservationTimings(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(20)),
        TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10));

    public ControllerHarness(IPluginCatalogProvider? plugins = null, ControllerTimings? timings = null,
        MutationCoordinator? coordinator = null)
    {
        Directory.CreateDirectory(Root);
        Journal = new ExecutionJournal(Path.Combine(Root, "journal"));
        Compositions = new MidiCompositionService(Path.Combine(Root, "state"));
        Coordinator = coordinator ?? new MutationCoordinator(TimeSpan.FromSeconds(5));
        Plugins = plugins ?? new UnavailablePluginCatalogProvider();
        var xml = new StringBuilder("<preferences><list name=\"Categories\">");
        foreach (var group in CatalogCommands.GroupBy(item => item.Category))
        {
            xml.Append($"<item><string name=\"Name\" value=\"{group.Key}\"/><list name=\"Commands\">");
            foreach (var command in group) xml.Append($"<item><string name=\"Name\" value=\"{System.Security.SecurityElement.Escape(command.Name)}\"/></item>");
            xml.Append("</list></item>");
        }
        xml.Append("</list></preferences>");
        var catalogPath = Path.Combine(Root, "Key Commands.xml");
        File.WriteAllText(catalogPath, xml.ToString());
        Catalog = KeyCommandCatalog.Load(catalogPath);
        var paths = new CubasePaths(Root, Path.Combine(Root, "state"), Path.Combine(Root, "backups"), Root, Root, Root, Root, Root, Root, catalogPath);
        Controller = new CubaseController(paths, Confirmations, Process, Driver, Ui, Compositions,
            new BuildIdentity(CubaseConstants.PipeProtocolVersion, null, "test-sha", "broker.exe"), Journal, Targets, Coordinator,
            Plugins, timings ?? FastTimings, Catalog);
    }

    public string CommandId(string category, string name) => Catalog.Find(category, name)!.Id;

    public Task<BrokerResponse> SendAsync(string operation, JsonObject? arguments = null, string? confirmationId = null,
        string? operationId = null, string session = "session-a", int? protocol = CubaseConstants.PipeProtocolVersion,
        CancellationToken cancellationToken = default) =>
        Controller.HandleAsync(new BrokerRequest(Guid.NewGuid().ToString("N"), operation, arguments, confirmationId, session,
            protocol, null, operationId), cancellationToken);

    /// <summary>Plans a confirm-required request, then commits it with the returned confirmation.</summary>
    public async Task<BrokerResponse> ConfirmAndCommitAsync(string operation, JsonObject arguments, string session = "session-a")
    {
        var proposal = await SendAsync(operation, (JsonObject)arguments.DeepClone(), session: session);
        Assert.Equal(ResultStatus.RequiresConfirmation, proposal.Status);
        return await SendAsync(operation, (JsonObject)arguments.DeepClone(), proposal.Data!["confirmation_id"]!.GetValue<string>(), session: session);
    }

    public async Task<string> HandleForAsync(FakeChannel channel, string session = "session-a")
    {
        var list = await SendAsync("track", new JsonObject { ["action"] = "list" }, session: session);
        Assert.Equal(ResultStatus.Ok, list.Status);
        return list.Data!["channels"]!.AsArray().First(node => node!["unique_id"]!.GetValue<string>() == channel.UniqueId)!["handle"]!.GetValue<string>();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch (IOException) { }
    }
}
