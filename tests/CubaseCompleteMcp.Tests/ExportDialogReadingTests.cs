using System.Text.Json.Nodes;
using CubaseCompleteMcp.Broker;

namespace CubaseCompleteMcp.Tests;

/// <summary>
/// Read-back of Cubase 15's Export Audio Mixdown settings from OCR lines. The lines below are what Windows OCR (Korean
/// profile, 2x upscale) recognized from the live dialog on Cubase 15.0.30, spacing and l/I confusions included.
/// </summary>
public sealed class ExportDialogReadingTests
{
    private const string Directory = @"C:\Users\lufth\AppData\Local\CubaseCompleteMCP\Acceptance\fx-b2ab6e4c8c03364d\Mixdown";

    private static JsonObject Line(string text, double x, double y) =>
        new() { ["text"] = text, ["words"] = new JsonArray(new JsonObject { ["text"] = text.Split(' ')[0], ["x"] = x, ["y"] = y, ["width"] = 40, ["height"] = 12 }) };

    private static JsonArray LiveDialog(string fileType = "Wave", string rate = "48.0 kHz", string bits = "24 bit", string preview = "accepta nce.wav",
        string path = @"C:\Users\I ufth\App Data\Loca l\CubaseCom pIeteM CP\Acceptance\fx- b2...") => new(
        Line("Name", 377, 101), Line("acceptance", 501, 101),
        Line("Path", 377, 130), Line(path, 501, 130),
        Line("Prevlew", 377, 161), Line(preview, 501, 161),
        Line("File Type", 377, 262), Line(fileType, 501, 263),
        Line("Sample Rate", 376, 292), Line(rate, 501, 293),
        Line("8it Depth", 377, 322), Line(bits, 501, 323),
        Line("lnsert Broadcast Wave Chunk", 677, 263), Line("Left Locator Position", 54, 550));

    [Fact]
    public void LiveOcrOfAMatchingDialog_IsAccepted()
    {
        var result = ExportDialogReading.Verify(LiveDialog(), Directory, "acceptance");
        Assert.True(result.Ok, result.Message);
        Assert.Equal("accepta nce.wav", result.Evidence["preview"]!["read"]!.GetValue<string>());
        Assert.Equal("Wave", result.Evidence["file_type"]!["read"]!.GetValue<string>()); // not the "Insert Broadcast Wave Chunk" label
    }

    [Theory]
    [InlineData("MPEG 1 Layer 3", "48.0 kHz", "24 bit", "accepta nce.wav", "file_type")]
    [InlineData("Wave", "44.1 kHz", "24 bit", "accepta nce.wav", "sample_rate")]
    [InlineData("Wave", "48.0 kHz", "32 bit float", "accepta nce.wav", "bit_depth")]
    [InlineData("Wave", "48.0 kHz", "24 bit", "other.wav", "preview")]
    public void AnyDifferingSetting_IsRefusedAndNamed(string fileType, string rate, string bits, string preview, string field)
    {
        var result = ExportDialogReading.Verify(LiveDialog(fileType, rate, bits, preview), Directory, "acceptance");
        Assert.False(result.Ok);
        Assert.Contains(field, result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void APathOutsideTheRequestedDirectory_IsRefused()
    {
        var result = ExportDialogReading.Verify(LiveDialog(path: @"C:\Users\lufth\Desktop\New York"), Directory, "acceptance");
        Assert.False(result.Ok);
        Assert.Contains("path", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableSetting_IsRefused()
    {
        var lines = LiveDialog();
        lines.Remove(lines.Single(line => line!["text"]!.GetValue<string>() == "24 bit")); // the Bit Depth value was not recognized
        var result = ExportDialogReading.Verify(lines, Directory, "acceptance");
        Assert.False(result.Ok);
        Assert.Contains("bit_depth could not be read", result.Message!, StringComparison.Ordinal);
    }
}
