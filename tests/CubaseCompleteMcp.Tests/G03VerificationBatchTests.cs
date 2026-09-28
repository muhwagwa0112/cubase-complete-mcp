using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;
using CubaseCompleteMcp.Tests.Fakes;

namespace CubaseCompleteMcp.Tests;

public sealed class G03VerificationBatchTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(250)]
    [InlineData(1000)]
    public async Task VER01_DelayedTransportCallback_DispatchOnceOkOnlyAfterFreshObservation(int delayMs)
    {
        using var harness = new ControllerHarness();
        harness.Driver.CallbackDelay = TimeSpan.FromMilliseconds(delayMs);
        var response = await harness.SendAsync("transport", new JsonObject { ["action"] = "play" });
        Assert.Equal(ResultStatus.Ok, response.Status);
        Assert.Equal(ExecutionState.Applied, response.ExecutionState);
        Assert.Equal(1, harness.Driver.WriteCount("transport.set"));
        var verification = response.Data!["verification"]!;
        Assert.True(verification["observed"]!["state_revision"]!.GetValue<long>() > verification["observed"]!["dispatch_revision"]!.GetValue<long>());
    }

    [Fact]
    public async Task VER02_NoCallback_UnverifiedWithinDeadlineAndNoRepeatedWrite()
    {
        using var harness = new ControllerHarness();
        harness.Driver.CallbackDelay = null;
        var started = DateTime.UtcNow;
        var response = await harness.SendAsync("transport", new JsonObject { ["action"] = "play" });
        Assert.Equal(ResultStatus.Unverified, response.Status);
        Assert.Equal(ErrorCodes.PostconditionTimeout, response.Error!.Code);
        Assert.Equal(1, harness.Driver.WriteCount("transport.set"));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));

        var channel = harness.Driver.AddChannel("X");
        var handle = await harness.HandleForAsync(channel);
        var parameter = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.9 });
        Assert.Equal(ResultStatus.Unverified, parameter.Status);
        Assert.Equal(1, harness.Driver.WriteCount("mixer.set"));
    }

    [Fact]
    public async Task VER03_MixerVerificationReadsTheWrittenTarget()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        var b = harness.Driver.AddChannel("B");
        harness.Driver.Select(a);
        var handleB = await harness.HandleForAsync(b);
        var response = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handleB, ["parameter"] = "volume", ["value"] = 0.7 });
        Assert.Equal(ResultStatus.Ok, response.Status);
        var reads = harness.Driver.Reads.Where(read => read == "mixer.get").Count();
        Assert.True(reads >= 1);
        Assert.Equal(b.UniqueId, response.Data!["verification"]!["observed"]!["target"]!["unique_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task VER04_FreshNoOp_IsOkWithoutMutation()
    {
        using var harness = new ControllerHarness();
        var channel = harness.Driver.AddChannel("A");
        var handle = await harness.HandleForAsync(channel);
        var response = await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.5 });
        Assert.Equal(ResultStatus.Ok, response.Status);
        Assert.Equal(ExecutionState.NotApplied, response.ExecutionState);
        Assert.False(response.Data!["mutation_sent"]!.GetValue<bool>());
        var transport = await harness.SendAsync("transport", new JsonObject { ["action"] = "cycle", ["enabled"] = false });
        Assert.Equal(ResultStatus.Ok, transport.Status);
        Assert.Equal(ExecutionState.NotApplied, transport.ExecutionState);
    }

    [Fact]
    public async Task VER05_RecordStateUnknown_IsRefused_ExplicitStopPulsesAtMostOnce()
    {
        using var harness = new ControllerHarness();
        harness.Driver.Reconnect(); // new epoch: nothing reported yet
        var args = new JsonObject { ["action"] = "record_stop" };
        var proposal = await harness.SendAsync("transport", (JsonObject)args.DeepClone());
        Assert.Equal(ResultStatus.RequiresConfirmation, proposal.Status);
        var refused = await harness.SendAsync("transport", (JsonObject)args.DeepClone(), proposal.Data!["confirmation_id"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.StateUnknown, refused.Error!.Code);
        Assert.Equal(ExecutionState.NotStarted, refused.ExecutionState);

        harness.Driver.HostChange("recording", true);
        var stop = await harness.ConfirmAndCommitAsync("transport", new JsonObject { ["action"] = "record_stop" });
        Assert.Equal(ResultStatus.Ok, stop.Status);
        Assert.False(harness.Driver.State["recording"]!.GetValue<bool>());
        Assert.Equal(2, harness.Driver.WriteCount("transport.set")); // one refused before dispatch, one pulse
    }

    [Fact]
    public async Task VER06_TempoToleranceAndTimeFormat()
    {
        using var harness = new ControllerHarness();
        var tempo = await harness.SendAsync("transport", new JsonObject { ["action"] = "tempo", ["bpm"] = 128.004 });
        Assert.Equal(ResultStatus.Ok, tempo.Status);
        Assert.Equal("abs(bpm) <= 0.01", tempo.Data!["verification"]!["tolerance"]!.GetValue<string>());
        var ambiguous = await harness.SendAsync("transport", new JsonObject { ["action"] = "position", ["value"] = 12.5 });
        Assert.Equal(ErrorCodes.InvalidArgument, ambiguous.Error!.Code);
        var wrongFormat = await harness.SendAsync("transport", new JsonObject { ["action"] = "position", ["position_text"] = "0:00:05.000", ["time_format"] = "Seconds" });
        Assert.Equal(ErrorCodes.UnsupportedTimeFormat, wrongFormat.Error!.Code);
        var position = await harness.SendAsync("transport", new JsonObject { ["action"] = "position", ["position_text"] = "005.001.01.000", ["time_format"] = "Bars+Beats" });
        Assert.Equal(ResultStatus.Ok, position.Status);
        Assert.Equal(PostconditionVerifier.CanonicalTimeText("5.1.1.0"), PostconditionVerifier.CanonicalTimeText("005.001.01.000"));
    }

    private static JsonObject Op(string id, string operation, JsonObject arguments, params string[]? dependsOn)
    {
        var item = new JsonObject { ["id"] = id, ["operation"] = operation, ["arguments"] = arguments };
        if (dependsOn is not null) item["depends_on"] = new JsonArray(dependsOn.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return item;
    }

    [Theory]
    [InlineData("""{"operations":[]}""")]
    [InlineData("""{"operations":[{"operation":"batch","arguments":{}}]}""")]
    [InlineData("""{"operations":[{"id":"a","operation":"transport","arguments":{"action":"play"}},{"id":"a","operation":"transport","arguments":{"action":"stop"}}]}""")]
    [InlineData("""{"operations":[{"id":"a","operation":"transport","arguments":{"action":"play"},"depends_on":["b"]},{"id":"b","operation":"transport","arguments":{"action":"stop"}}]}""")]
    [InlineData("""{"operations":[{"operation":"nope","arguments":{}}]}""")]
    [InlineData("""{"operations":[{"operation":"transport","arguments":{"action":"play"}}],"timeout_ms":999999}""")]
    public async Task BAT02_InvalidBatches_AreRefusedWithZeroMutation(string json)
    {
        using var harness = new ControllerHarness();
        var response = await harness.SendAsync("batch", JsonNode.Parse(json)!.AsObject());
        Assert.NotEqual(ResultStatus.Ok, response.Status);
        Assert.Equal(ExecutionState.NotStarted, response.ExecutionState);
        Assert.Empty(harness.Driver.Writes);
        var big = new JsonArray(Enumerable.Range(0, 33).Select(i => (JsonNode?)new JsonObject { ["operation"] = "transport", ["arguments"] = new JsonObject { ["action"] = "get" } }).ToArray());
        Assert.Throws<PlanException>(() => BatchPlanner.Plan(new JsonObject { ["operations"] = big }));
    }

    [Fact]
    public async Task BAT03_AppliedButUnverifiedChild_StopsByDefault()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        var handle = await harness.HandleForAsync(a);
        harness.Driver.Override = (operation, args) => operation == "mixer.set" && args["display_value"] is not null
            ? new CubaseCompleteMcp.Broker.DriverReply(true, new JsonObject
            {
                ["mutation_sent"] = true, ["before"] = 0.5, ["after"] = 0.6, ["requested_display_value"] = "-3 dB", ["parameter_tag"] = 0
            }, null, false, 5, true, harness.Driver.Epoch)
            : null;
        var response = await harness.SendAsync("batch", new JsonObject
        {
            ["operations"] = new JsonArray(
                Op("disp", "mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["display_value"] = "-3 dB" }, null),
                Op("next", "transport", new JsonObject { ["action"] = "tempo", ["bpm"] = 90 }, []))
        });
        Assert.Equal(ResultStatus.Unverified, response.Status);
        Assert.Equal("unverified_predecessor", response.Data!["stop_reason"]!.GetValue<string>());
        Assert.Equal(0, harness.Driver.WriteCount("transport.set")); // even an independent child does not run by default
        Assert.Equal(1, response.Data["not_started_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task BAT04_ContinueOnUnverified_OnlyRunsIndependentChildren()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        var b = harness.Driver.AddChannel("B");
        var handleA = await harness.HandleForAsync(a);
        var handleB = await harness.HandleForAsync(b);
        // A display_value write that the host formats differently is applied but not verifiable.
        harness.Driver.Override = (operation, args) => operation == "mixer.set" && args["display_value"] is not null
            ? new CubaseCompleteMcp.Broker.DriverReply(true, new JsonObject
            {
                ["mutation_sent"] = true, ["before"] = 0.5, ["after"] = 0.6, ["requested_display_value"] = "-3 dB", ["parameter_tag"] = 0,
                ["target"] = new JsonObject { ["unique_id"] = a.UniqueId }
            }, null, false, 5, true, harness.Driver.Epoch)
            : null;
        var response = await harness.SendAsync("batch", new JsonObject
        {
            ["continue_on_unverified"] = true,
            ["operations"] = new JsonArray(
                Op("disp", "mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handleA, ["parameter"] = "volume", ["display_value"] = "-3 dB" }, null),
                Op("dependent", "mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handleA, ["parameter"] = "pan", ["value"] = 0.2 }, null),
                Op("independent", "mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handleB, ["parameter"] = "pan", ["value"] = 0.3 }))
        });
        Assert.Equal(ResultStatus.Unverified, response.Status);
        Assert.Equal(0.5, a.Parameters["pan"], 6); // dependent child never ran
        Assert.Equal(0.3, b.Parameters["pan"], 6); // independent child ran
        Assert.Contains(response.Data!["responses"]!.AsArray(), node => node!["skipped"]?.GetValue<string>() == "dependency_not_verified");
    }

    [Fact]
    public async Task BAT05_UnknownChild_AlwaysStopsEvenWithContinue()
    {
        using var harness = new ControllerHarness();
        harness.Driver.LoseReplyAfterApply = true;
        var response = await harness.SendAsync("batch", new JsonObject
        {
            ["continue_on_unverified"] = true,
            ["operations"] = new JsonArray(Op("play", "transport", new JsonObject { ["action"] = "play" }, null),
                Op("other", "transport", new JsonObject { ["action"] = "tempo", ["bpm"] = 100 }))
        });
        Assert.Equal("outcome_unknown", response.Data!["outcome"]!.GetValue<string>());
        Assert.Equal("outcome_unknown", response.Data["stop_reason"]!.GetValue<string>());
        Assert.Equal(1, harness.Driver.WriteCount("transport.set"));
    }

    [Fact]
    public async Task BAT06_FailureAfterAppliedPrefix_IsErrorPartiallyAppliedWithExactCounts()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        var handle = await harness.HandleForAsync(a);
        var response = await harness.SendAsync("batch", new JsonObject
        {
            ["operations"] = new JsonArray(
                Op("v", "mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.1 }, null),
                Op("bad", "transport", new JsonObject { ["action"] = "position", ["position_text"] = "1", ["time_format"] = "Samples" }, null),
                Op("never", "transport", new JsonObject { ["action"] = "play" }, null))
        });
        Assert.Equal(ResultStatus.Error, response.Status);
        Assert.Equal("partially_applied", response.Data!["outcome"]!.GetValue<string>());
        Assert.Equal(3, response.Data["requested_count"]!.GetValue<int>());
        Assert.Equal(2, response.Data["dispatched_count"]!.GetValue<int>());
        Assert.Equal(1, response.Data["verified_count"]!.GetValue<int>());
        Assert.Equal(1, response.Data["failed_count"]!.GetValue<int>());
        Assert.Equal(1, response.Data["not_started_count"]!.GetValue<int>());
        Assert.Equal(1, response.Data["stopped_after_index"]!.GetValue<int>());
    }

    [Fact]
    public async Task BAT07_ResubmittingAFinishedBatch_NeverReappliesThePrefix_UnknownBlocksResume()
    {
        using var harness = new ControllerHarness();
        var a = harness.Driver.AddChannel("A");
        var handle = await harness.HandleForAsync(a);
        var id = Guid.NewGuid().ToString();
        JsonObject Batch() => new()
        {
            ["operations"] = new JsonArray(
                Op("v", "mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.1 }, null),
                Op("bad", "transport", new JsonObject { ["action"] = "position", ["position_text"] = "1", ["time_format"] = "Samples" }, null))
        };
        await harness.SendAsync("batch", Batch(), operationId: id);
        var writes = harness.Driver.Writes.Count;
        var again = await harness.SendAsync("batch", Batch(), operationId: id);
        Assert.True(again.Data!["replayed_from_journal"]!.GetValue<bool>());
        Assert.Equal(writes, harness.Driver.Writes.Count);
        var resume = await harness.SendAsync("batch", new JsonObject { ["resume_operation_id"] = id });
        Assert.Equal(ResultStatus.Ok, resume.Status);
        Assert.False(resume.Data!["resume_supported"]!.GetValue<bool>());
        Assert.Contains("v", resume.Data["completed_children"]!.ToJsonString(), StringComparison.Ordinal);

        harness.Driver.LoseReplyAfterApply = true;
        var unknownId = Guid.NewGuid().ToString();
        await harness.SendAsync("batch", new JsonObject
        {
            ["operations"] = new JsonArray(Op("p", "transport", new JsonObject { ["action"] = "play" }, null), Op("q", "transport", new JsonObject { ["action"] = "tempo", ["bpm"] = 99 }, null))
        }, operationId: unknownId);
        var blocked = await harness.SendAsync("batch", new JsonObject { ["resume_operation_id"] = unknownId });
        Assert.NotEmpty(blocked.Data!["blocked_by_unknown_children"]!.AsArray());
    }

    [Fact]
    public async Task BAT08_ProtocolMismatchIsRefusedBeforeDispatch_TimeoutStopsTheBatch()
    {
        using var harness = new ControllerHarness();
        var mismatch = await harness.SendAsync("batch", new JsonObject
        {
            ["operations"] = new JsonArray(Op("p", "transport", new JsonObject { ["action"] = "play" }, null))
        }, protocol: 1);
        Assert.Equal(ErrorCodes.BrokerProtocolMismatch, mismatch.Error!.Code);
        harness.Driver.ContractVersion = 1;
        var oldDriver = await harness.SendAsync("transport", new JsonObject { ["action"] = "play" });
        Assert.Equal(ErrorCodes.DriverContractMismatch, oldDriver.Error!.Code);
        harness.Driver.ContractVersion = CubaseConstants.DriverContractVersion;
        Assert.Equal(0, harness.Driver.WriteCount("transport.set"));

        harness.Driver.CallbackDelay = TimeSpan.FromMilliseconds(400);
        var timed = await harness.SendAsync("batch", new JsonObject
        {
            ["timeout_ms"] = 200,
            ["operations"] = new JsonArray(Op("p", "transport", new JsonObject { ["action"] = "play" }, null),
                Op("t", "transport", new JsonObject { ["action"] = "tempo", ["bpm"] = 99 }, null))
        });
        Assert.NotEqual(ResultStatus.Ok, timed.Status);
        Assert.Equal(1, harness.Driver.WriteCount("transport.set"));
    }

    [Fact]
    public void BatchReducer_StatusTable()
    {
        BatchChildResult Result(int index, ResultStatus status, string state, bool sent, string? code = null) =>
            new("c" + index, index, status, state, code, sent, null);
        Assert.Equal(ResultStatus.Ok, BatchOutcomeReducer.Summarize(1, [Result(0, ResultStatus.Ok, ExecutionState.Applied, true)], "completed").Status);
        var unknown = BatchOutcomeReducer.Summarize(2, [Result(0, ResultStatus.Unverified, ExecutionState.Unknown, true)], "outcome_unknown");
        Assert.Equal("outcome_unknown", unknown.Outcome);
        var failedClean = BatchOutcomeReducer.Summarize(1, [Result(0, ResultStatus.Offline, ExecutionState.NotStarted, false, ErrorCodes.DriverOffline)], "failed");
        Assert.Equal(ResultStatus.Offline, failedClean.Status);
        Assert.Equal("failed", failedClean.Outcome);
        Assert.Equal((BatchOutcomeReducer.Next.Stop, "unverified_predecessor"),
            BatchOutcomeReducer.AfterChild(Result(0, ResultStatus.Unverified, ExecutionState.Applied, true), false));
        Assert.Equal((BatchOutcomeReducer.Next.Continue, (string?)null),
            BatchOutcomeReducer.AfterChild(Result(0, ResultStatus.Unverified, ExecutionState.Applied, true), true));
        Assert.Equal((BatchOutcomeReducer.Next.Stop, "outcome_unknown"),
            BatchOutcomeReducer.AfterChild(Result(0, ResultStatus.Unverified, ExecutionState.Unknown, true), true));
    }

    [Fact]
    public async Task API01_TopStatusAndExecutionStateAreConsistent()
    {
        using var harness = new ControllerHarness();
        var channel = harness.Driver.AddChannel("A");
        harness.Driver.Select(channel);
        var handle = await harness.HandleForAsync(channel);
        var responses = new List<BrokerResponse>
        {
            await harness.SendAsync("status"),
            await harness.SendAsync("transport", new JsonObject { ["action"] = "play" }),
            await harness.SendAsync("transport", new JsonObject { ["action"] = "cycle", ["enabled"] = false }),
            await harness.SendAsync("track", new JsonObject { ["action"] = "delete", ["target_scope"] = "selected" }),
            await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = "tgt_unknown", ["parameter"] = "volume", ["value"] = 0.1 }),
            await harness.SendAsync("mixer", new JsonObject { ["action"] = "set", ["target_handle"] = handle, ["parameter"] = "volume", ["value"] = 0.9 })
        };
        harness.Driver.LoseReplyAfterApply = true;
        responses.Add(await harness.SendAsync("transport", new JsonObject { ["action"] = "stop" }));
        foreach (var response in responses)
        {
            Assert.False(string.IsNullOrEmpty(response.ExecutionState));
            switch (response.Status)
            {
                case ResultStatus.Ok:
                    Assert.Contains(response.ExecutionState, new[] { ExecutionState.Applied, ExecutionState.NotApplied, ExecutionState.NotApplicable });
                    Assert.Null(response.Error);
                    break;
                case ResultStatus.RequiresConfirmation:
                    Assert.Equal(ExecutionState.NotStarted, response.ExecutionState);
                    break;
                case ResultStatus.Unverified:
                    Assert.Contains(response.ExecutionState, new[] { ExecutionState.Applied, ExecutionState.Unknown, ExecutionState.NotApplied });
                    if (response.Error?.Code == ErrorCodes.OutcomeUnknown) Assert.False(response.Error.Retryable);
                    break;
                default:
                    Assert.NotNull(response.Error);
                    Assert.NotEqual(ExecutionState.Applied, response.ExecutionState);
                    break;
            }
        }
        Assert.Contains(responses, response => response.Error?.Code == ErrorCodes.OutcomeUnknown);
    }
}
