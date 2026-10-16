using System.Text;
using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Broker;

/// <summary>
/// Reads back Cubase's Export Audio Mixdown settings from OCR lines of the dialog. The dialog exposes no controls, so
/// the values are recognized text: each label ("File Type", "Sample Rate", "Bit Depth", "Preview", "Path") is paired
/// with the value on the same row to its right, and both sides are compared after the same normalization of OCR noise
/// (case, spaces, l/I/1/|, 0/O, ₩ for a backslash). An export is started only when every setting matches.
/// </summary>
public static class ExportDialogReading
{
    public sealed record Result(bool Ok, string? Message, JsonObject Evidence);

    private const double RowTolerance = 8;
    private const double MinimumValueOffset = 60;

    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var raw in text.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(raw)) continue;
            builder.Append(raw switch
            {
                'i' or '1' or '|' or '!' => 'l',
                '0' => 'o',
                '₩' or '/' => '\\',
                _ => raw
            });
        }
        return builder.ToString();
    }

    private sealed record Line(string Text, double X, double Y);

    private static List<Line> Lines(JsonArray ocr) =>
        ocr.OfType<JsonObject>()
            .Select(line => (Text: line["text"]?.GetValue<string>(), First: (line["words"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()))
            .Where(item => !string.IsNullOrWhiteSpace(item.Text) && item.First is not null)
            .Select(item => new Line(item.Text!, item.First!["x"]!.GetValue<double>(), item.First["y"]!.GetValue<double>()))
            .ToList();

    /// <summary>The recognized value on the row of the first label whose normalized text contains <paramref name="labelKey"/>.</summary>
    private static string? ValueFor(IReadOnlyList<Line> lines, string labelKey)
    {
        var key = Normalize(labelKey);
        var label = lines.FirstOrDefault(line => Normalize(line.Text).Contains(key, StringComparison.Ordinal));
        if (label is null) return null;
        return lines.Where(line => !ReferenceEquals(line, label) && Math.Abs(line.Y - label.Y) <= RowTolerance && line.X >= label.X + MinimumValueOffset)
            .OrderBy(line => line.X).FirstOrDefault()?.Text;
    }

    public static Result Verify(JsonArray ocr, string directory, string fileName)
    {
        var lines = Lines(ocr);
        var evidence = new JsonObject();
        var problems = new List<string>();
        void Expect(string field, string labelKey, Func<string, bool> matches, string expected)
        {
            var value = ValueFor(lines, labelKey);
            evidence[field] = new JsonObject { ["read"] = value, ["expected"] = expected };
            if (value is null) problems.Add($"{field} could not be read");
            else if (!matches(Normalize(value))) problems.Add($"{field} reads '{value}', expected {expected}");
        }
        Expect("file_type", "File Type", value => value.Contains("wave", StringComparison.Ordinal), "Wave");
        Expect("sample_rate", "Sample Rate", value => value.StartsWith(Normalize("48.0 kHz"), StringComparison.Ordinal) || value.StartsWith(Normalize("48 kHz"), StringComparison.Ordinal), "48.0 kHz");
        Expect("bit_depth", "Depth", value => value == Normalize("24 bit"), "24 bit");
        Expect("preview", "Preview", value => value == Normalize(fileName + ".wav"), fileName + ".wav");
        var expectedDirectory = Normalize(directory.TrimEnd('\\', '/'));
        Expect("path", "Path", value =>
        {
            var shown = value.TrimEnd('.', '…');
            var truncated = shown.Length < value.Length;
            return shown.Length >= 8 && (truncated ? expectedDirectory.StartsWith(shown, StringComparison.Ordinal) : shown.TrimEnd('\\') == expectedDirectory);
        }, directory);
        evidence["ocr_line_count"] = lines.Count;
        return problems.Count == 0
            ? new Result(true, null, evidence)
            : new Result(false, "The Export Audio Mixdown settings do not match a 48 kHz/24-bit WAV at the requested path: " + string.Join("; ", problems) + ".", evidence);
    }
}
