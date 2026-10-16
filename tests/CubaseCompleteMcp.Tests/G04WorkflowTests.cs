using System.Buffers.Binary;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Broker;
using CubaseCompleteMcp.Core;
using CubaseCompleteMcp.Tests.Fakes;

namespace CubaseCompleteMcp.Tests;

public sealed class G04WorkflowTests
{
    internal static byte[] Wav(double seconds, int channels = 2, int rate = 48000, int bits = 24)
    {
        var blockAlign = channels * bits / 8;
        var dataBytes = (int)Math.Round(seconds * rate) * blockAlign;
        var bytes = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)(rate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), (ushort)bits);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)dataBytes);
        return bytes;
    }

    private static string FixtureDirectory(ControllerHarness harness)
    {
        var directory = Path.Combine(harness.Root, "fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public async Task WFL01_CreateDeliversNameAndObservesTheNewTarget_RenameTouchesOnlyThePlannedTrack()
    {
        using var harness = new ControllerHarness();
        var existing = harness.Driver.AddChannel("Existing");
        harness.Ui.AvailableDialogs.Add(DialogKinds.AddTrack);
        string? typedName = null;
        harness.Ui.OnFillAddTrack = name => { typedName = name; harness.Driver.Select(harness.Driver.AddChannel(name)); return true; };
        var create = await harness.SendAsync("track", new JsonObject { ["action"] = "create", ["track_type"] = "midi", ["name"] = "Lead 한글" });
        Assert.Equal(ResultStatus.Ok, create.Status);
        Assert.Equal("Lead 한글", typedName);
        Assert.Equal("Lead 한글", create.Data!["created"]!["title"]!.GetValue<string>());
        Assert.StartsWith("tgt_", create.Data["target_handle"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Existing", existing.Title);

        var created = harness.Driver.Channels.Last();
        harness.Ui.OnRenameText = name => { harness.Driver.Channels.First(c => c.UniqueId == harness.Driver.SelectedUniqueId).Title = name; return true; };
        var rename = await harness.SendAsync("track", new JsonObject { ["action"] = "rename", ["target_scope"] = "selected", ["name"] = "Lead 2" });
        Assert.Equal(ResultStatus.Ok, rename.Status);
        Assert.Equal("Lead 2", created.Title);
        Assert.Equal("Existing", existing.Title);
        Assert.True(harness.Ui.Actions.IndexOf("activate_cubase") < harness.Ui.Actions.LastIndexOf("rename_text")); // Cubase is brought to front first

        // When Cubase cannot be brought to the front, the rename command is not even sent.
        harness.Ui.CubaseActivates = false;
        var commandsBefore = harness.Driver.WriteCount("command.execute");
        var background = await harness.SendAsync("track", new JsonObject { ["action"] = "rename", ["target_scope"] = "selected", ["name"] = "Lead 3" });
        Assert.Equal("FOREGROUND_NOT_CUBASE", background.Error!.Code);
        Assert.Equal(commandsBefore, harness.Driver.WriteCount("command.execute"));
        Assert.Equal("Lead 2", created.Title);
        harness.Ui.CubaseActivates = true;

        var notSelected = await harness.SendAsync("track", new JsonObject { ["action"] = "rename", ["target_handle"] = await harness.HandleForAsync(existing), ["name"] = "X" });
        Assert.Equal(ErrorCodes.TargetNotSelected, notSelected.Error!.Code);
        Assert.Equal("Existing", existing.Title);
        var controlCharacter = await harness.SendAsync("track", new JsonObject { ["action"] = "create", ["track_type"] = "midi", ["name"] = "bad\u0007" });
        Assert.Equal(ErrorCodes.InvalidArgument, controlCharacter.Error!.Code);

        var noDialog = await harness.SendAsync("track", new JsonObject { ["action"] = "create", ["track_type"] = "instrument", ["name"] = "Ghost" });
        Assert.Equal(ResultStatus.Unverified, noDialog.Status); // the command alone is never reported as a created track
    }

    [Fact]
    public async Task WFL02_ProjectPathIsDeliveredToTheOwnedDialogAndCommandAloneIsNotOk()
    {
        using var harness = new ControllerHarness();
        var directory = FixtureDirectory(harness);
        var path = Path.Combine(directory, "Fixture Song.cpr");
        string? submitted = null;
        harness.Ui.AvailableDialogs.Add(DialogKinds.FileSave);
        harness.Ui.OnSubmitPath = (kind, value) =>
        {
            submitted = value;
            File.WriteAllBytes(value, [1, 2, 3, 4]);
            harness.Process.Title = "Cubase Pro Project - Fixture Song";
            return true;
        };
        var saved = await harness.SendAsync("project", new JsonObject { ["action"] = "save_as", ["path"] = path });
        Assert.Equal(ResultStatus.Ok, saved.Status);
        Assert.Equal(Path.GetFullPath(path), submitted);

        var missingDialog = await harness.SendAsync("project", new JsonObject { ["action"] = "save_as", ["path"] = Path.Combine(directory, "Other.cpr") });
        Assert.Equal(ResultStatus.Unverified, missingDialog.Status);
        Assert.Equal(ErrorCodes.StageTimeout, missingDialog.Error!.Code);
        var relative = await harness.SendAsync("project", new JsonObject { ["action"] = "save_as", ["path"] = "relative.cpr" });
        Assert.Equal(ErrorCodes.InvalidArgument, relative.Error!.Code);
    }

    [Fact]
    public async Task WFL03_OverwriteApprovalIsBoundToFileIdentity()
    {
        using var harness = new ControllerHarness();
        var directory = FixtureDirectory(harness);
        var path = Path.Combine(directory, "Existing.cpr");
        File.WriteAllBytes(path, [9, 9, 9]);
        var exists = await harness.SendAsync("project", new JsonObject { ["action"] = "save_as", ["path"] = path });
        Assert.Equal(ErrorCodes.FileExists, exists.Error!.Code);

        var args = new JsonObject { ["action"] = "save_as", ["path"] = path, ["overwrite"] = true };
        var proposal = await harness.SendAsync("project", (JsonObject)args.DeepClone());
        Assert.Equal(ResultStatus.RequiresConfirmation, proposal.Status);
        Assert.Contains("sha256", proposal.Data!["plan"]!["preconditions"]!["file"]!.ToJsonString(), StringComparison.Ordinal);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]); // someone replaced the file after approval
        var commit = await harness.SendAsync("project", (JsonObject)args.DeepClone(), proposal.Data["confirmation_id"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.FileChanged, commit.Error!.Code);
        Assert.Equal(0, harness.Driver.WriteCount("command.execute"));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WFL04_PluginCatalogReturnsOnlyObservedUids()
    {
        using (var harness = new ControllerHarness())
        {
            var catalog = await harness.SendAsync("plugin", new JsonObject { ["action"] = "catalog" });
            Assert.Equal(ResultStatus.Unsupported, catalog.Status);
            Assert.Equal(ErrorCodes.PluginCatalogUnavailable, catalog.Error!.Code);
            var channel = harness.Driver.AddChannel("Synth");
            harness.Driver.Select(channel);
            var assign = await harness.SendAsync("plugin", new JsonObject { ["action"] = "assign", ["target_scope"] = "selected", ["object_id"] = 5, ["plugin_uid"] = "ABCDEF" });
            Assert.Equal(ErrorCodes.PluginCatalogUnavailable, assign.Error!.Code);
            Assert.Equal(0, harness.Driver.WriteCount("plugin.set"));
        }
        using var withCatalog = new ControllerHarness(new FakePluginCatalog(new PluginCatalogEntry("uid-reverb", "Reverb", "Vendor", "Fx", true)));
        var target = withCatalog.Driver.AddChannel("Bus");
        withCatalog.Driver.Select(target);
        var listed = await withCatalog.SendAsync("plugin", new JsonObject { ["action"] = "catalog" });
        Assert.Equal("uid-reverb", listed.Data!["entries"]![0]!["uid"]!.GetValue<string>());
        var unknown = await withCatalog.SendAsync("plugin", new JsonObject { ["action"] = "assign", ["target_scope"] = "selected", ["object_id"] = 5, ["plugin_uid"] = "uid-invented" });
        Assert.Equal(ErrorCodes.InvalidArgument, unknown.Error!.Code);
    }

    [Theory]
    [InlineData("uid-reverb", 100, ResultStatus.Ok)]
    [InlineData(null, 0, ResultStatus.Unverified)]
    [InlineData("uid-other", 0, ResultStatus.Error)]
    public async Task WFL05_AssignIsJudgedByTheLoadedUid(string? loadedUid, int delayMs, ResultStatus expected)
    {
        var catalog = new FakePluginCatalog(new PluginCatalogEntry("uid-reverb", "Reverb", "Vendor", "Fx", true))
        {
            LoadedUid = loadedUid, LoadDelay = TimeSpan.FromMilliseconds(delayMs)
        };
        using var harness = new ControllerHarness(catalog);
        var target = harness.Driver.AddChannel("Bus");
        harness.Driver.Select(target);
        harness.Driver.BeforeWrite = (operation, _) => { if (operation == "plugin.set") catalog.MarkAssigned(); };
        var result = await harness.ConfirmAndCommitAsync("plugin", new JsonObject { ["action"] = "assign", ["target_scope"] = "selected", ["object_id"] = 5, ["plugin_uid"] = "uid-reverb" });
        Assert.Equal(expected, result.Status);
        Assert.Equal(1, harness.Driver.WriteCount("plugin.set"));
        Assert.Equal("Assigned", harness.Driver.SlotTitle); // the title changed in every case; it never decides the result
    }

    [Theory]
    [InlineData("946051208E29496E804F64A825C8A047", null, ResultStatus.Ok)]
    [InlineData("94605120-8e29-496e-804f-64a825c8a047", null, ResultStatus.Ok)]
    [InlineData("946051208E29496E804F64A825C8A047", "5B38F28281144FFE80285FF7CCF20483", ResultStatus.Error)]
    public async Task WFL05_Api13Host_CatalogAndUidReadBackComeFromTheDriver(string requested, string? hostLoads, ResultStatus expected)
    {
        using var harness = new ControllerHarness();
        harness.Driver.DriverPluginCatalog =
        [
            new JsonObject { ["uid"] = "946051208E29496E804F64A825C8A047", ["name"] = "StudioEQ", ["vendor"] = "Steinberg Media Technologies", ["sub_categories"] = "Fx|EQ" },
            new JsonObject { ["uid"] = "5B38F28281144FFE80285FF7CCF20483", ["name"] = "Compressor", ["vendor"] = "Steinberg Media Technologies", ["sub_categories"] = "Fx|Dynamics" }
        ];
        harness.Driver.LoadedUidOverride = hostLoads;
        var target = harness.Driver.AddChannel("Bus");
        harness.Driver.Select(target);

        var catalog = await harness.SendAsync("plugin", new JsonObject { ["action"] = "catalog" });
        Assert.Equal(ResultStatus.Ok, catalog.Status);
        Assert.Equal("midi_remote_api_1_3_plugin_manager", catalog.Data!["source"]!.GetValue<string>());
        Assert.Equal(2, catalog.Data["entries"]!.AsArray().Count);
        Assert.Equal(7, catalog.Data["slot_object_id"]!.GetValue<long>());

        var result = await harness.ConfirmAndCommitAsync("plugin", new JsonObject { ["action"] = "assign", ["target_scope"] = "selected", ["object_id"] = 7, ["plugin_uid"] = requested });
        Assert.Equal(expected, result.Status);
        Assert.Equal(1, harness.Driver.WriteCount("plugin.set"));
        var verification = result.Data!["verification"]!;
        Assert.Equal("slot_child_unique_name", verification["observation_source"]!.GetValue<string>());
        Assert.Equal(hostLoads ?? "946051208E29496E804F64A825C8A047", verification["observed"]!["slot_plugin_uid"]!.GetValue<string>());
        if (expected == ResultStatus.Error) Assert.Equal("PLUGIN_MISMATCH", result.Error!.Code);
    }

    [Fact]
    public async Task WFL05_Api13Host_UidOutsideTheDriverCatalog_IsRefusedBeforeDispatch()
    {
        using var harness = new ControllerHarness();
        harness.Driver.DriverPluginCatalog = [new JsonObject { ["uid"] = "946051208E29496E804F64A825C8A047", ["name"] = "StudioEQ" }];
        var target = harness.Driver.AddChannel("Bus");
        harness.Driver.Select(target);
        var unknown = await harness.SendAsync("plugin", new JsonObject { ["action"] = "assign", ["target_scope"] = "selected", ["object_id"] = 7, ["plugin_uid"] = "00000000000000000000000000000000" });
        Assert.Equal(ErrorCodes.InvalidArgument, unknown.Error!.Code);
        Assert.Equal(0, harness.Driver.WriteCount("plugin.set"));
    }

    [Fact]
    public async Task WFL05_SlotChangedAfterApproval_IsRefused()
    {
        var catalog = new FakePluginCatalog(new PluginCatalogEntry("uid-reverb", "Reverb", "Vendor", "Fx", true));
        using var harness = new ControllerHarness(catalog);
        var target = harness.Driver.AddChannel("Bus");
        harness.Driver.Select(target);
        var args = new JsonObject { ["action"] = "assign", ["target_scope"] = "selected", ["object_id"] = 5, ["plugin_uid"] = "uid-reverb" };
        var proposal = await harness.SendAsync("plugin", (JsonObject)args.DeepClone());
        Assert.Contains("discarded", proposal.Data!["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        harness.Driver.SlotUid = "slot-now-holds-something";
        var commit = await harness.SendAsync("plugin", (JsonObject)args.DeepClone(), proposal.Data["confirmation_id"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.TargetChanged, commit.Error!.Code);
        Assert.Equal(0, harness.Driver.WriteCount("plugin.set"));
    }

    [Fact]
    public async Task WFL06_WrongStageOrForeignDialog_GetsNoInput()
    {
        using var harness = new ControllerHarness();
        var directory = FixtureDirectory(harness);
        harness.Ui.AvailableDialogs.Add(DialogKinds.FileOpen); // a different stage's dialog is open
        var result = await harness.SendAsync("project", new JsonObject { ["action"] = "save_as", ["path"] = Path.Combine(directory, "A.cpr") });
        Assert.Equal(ErrorCodes.StageTimeout, result.Error!.Code);
        Assert.DoesNotContain(harness.Ui.Actions, action => action.StartsWith("submit:", StringComparison.Ordinal));
        var foreign = await harness.Ui.CancelDialogAsync(new UiDialogRef("dlg-not-ours", DialogKinds.FileSave, "Other app"), CancellationToken.None);
        Assert.False(foreign.Ok);

        // Real ownership policy: a same-titled window of another process is rejected without owner-chain evidence.
        var decision = WindowOwnershipPolicy.Evaluate(new WindowOwnershipEvidence
        {
            CandidateHandle = new IntPtr(1), CandidateProcessId = 99, CubaseProcessId = 42, CubaseIdentityVerified = true,
            OwnerChainProcessId = 99, OwnerChainReachedCubaseTopLevel = false, ExpectedStage = CubaseDialogStage.FileDialog,
            MatchedTitleFragments = ["Save As"], DeclaredTitleFragments = ["Save As"], StructuralRoleConfirmed = true,
            IsVisible = true, HasNonEmptyTitle = true, HasNonEmptyRectangle = true, RectangleInsideCubaseWindow = true
        });
        Assert.False(decision.Accepted);
    }

    [Fact]
    public async Task WFL07_WavExportIsVerifiedByHeaderDataAndLength_NeverReExported()
    {
        using var harness = new ControllerHarness();
        var directory = FixtureDirectory(harness);
        var good = Path.Combine(directory, "mix.wav");
        var perform = harness.Catalog.Find("Audio Export", "Perform Audio Export")!.Id;
        // The exporter writes only when "Perform Audio Export" arrives, under the name the dialog shows.
        Func<byte[]>? render = () => Wav(2.0);
        harness.Driver.Commands[perform] = (_, _) =>
        {
            if (render is not null) File.WriteAllBytes(Path.Combine(harness.Ui.ExportDirectory!, harness.Ui.ExportDialogName + ".wav"), render());
            return true;
        };

        harness.Ui.AvailableDialogs.Add(DialogKinds.ExportAudio);
        var ok = await harness.SendAsync("project", new JsonObject { ["action"] = "export_audio", ["path"] = good, ["expected_duration_seconds"] = 2.0 });
        Assert.Equal(ResultStatus.Ok, ok.Status);
        Assert.Equal(48000, ok.Data!["wav"]!["sample_rate"]!.GetValue<int>());
        // The dialog showed another name: it was typed, the dialog closed and reopened, and read back before exporting.
        Assert.Equal(["export_read:acceptance", "export_name:mix", "export_read:mix"],
            harness.Ui.Actions.Where(action => action.StartsWith("export_", StringComparison.Ordinal)).ToArray());
        Assert.True(File.Exists(good));

        harness.Ui.AvailableDialogs.Add(DialogKinds.ExportAudio);
        render = () => Wav(2.0, bits: 16);
        var wrong = await harness.SendAsync("project", new JsonObject { ["action"] = "export_audio", ["path"] = Path.Combine(directory, "mix16.wav"), ["expected_duration_seconds"] = 2.0 });
        Assert.Equal(ResultStatus.Error, wrong.Status);
        Assert.Equal("EXPORT_FORMAT_MISMATCH", wrong.Error!.Code);

        harness.Ui.AvailableDialogs.Add(DialogKinds.ExportAudio);
        render = null; // the exporter never writes a file
        var performsBefore = harness.Driver.WriteCount("command.execute");
        var timeout = await harness.SendAsync("project", new JsonObject { ["action"] = "export_audio", ["path"] = Path.Combine(directory, "never.wav"), ["expected_duration_seconds"] = 1.0 });
        Assert.Equal(ResultStatus.Unverified, timeout.Status);
        Assert.Equal(performsBefore + 3, harness.Driver.WriteCount("command.execute")); // open, reopen after naming, perform once; never again

        // Settings that do not read back as the requested WAV start nothing and close the dialog.
        harness.Ui.AvailableDialogs.Add(DialogKinds.ExportAudio);
        harness.Ui.ExportFormatMatches = false;
        render = () => Wav(2.0);
        var beforeMismatch = harness.Driver.WriteCount("command.execute");
        var mismatch = await harness.SendAsync("project", new JsonObject { ["action"] = "export_audio", ["path"] = Path.Combine(directory, "mismatch.wav"), ["expected_duration_seconds"] = 2.0 });
        Assert.Equal("EXPORT_SETTINGS_MISMATCH", mismatch.Error!.Code);
        Assert.Equal(ExecutionState.NotApplied, mismatch.ExecutionState);
        Assert.Equal(beforeMismatch + 1, harness.Driver.WriteCount("command.execute")); // only the dialog was opened
        Assert.False(File.Exists(Path.Combine(directory, "mismatch.wav")));

        Assert.True(WavFileValidator.Validate(Wav(2.0), 2.0).Valid);
        Assert.Equal("not_stereo", WavFileValidator.Validate(Wav(2.0, channels: 1), 2.0).Error);
        Assert.Equal("sample_rate_not_48000", WavFileValidator.Validate(Wav(2.0, rate: 44100), 2.0).Error);
        Assert.Equal("duration_out_of_tolerance", WavFileValidator.Validate(Wav(2.1), 2.0).Error);
        Assert.True(WavFileValidator.Validate(Wav(2.015), 2.0).Valid);
        Assert.Equal("empty_data", WavFileValidator.Validate(Wav(0), 0).Error);
        Assert.False(WavFileValidator.Read(new byte[] { 1, 2, 3 }).Valid);
    }

    [Fact]
    public async Task WFL08_DeclinedOverwriteCancelsOnlyTheOwnDialogAndDeletesNothing()
    {
        using var harness = new ControllerHarness();
        var directory = FixtureDirectory(harness);
        var path = Path.Combine(directory, "Race.cpr");
        harness.Ui.AvailableDialogs.Add(DialogKinds.FileSave);
        harness.Ui.OverwritePromptOnSave = true;
        harness.Ui.OnSubmitPath = (_, value) => { File.WriteAllBytes(value, [7]); return true; }; // file appeared meanwhile
        var result = await harness.SendAsync("project", new JsonObject { ["action"] = "save_as", ["path"] = path });
        Assert.Equal(ResultStatus.Error, result.Status);
        Assert.Equal(ErrorCodes.FileExists, result.Error!.Code);
        Assert.False(harness.Ui.LastPromptAnswer);
        Assert.Contains("cancel:" + DialogKinds.FileSave, harness.Ui.Actions);
        Assert.True(File.Exists(path));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(path));
        Assert.Contains(result.Data!["workflow_stages"]!.AsArray(), stage => stage!["stage"]!.GetValue<string>() == "overwrite_declined");
    }

    private static async Task<(ControllerHarness Harness, string DocumentId)> ComposeAsync(bool conductor = true)
    {
        var harness = new ControllerHarness();
        var created = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "create_document", ["name"] = "Song" });
        var documentId = created.Data!["document"]!["id"]!.GetValue<string>();
        var track = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "add_track", ["document_id"] = documentId, ["name"] = "Piano" });
        await harness.SendAsync("midi.compose", new JsonObject
        {
            ["action"] = "add_notes", ["document_id"] = documentId, ["track_id"] = track.Data!["target_id"]!.GetValue<string>(),
            ["events"] = new JsonArray(new JsonObject { ["tick"] = 0, ["duration_ticks"] = 480, ["note"] = 60, ["velocity"] = 100 })
        });
        _ = conductor;
        return (harness, documentId);
    }

    private static void ImportCreatesTracks(ControllerHarness harness) => harness.Ui.OnMidiSubmit = path =>
    {
        var document = StandardMidiFile.Read(File.ReadAllBytes(path));
        for (var index = 0; index < document.Tracks.Count; index++)
            if (!MidiImportCausality.IsConductor(document, index)) harness.Driver.AddChannel(document.Tracks[index].Name);
    };

    [Fact]
    public async Task MIDI01_ExistingSameNamedTrack_IsNeverTakenAsTheImportResult()
    {
        var (harness, documentId) = await ComposeAsync();
        using var _ = harness;
        harness.Driver.AddChannel("Piano");
        var result = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "import_to_cubase", ["document_id"] = documentId });
        Assert.NotEqual(ResultStatus.Ok, result.Status);
        Assert.Equal(ErrorCodes.ImportCausalityUnproven, result.Error!.Code);
        Assert.Equal(MidiVerificationLevels.Submitted, result.Data!["verification_level"]!.GetValue<string>());
    }

    [Fact]
    public async Task MIDI02_UniqueTaggedTargetsAreObservedAndMapped_ButPartsAreNotClaimed()
    {
        var (harness, documentId) = await ComposeAsync();
        using var _ = harness;
        harness.Driver.AddChannel("Piano");
        ImportCreatesTracks(harness);
        var operationId = Guid.NewGuid().ToString();
        var result = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "import_to_cubase", ["document_id"] = documentId }, operationId: operationId);
        Assert.Equal(ResultStatus.Unverified, result.Status);
        Assert.Equal(ExecutionState.Applied, result.ExecutionState);
        Assert.Equal(MidiVerificationLevels.TargetsObserved, result.Data!["verification_level"]!.GetValue<string>());
        var mapping = result.Data["created_tracks"]!.AsArray().Single()!;
        var tag = MidiImportCausality.VerificationTag(operationId);
        Assert.Equal($"Piano [{tag}]", mapping["created_name"]!.GetValue<string>());
        Assert.NotNull(mapping["target_handle"]);
        var storedDocument = harness.Compositions.LoadDocument(documentId, "session-a");
        Assert.Equal("Piano", storedDocument.Tracks[1].Name); // the source composition is not renamed
    }

    [Fact]
    public void MIDI03_ConductorIsOnlyAChannelEventFreeTrackZeroOfAMultiTrackFile()
    {
        MidiCompositionEvent Note() => new() { Id = "e", Type = "note", Tick = 0, DurationTicks = 10, Note = 60, Velocity = 90 };
        var single = new MidiCompositionDocument { Id = "midi_a", Name = "A", Tracks = [new MidiCompositionTrack { Id = "t0", Name = "Only", Events = [Note()] }] };
        Assert.False(MidiImportCausality.IsConductor(single, 0));
        Assert.Single(MidiImportCausality.Plan(single, Guid.NewGuid().ToString()).Tracks);
        var notesInFirst = new MidiCompositionDocument
        {
            Id = "midi_b", Name = "B",
            Tracks = [new MidiCompositionTrack { Id = "t0", Name = "Lead", Events = [Note()] }, new MidiCompositionTrack { Id = "t1", Name = "Bass", Events = [Note()] }]
        };
        var plan = MidiImportCausality.Plan(notesInFirst, Guid.NewGuid().ToString());
        Assert.False(plan.ConductorSkipped);
        Assert.Equal(2, plan.Tracks.Count);
        Assert.Equal("track0_contains_channel_events", plan.ConductorReason);
        var conductor = new MidiCompositionDocument
        {
            Id = "midi_c", Name = "C",
            Tracks = [new MidiCompositionTrack { Id = "t0", Name = "Conductor", Events = [new MidiCompositionEvent { Id = "t", Type = "tempo", Bpm = 120 }] }, new MidiCompositionTrack { Id = "t1", Name = "Bass", Events = [Note()] }]
        };
        Assert.True(MidiImportCausality.Plan(conductor, Guid.NewGuid().ToString()).ConductorSkipped);
    }

    [Fact]
    public async Task MIDI04_ContentRoundtripComparesNotesCcAndTimingOnly()
    {
        var (harness, documentId) = await ComposeAsync();
        using var _ = harness;
        var source = harness.Compositions.LoadDocument(documentId, "session-a");
        var renamed = StandardMidiFile.Read(StandardMidiFile.Write(MidiImportCausality.TaggedCopy(source, MidiImportCausality.Plan(source, Guid.NewGuid().ToString()))));
        Assert.True(MidiImportCausality.Compare(source, renamed)["content_equal"]!.GetValue<bool>());
        renamed.Tracks[1].Events.First(item => item.Type == "note").Note = 61;
        var changed = MidiImportCausality.Compare(source, renamed);
        Assert.False(changed["content_equal"]!.GetValue<bool>());
        Assert.NotEmpty(changed["missing_examples"]!.AsArray());

        var exportPath = Path.Combine(harness.Root, "exported.mid");
        await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "export_file", ["document_id"] = documentId, ["path"] = exportPath });
        var compared = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "compare_file", ["document_id"] = documentId, ["path"] = exportPath });
        Assert.Equal(MidiVerificationLevels.ContentRoundtripVerified, compared.Data!["level"]!.GetValue<string>());
        Assert.NotNull(compared.Data["exported_sha256"]);
    }

    [Fact]
    public async Task MIDI05_CompletedImportIsNotRepeated_UnknownIsNotRetried()
    {
        var (harness, documentId) = await ComposeAsync();
        using var _ = harness;
        ImportCreatesTracks(harness);
        var id = Guid.NewGuid().ToString();
        JsonObject Args() => new() { ["action"] = "import_to_cubase", ["document_id"] = documentId };
        await harness.SendAsync("midi.compose", Args(), operationId: id);
        var again = await harness.SendAsync("midi.compose", Args(), operationId: id);
        Assert.True(again.Data!["replayed_from_journal"]!.GetValue<bool>());
        Assert.Equal(1, harness.Ui.MidiSubmissions);

        harness.Ui.OnMidiSubmit = _ => throw new IOException("UI vanished mid-import");
        var unknownId = Guid.NewGuid().ToString();
        var lost = await harness.SendAsync("midi.compose", Args(), operationId: unknownId);
        Assert.Equal(ExecutionState.Unknown, lost.ExecutionState);
        var retry = await harness.SendAsync("midi.compose", Args(), operationId: unknownId);
        Assert.Equal(ErrorCodes.OutcomeUnknown, retry.Error!.Code);
        Assert.Equal(2, harness.Ui.MidiSubmissions);
    }

    [Fact]
    public async Task MIDI06_OneMenuFallbackAtMost_OptionsAreNotAcceptedWhenRefused()
    {
        var (harness, documentId) = await ComposeAsync();
        using var _ = harness;
        harness.Ui.MidiStageAfterCommand = "none";
        var stuck = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "import_to_cubase", ["document_id"] = documentId });
        Assert.Equal(ErrorCodes.StageTimeout, stuck.Error!.Code);
        Assert.Equal(1, harness.Ui.MenuFallbacks);
        Assert.Equal(0, harness.Ui.MidiSubmissions);

        harness.Ui.MidiStageAfterCommand = "midi_file_dialog";
        harness.Ui.MidiStageAfterSubmit = "import_options";
        var pending = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "import_to_cubase", ["document_id"] = documentId, ["accept_import_options"] = false });
        Assert.Equal("IMPORT_OPTIONS_PENDING", pending.Error!.Code);
        Assert.Equal(0, harness.Ui.OptionsAccepted);
    }

    [Fact]
    public async Task MIDI07_StopImpactNeedsAnExplicitConfirmedPlan()
    {
        var (harness, documentId) = await ComposeAsync();
        using var _ = harness;
        harness.Driver.HostChange("playing", true);
        var refused = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "import_to_cubase", ["document_id"] = documentId });
        Assert.Equal(ErrorCodes.TransportActive, refused.Error!.Code);
        Assert.Equal(0, harness.Driver.WriteCount("transport.set"));
        var args = new JsonObject { ["action"] = "import_to_cubase", ["document_id"] = documentId, ["stop_transport"] = true };
        var proposal = await harness.SendAsync("midi.compose", (JsonObject)args.DeepClone());
        Assert.Equal(ResultStatus.RequiresConfirmation, proposal.Status);
        Assert.Contains("Stop Cubase transport", proposal.Data!["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, harness.Driver.WriteCount("transport.set"));
        ImportCreatesTracks(harness);
        await harness.SendAsync("midi.compose", (JsonObject)args.DeepClone(), proposal.Data["confirmation_id"]!.GetValue<string>());
        Assert.Equal(1, harness.Driver.WriteCount("transport.set"));
        Assert.False(harness.Driver.State["playing"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MIDI06_WholeImportIsBoundedByTheWorkflowBudget()
    {
        var timings = ControllerHarness.FastTimings with { MidiImportTotal = TimeSpan.FromMilliseconds(400) };
        using var harness = new ControllerHarness(timings: timings);
        var created = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "create_document", ["name"] = "Budget" });
        var documentId = created.Data!["document"]!["id"]!.GetValue<string>();
        await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "add_track", ["document_id"] = documentId, ["name"] = "Keys" });
        harness.Ui.SubmitDelay = TimeSpan.FromSeconds(5); // the file dialog never finishes
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "import_to_cubase", ["document_id"] = documentId });
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"import took {clock.Elapsed}");
        Assert.Equal(ExecutionState.Unknown, result.ExecutionState); // dispatched, then stopped by the budget; never retried
        Assert.Equal(1, harness.Ui.MidiSubmissions);
    }

    [Theory]
    [InlineData("Do you want to activate the project?", true)]
    [InlineData("프로젝트를 활성화하시겠습니까?", true)]
    [InlineData("Do you want to save this project?", false)]
    public async Task WFL02_OpenAnswersOnlyTheActivationQuestion(string promptText, bool answered)
    {
        using var harness = new ControllerHarness();
        var directory = FixtureDirectory(harness);
        var path = Path.Combine(directory, "Song.cpr");
        File.WriteAllBytes(path, [1, 2, 3]);
        harness.Ui.AvailableDialogs.Add(DialogKinds.FileOpen);
        harness.Ui.AvailableDialogs.Add(DialogKinds.CubasePrompt);
        harness.Ui.PromptText = promptText;
        harness.Ui.OnDefaultButton = () => harness.Process.Title = "Cubase Pro Project - Song";
        var result = await harness.ConfirmAndCommitAsync("project", new JsonObject { ["action"] = "open", ["path"] = path });
        Assert.Equal(answered ? ResultStatus.Ok : ResultStatus.Unverified, result.Status);
        Assert.Equal(answered, harness.Ui.Actions.Contains("default_button:" + promptText));
        if (!answered) Assert.Equal(promptText, result.Data!["pending_user_dialog"]!.GetValue<string>());
    }
}
