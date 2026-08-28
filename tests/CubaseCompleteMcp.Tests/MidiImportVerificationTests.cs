using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class MidiImportVerificationTests
{
    [Fact]
    public void ExactDirectNames_Verify()
    {
        var result = MidiImportVerification.Evaluate(Document("Piano", "Bass"), ["Piano", "Bass"], null, false, true);
        Assert.True(result.Verified);
        Assert.Equal("direct_access_track_names", result.Method);
    }

    [Fact]
    public void ExactOcrNames_Verify()
    {
        var result = MidiImportVerification.Evaluate(Document("Piano", "Bass"), [], "Piano\nBass", false, true);
        Assert.True(result.Verified);
        Assert.Equal("project_ocr_track_names", result.Method);
    }

    [Fact]
    public void CombinedNames_Verify()
    {
        var result = MidiImportVerification.Evaluate(Document("Piano", "Bass"), ["Piano"], "Bass", false, true);
        Assert.True(result.Verified);
        Assert.Equal("combined_track_names", result.Method);
    }

    [Theory]
    [InlineData(false, true, "expected_track_names_not_observed")]
    [InlineData(true, true, "midi_file_dialog_remains")]
    [InlineData(false, false, "direct_postcondition_failed")]
    public void GraphOrSubmissionEvidenceAlone_DoesNotVerify(bool dialogRemains, bool directOk, string reason)
    {
        var result = MidiImportVerification.Evaluate(Document("Piano"), ["Unrelated"], null, dialogRemains, directOk);
        Assert.False(result.Verified);
        Assert.Equal("none", result.Method);
        Assert.Equal(reason, result.UnverifiedReason);
    }

    [Fact]
    public void EmptyDuplicateAndConductorOnlyNames_AreAmbiguous()
    {
        foreach (var document in new[] { Document(""), Document("Piano", "Piano"), Document() })
        {
            var result = MidiImportVerification.Evaluate(document, ["Piano"], "Piano", false, true);
            Assert.False(result.Verified);
            Assert.Equal("ambiguous_expected_track_names", result.UnverifiedReason);
        }
    }

    private static MidiCompositionDocument Document(params string[] names) => new()
    {
        Id = "midi_test", Name = "Test", Tracks = [
            new MidiCompositionTrack { Id = "trk_conductor", Name = "Conductor" },
            .. names.Select((name, index) => new MidiCompositionTrack { Id = $"trk_{index}", Name = name })
        ]
    };
}
