using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Broker;

/// <summary>
/// Broker request pipeline: normalize, plan (resolve targets and preconditions), approval, mutation lease,
/// re-plan under the lease, journal (dispatching), dispatch once, observe. Reads skip the lease and journal.
/// </summary>
public sealed partial class CubaseController
{
    private readonly CubasePaths _paths;
    private readonly ConfirmationStore _confirmations;
    private readonly IHostProcessProbe _processes;
    private readonly ICubaseDriver _driver;
    private readonly IWorkflowUi _ui;
    private readonly MidiCompositionService _compositions;
    private readonly BuildIdentity _buildIdentity;
    private readonly KeyCommandCatalog? _commands;
    private readonly ExecutionJournal _journal;
    private readonly TargetHandleStore _targets;
    private readonly MutationCoordinator _coordinator;
    private readonly IPluginCatalogProvider _plugins;
    private readonly ControllerTimings _timings;
    private readonly string _brokerInstanceId = Guid.NewGuid().ToString("N");

    public CubaseController(CubasePaths paths, ConfirmationStore confirmations, IHostProcessProbe processes,
        ICubaseDriver driver, IWorkflowUi ui, MidiCompositionService compositions, BuildIdentity buildIdentity,
        ExecutionJournal journal, TargetHandleStore targets, MutationCoordinator coordinator,
        IPluginCatalogProvider? plugins = null, ControllerTimings? timings = null, KeyCommandCatalog? commands = null)
    {
        _paths = paths;
        _confirmations = confirmations;
        _processes = processes;
        _driver = driver;
        _ui = ui;
        _compositions = compositions;
        _buildIdentity = buildIdentity;
        _journal = journal;
        _targets = targets;
        _coordinator = coordinator;
        _plugins = plugins ?? new UnavailablePluginCatalogProvider();
        _timings = timings ?? ControllerTimings.Default;
        if (commands is not null)
        {
            _commands = commands;
        }
        else
        {
            try
            {
                _commands = KeyCommandCatalog.Load(paths.KeyCommandsFile);
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                CommandCatalogWarning = error.Message;
            }
        }
    }

    public string? CommandCatalogWarning { get; }
    public MutationCoordinator Coordinator => _coordinator;
}
