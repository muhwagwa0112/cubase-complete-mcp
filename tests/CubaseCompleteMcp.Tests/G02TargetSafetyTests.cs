using System.Text.Json.Nodes;
using CubaseCompleteMcp.Broker;
using CubaseCompleteMcp.Core;
using CubaseCompleteMcp.Tests.Fakes;

namespace CubaseCompleteMcp.Tests;

public sealed class G02TargetSafetyTests
{
    [Fact]
    public async Task TGT01_HandleReadAndWriteAddressTheSameChannel()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        var b = harness.Driver.AddChannel("B");
        harness.Driver.Select(a);
        var handleB = await harness.HandleForAsync(b);
        var write = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handleB, ["parameter"] = "volume", ["value"] = 0.8 });
        Assert.Equal(ResultStatus.Ok, write.Status);
        Assert.Equal(0.8, b.Parameters["volume"], 6);
        Assert.Equal(0.5, a.Parameters["volume"], 6);
        var read = await harness.SendAsync("mixer", new JsonObject { ["action"] = "get", ["target_handle"] = handleB, ["parameter"] = "volume" });
        Assert.Equal(0.8, read.Data!["process_value"]!.GetValue<double>(), 6);
        Assert.Equal(b.UniqueId, read.Data["target"]!["unique_id"]!.GetValue<string>());
        Assert.Equal(b.UniqueId, harness.Driver.Writes.Single().Arguments["target"]!["unique_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task TGT02_SAFE01_ApprovedDeleteOfAWithBSelectedAtCommit_NeverDeletesB()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        var b = harness.Driver.AddChannel("B");
        harness.Driver.Select(a);
        var args = new JsonObject { ["action"] = "delete", ["target_scope"] = "selected" };
        var proposal = await harness.SendAsync("track", (JsonObject)args.DeepClone());
        Assert.Equal(ResultStatus.RequiresConfirmation, proposal.Status);
        Assert.Contains(a.UniqueId, proposal.Data!["plan"]!.ToJsonString(), StringComparison.Ordinal);
        harness.Driver.Select(b); // the user selects another track before committing
        var commit = await harness.SendAsync("track", (JsonObject)args.DeepClone(), proposal.Data["confirmation_id"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.TargetChanged, commit.Error!.Code);
        Assert.Equal(ExecutionState.NotStarted, commit.ExecutionState);
        Assert.Equal(0, harness.Driver.WriteCount("command.execute"));
        Assert.Contains(harness.Driver.Channels, channel => channel.UniqueId == b.UniqueId);

        // Even if the broker check were bypassed, the driver guard compares the selection in the same callback.
        harness.Driver.Commands[harness.CommandId("Project", "Remove Selected Tracks")] = (_, driver) => { driver.Channels.RemoveAll(c => c.UniqueId == driver.SelectedUniqueId); return true; };
        var direct = await harness.Driver.SendAsync("command.execute", new JsonObject
        {
            ["id"] = harness.CommandId("Project", "Remove Selected Tracks"), ["requires_target"] = true,
            ["expected_selected"] = new JsonObject { ["unique_id"] = a.UniqueId, ["runtime_id"] = a.RuntimeId }
        });
        Assert.False(direct.Ok);
        Assert.Equal(ErrorCodes.TargetChanged, direct.Error!.Code);
        Assert.Contains(harness.Driver.Channels, channel => channel.UniqueId == b.UniqueId);
    }

    [Fact]
    public async Task TGT03_BankReorder_FollowsIdentityNotIndex()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A", 0);
        var b = harness.Driver.AddChannel("B", 1);
        var handleB = await harness.HandleForAsync(b);
        (a.BankIndex, b.BankIndex) = (1, 0); // Cubase reordered the bank
        var write = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handleB, ["parameter"] = "pan", ["value"] = 0.1 });
        Assert.Equal(ResultStatus.Ok, write.Status);
        Assert.Equal(0.1, b.Parameters["pan"], 6);
        Assert.Equal(0.5, a.Parameters["pan"], 6);

        b.RuntimeId = 9999; // the object was replaced: identity can no longer be proven
        var stale = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handleB, ["parameter"] = "pan", ["value"] = 0.9 });
        Assert.NotEqual(ResultStatus.Ok, stale.Status);
        Assert.Equal(0.1, b.Parameters["pan"], 6);
        Assert.Equal(0.5, a.Parameters["pan"], 6);
    }

    [Fact]
    public async Task TGT04_ProjectOrDriverEpochChange_InvalidatesHandlesWithZeroWrites()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        var handle = await harness.HandleForAsync(a);
        harness.Process.Title = "Cubase Pro Project - Other Song";
        var afterProject = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.9 });
        Assert.Equal(ErrorCodes.StaleTarget, afterProject.Error!.Code);

        var fresh = await harness.HandleForAsync(a);
        harness.Driver.Reconnect();
        var afterReconnect = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = fresh, ["parameter"] = "volume", ["value"] = 0.9 });
        Assert.Equal(ErrorCodes.StaleTarget, afterReconnect.Error!.Code);
        Assert.Equal(0, harness.Driver.WriteCount("mixer.set"));

        // Store-level: expiry, other sessions and epochs.
        var now = DateTimeOffset.UtcNow;
        var store = new TargetHandleStore(TimeSpan.FromMinutes(5), () => now);
        var epochs = new HostEpochs("b", 1, "p");
        var issued = store.Issue("s1", epochs, new TargetIdentity(TargetKinds.MixerChannel, "mixer", "u", 1, 0, "A"));
        Assert.Equal(ErrorCodes.TargetScopeMismatch, store.Resolve(issued.Handle, "s2", epochs).ErrorCode);
        Assert.True(store.Resolve(issued.Handle, "s1", epochs).Ok);
        now += TimeSpan.FromMinutes(6);
        Assert.Equal(ErrorCodes.StaleTarget, store.Resolve(issued.Handle, "s1", epochs).ErrorCode);
    }

    [Fact]
    public async Task TGT05_ConflictingAndLegacySelectors_AreRejected_MidiTrackIdUnchanged()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        var handle = await harness.HandleForAsync(a);
        var legacy = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["track_id"] = "3", ["parameter"] = "volume", ["value"] = 0.5 });
        Assert.Equal(ErrorCodes.LegacyTargetAmbiguous, legacy.Error!.Code);
        var conflicting = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["bank_index"] = 0, ["parameter"] = "volume", ["value"] = 0.5 });
        Assert.Equal(ErrorCodes.TargetScopeMismatch, conflicting.Error!.Code);
        Assert.Equal(0, harness.Driver.WriteCount("mixer.set"));

        var document = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "create_document", ["name"] = "Song" });
        var documentId = document.Data!["document"]!["id"]!.GetValue<string>();
        var track = await harness.SendAsync("midi.compose", new JsonObject { ["action"] = "add_track", ["document_id"] = documentId, ["name"] = "Piano" });
        Assert.Equal(ResultStatus.Ok, track.Status);
        var trackId = track.Data!["target_id"]!.GetValue<string>();
        var notes = await harness.SendAsync("midi.compose", new JsonObject
        {
            ["action"] = "add_notes", ["document_id"] = documentId, ["track_id"] = trackId,
            ["events"] = new JsonArray(new JsonObject { ["tick"] = 0, ["duration_ticks"] = 480, ["note"] = 60, ["velocity"] = 90 })
        });
        Assert.Equal(ResultStatus.Ok, notes.Status); // cubase_midi track_id is a document ID and still works
    }

    [Fact]
    public async Task TGT06_TrackListSeparatesChannelsAndReportsBankScope()
    {
        using var harness = new ControllerHarness();
        for (var i = 0; i < 64; i++) harness.Driver.AddChannel("Ch" + i, i);
        harness.Driver.AddChannel("Outside", 70);
        var list = await harness.SendAsync("track", new JsonObject { ["action"] = "list" });
        Assert.Equal(64, list.Data!["available_channel_count"]!.GetValue<int>());
        Assert.True(list.Data["bank_truncated"]!.GetValue<bool>());
        Assert.Equal("mixer_bank", list.Data["scope"]!.GetValue<string>());
        Assert.All(list.Data["channels"]!.AsArray(), node => Assert.StartsWith("tgt_", node!["handle"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.DoesNotContain(list.Data["channels"]!.AsArray(), node => node!["title"]!.GetValue<string>() == "Outside");
    }

    [Fact]
    public async Task TGT07_AmbiguousLockedOrInvalidParameters_AreRefusedWithoutFallback()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        harness.Driver.Select(a);
        var handle = await harness.HandleForAsync(a);
        a.Locked = true;
        var locked = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.2 });
        Assert.Equal(ErrorCodes.ParameterLocked, locked.Error!.Code);
        a.Locked = false;
        a.DuplicateVolumeTitles = 2;
        var ambiguous = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.2 });
        Assert.Equal(ErrorCodes.ParameterAmbiguous, ambiguous.Error!.Code);
        var nonFinite = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "pan" });
        Assert.Equal(ErrorCodes.InvalidArgument, nonFinite.Error!.Code);
        var noTarget = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["parameter"] = "volume", ["value"] = 0.2 });
        Assert.Equal(ErrorCodes.TargetRequired, noTarget.Error!.Code);
        Assert.Equal(0.5, a.Parameters["volume"], 6);
        Assert.DoesNotContain(harness.Driver.Writes, write => write.Arguments.ContainsKey("expected_selected")); // no selected-track fallback
    }

    [Fact]
    public async Task TGT08_TwoClientsRace_OneLeaseAtATimeAndObserversDoNotDeadlock()
    {
        using var harness = new ControllerHarness(timings: ControllerHarness.FastTimings);
        var a = harness.Driver.AddChannel("A");
        var b = harness.Driver.AddChannel("B");
        var handleA = await harness.HandleForAsync(a, "client-1");
        var handleB = await harness.HandleForAsync(b, "client-2");
        harness.Driver.CallbackDelay = TimeSpan.FromMilliseconds(150);
        var active = 0;
        var maxActive = 0;
        harness.Driver.BeforeWrite = (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            Interlocked.Decrement(ref active);
        };
        using var barrier = new Barrier(2);
        var first = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await harness.SendAsync("transport", new JsonObject { ["action"] = "play" }, session: "client-1");
        });
        var second = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handleB, ["parameter"] = "volume", ["value"] = 0.9 }, session: "client-2");
        });
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Equal(ResultStatus.Ok, result.Status));
        Assert.Equal(1, maxActive);
        Assert.Equal(2, harness.Coordinator.LeasesGranted);
        Assert.Equal(0.5, a.Parameters["volume"], 6);
        Assert.Equal(0.9, b.Parameters["volume"], 6);
        _ = handleA;

        // Busy: a full queue is refused before dispatch.
        var coordinator = new MutationCoordinator(TimeSpan.FromMilliseconds(50), maximumQueue: 1);
        var hold = new TaskCompletionSource();
        var holder = coordinator.RunExclusiveAsync("hold", async () => { await hold.Task; return 1; }, _ => -1, CancellationToken.None);
        var waiter = await coordinator.RunExclusiveAsync("wait", () => Task.FromResult(2), refusal => refusal.ErrorCode == ErrorCodes.Busy ? -2 : -3, CancellationToken.None);
        Assert.Equal(-2, waiter);
        hold.SetResult();
        Assert.Equal(1, await holder);
    }

    [Fact]
    public void SAFE02_CanonicalHash_IgnoresKeyOrderButNotArrayOrder()
    {
        var a = JsonNode.Parse("""{"b":1,"a":{"y":[1,2],"x":"s"},"confirmation_id":"c1"}""")!;
        var b = JsonNode.Parse("""{"a":{"x":"s","y":[1,2]},"b":1.0,"confirmation_id":"c2"}""")!;
        var c = JsonNode.Parse("""{"a":{"x":"s","y":[2,1]},"b":1}""")!;
        Assert.Equal(CanonicalJson.Hash("d", a), CanonicalJson.Hash("d", b));
        Assert.NotEqual(CanonicalJson.Hash("d", a), CanonicalJson.Hash("d", c));
        Assert.Throws<ArgumentException>(() => CanonicalJson.Hash("d", new JsonObject { ["n"] = double.NaN }));
    }

    [Fact]
    public void SAFE03_SAFE04_MismatchKeepsApproval_ExpiryAndSingleUse()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new ConfirmationStore(TimeSpan.FromMinutes(5), () => now);
        var args = new JsonObject { ["action"] = "delete", ["target_scope"] = "selected" };
        var proposal = store.Prepare("s", "track.delete", args, "delete");
        var mismatch = store.Validate(proposal.ConfirmationId, "s", "track.delete", new JsonObject { ["action"] = "delete", ["target_handle"] = "x" });
        Assert.Equal(ErrorCodes.ConfirmationMismatch, mismatch.ErrorCode);
        Assert.True(store.Validate(proposal.ConfirmationId, "s", "track.delete", args).Ok);
        Assert.True(store.TryConsume(proposal.ConfirmationId));
        Assert.False(store.TryConsume(proposal.ConfirmationId));
        var expiring = store.Prepare("s", "track.delete", args, "delete");
        now += TimeSpan.FromMinutes(6);
        Assert.False(store.Validate(expiring.ConfirmationId, "s", "track.delete", args).Ok);
    }

    [Fact]
    public async Task SAFE05_UnclassifiedCommand_IsConfirmAndNeverDispatchedWithoutApproval()
    {
        using var harness = new ControllerHarness();
        var mystery = harness.Catalog.Find("Edit", "Mystery Command")!;
        Assert.False(CapabilityRegistry.TryClassifyCommand(mystery, out _));
        Assert.Equal(RiskLevel.Confirm, CapabilityRegistry.ClassifyCommand(mystery));
        var descriptor = CapabilityRegistry.DescribeCommand(mystery, false);
        Assert.False(descriptor.Classified);
        Assert.Equal(RiskLevel.Confirm, descriptor.Risk);
        var response = await harness.SendAsync("command", new JsonObject { ["action"] = "execute", ["id"] = mystery.Id });
        Assert.Equal(ResultStatus.RequiresConfirmation, response.Status);
        Assert.Equal(0, harness.Driver.WriteCount("command.execute"));
        Assert.Equal(RiskLevel.Confirm, SafetyPolicy.Classify("something.unknown", null));
        var unknownAction = await harness.SendAsync("transport", new JsonObject { ["action"] = "rewind_everything" });
        Assert.Equal(ErrorCodes.InvalidAction, unknownAction.Error!.Code);
    }

    [Fact]
    public async Task SAFE06_BatchIsFullyPlannedAndApprovedBeforeAnyChildRuns()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        harness.Driver.Select(a);
        var handle = await harness.HandleForAsync(a);
        var batch = new JsonObject
        {
            ["operations"] = new JsonArray(
                new JsonObject { ["id"] = "vol", ["operation"] = "mixer", ["arguments"] = new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.2 } },
                new JsonObject { ["id"] = "del", ["operation"] = "track", ["arguments"] = new JsonObject { ["action"] = "delete", ["target_scope"] = "selected" } })
        };
        var proposal = await harness.SendAsync("batch", (JsonObject)batch.DeepClone());
        Assert.Equal(ResultStatus.RequiresConfirmation, proposal.Status);
        Assert.Empty(harness.Driver.Writes);
        Assert.Equal(2, proposal.Data!["plan"]!["children"]!.AsArray().Count);
    }

    [Fact]
    public async Task SAFE07_UnrelatedChangesKeepApproval_RelevantChangesInvalidateIt()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        harness.Driver.Select(a);
        harness.Driver.Commands[harness.CommandId("Project", "Remove Selected Tracks")] = (_, driver) => { driver.Channels.RemoveAll(c => c.UniqueId == driver.SelectedUniqueId); return true; };
        var args = new JsonObject { ["action"] = "delete", ["target_scope"] = "selected" };
        var proposal = await harness.SendAsync("track", (JsonObject)args.DeepClone());
        harness.Driver.HostChange("metronome", true); // unrelated state change
        a.Parameters["volume"] = 0.1; // unrelated parameter change on the same target
        var commit = await harness.SendAsync("track", (JsonObject)args.DeepClone(), proposal.Data!["confirmation_id"]!.GetValue<string>());
        Assert.Equal(ResultStatus.Ok, commit.Status);
        Assert.DoesNotContain(harness.Driver.Channels, channel => channel.UniqueId == a.UniqueId);
    }
}
