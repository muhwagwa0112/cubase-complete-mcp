using System.Text.Json;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class StatusContractTests
{
    [Fact]
    public void LoopMidiFieldsUsePublicContractNames()
    {
        var snapshot = new CubaseStatusSnapshot(false, null, null, null, true, false, false, false,
            false, 2777, 69, 0, [], "ports_missing", true, false, false, "not_checked", [], [],
            false, "client", "broker", true);
        var json = JsonSerializer.Serialize(snapshot, PipeProtocol.JsonOptions);
        Assert.Contains("\"loopmidi_installed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"configured_loopmidi_ports\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"loop_midi_installed\"", json, StringComparison.Ordinal);
    }
}
