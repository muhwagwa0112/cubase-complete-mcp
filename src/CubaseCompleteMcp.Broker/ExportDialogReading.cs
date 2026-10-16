using System.Text;
using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Broker;

/// <summary>
/// Reads back Cubase's Export Audio Mixdown settings from OCR lines of the dialog. The dialog exposes no controls, so
/// the values are recognized text: each label ("File Type", "Sample Rate", "Bit Depth", "Preview", "Path") is paired
/// with the value on the same row to its right, and both sides are compared after the same normalization of OCR noise
/// (case, spaces, l/I/1/|, 0/O, ₩ for a backslash). The file name in Preview is stricter: it decides which file is
/// written, so it is compared position by position and only known OCR confusions are forgiven ('_' or a space read as
/// the other, l/I/1, O/0, case); a separator dropped or inserted makes a different name. An export is started only
/// when every setting matches.
/// </summary>
public static class ExportDialogReading
{
    /// <param name="OnlyNameDiffers">Every setting matches except the file name shown in Preview.</param>
    public sealed record Result(bool Ok, string? Message, JsonObject Evidence, bool OnlyNameDiffers = false);

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

    /// <summary>One OCR-confusable character class: case, l/i/1/|/!, o/0, and '_' or whitespace (a separator).</summary>
    private static char NameClass(char raw) => char.ToLowerInvariant(raw) switch
    {
        'i' or '1' or '|' or '!' => 'l',
        '0' => 'o',
        '_' => ' ',
        var other when char.IsWhiteSpace(other) => ' ',
        var other => other
    };

    /// <summary>
    /// True when an OCR read of a file name can only be <paramref name="requested"/>: same length, and every position
    /// is the same character up to a known OCR confusion. A separator that OCR dropped or inserted is not forgiven,
    /// because "YuugureDrive final" can be the reading of "YuugureDrive_final" as well as of "YuugureDriveFinal".
    /// </summary>
    public static bool SameNameUnderOcrConfusions(string read, string requested)
    {
        var left = read.Trim();
        var right = requested.Trim();
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
            if (NameClass(left[i]) != NameClass(right[i])) return false;
        return true;
    }

    /// <summary>Equal once separators are removed as well: a reading that cannot tell these names apart.</summary>
    private static bool SameNameIgnoringSeparators(string read, string requested)
    {
        static string Key(string text) => new(text.Select(NameClass).Where(ch => ch != ' ').ToArray());
        return Key(read) == Key(requested);
    }

    /// <summary>The Preview text without its ".wav" extension (spacing inside the extension is OCR noise); null without one.</summary>
    private static string? PreviewStem(string preview)
    {
        var dot = preview.LastIndexOf('.');
        if (dot < 0 || Normalize(preview[(dot + 1)..]) != "wav") return null;
        return preview[..dot].TrimEnd();
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

    public static Result Verify(JsonArray ocr, string directory, string fileName) => Verify(new List<JsonArray> { ocr }, directory, fileName);

    /// <summary>
    /// Verifies against several OCR passes of the same capture (different upscales recognize different words). A field
    /// matches when any pass reads the expected value; OCR noise cannot produce the exact expected text by chance.
    /// <paramref name="allowSeparatorNoise"/> also accepts a Preview that differs from the name only by separators. The
    /// caller allows it only after it typed the name over a reading that was clearly another name, so such a reading
    /// cannot be the old name.
    /// </summary>
    public static Result Verify(IReadOnlyList<JsonArray> passes, string directory, string fileName, bool allowSeparatorNoise = false)
    {
        var passLines = passes.Select(Lines).ToList();
        var evidence = new JsonObject();
        var problems = new List<string>();
        var differing = new List<string>();
        void Expect(string field, string labelKey, Func<string, bool> matches, string expected)
        {
            var reads = passLines.Select(lines => ValueFor(lines, labelKey)).ToList();
            var matched = reads.FirstOrDefault(value => value is not null && matches(value));
            var value = matched ?? reads.FirstOrDefault(read => read is not null);
            evidence[field] = new JsonObject { ["read"] = value, ["expected"] = expected, ["pass"] = matched is null ? null : reads.IndexOf(matched) };
            if (matched is not null) return;
            if (value is null) problems.Add($"{field} could not be read");
            else { problems.Add($"{field} reads '{value}', expected {expected}"); differing.Add(field); }
        }
        Expect("file_type", "File Type", value => Normalize(value).Contains("wave", StringComparison.Ordinal), "Wave");
        Expect("sample_rate", "Sample Rate", value => Normalize(value).StartsWith(Normalize("48.0 kHz"), StringComparison.Ordinal)
            || Normalize(value).StartsWith(Normalize("48 kHz"), StringComparison.Ordinal), "48.0 kHz");
        Expect("bit_depth", "Depth", value => Normalize(value) == Normalize("24 bit"), "24 bit");
        var separatorOnly = false;
        Expect("preview", "Preview", value =>
        {
            if (PreviewStem(value) is not { } stem) return false;
            if (SameNameUnderOcrConfusions(stem, fileName)) return true;
            if (!SameNameIgnoringSeparators(stem, fileName)) return false;
            separatorOnly = true;
            return allowSeparatorNoise;
        }, fileName + ".wav");
        if (evidence["preview"] is JsonObject preview)
        {
            var read = preview["read"]?.GetValue<string>();
            var exact = read is not null && PreviewStem(read) is { } stem && SameNameUnderOcrConfusions(stem, fileName);
            preview["separator_only_difference"] = separatorOnly && !exact;
            preview["matched_with_separator_noise"] = preview["pass"] is not null && !exact;
        }
        var expectedDirectory = Normalize(directory.TrimEnd('\\', '/'));
        Expect("path", "Path", raw =>
        {
            var value = Normalize(raw);
            var shown = value.TrimEnd('.', '…');
            var truncated = shown.Length < value.Length;
            return shown.Length >= 8 && (truncated ? expectedDirectory.StartsWith(shown, StringComparison.Ordinal) : shown.TrimEnd('\\') == expectedDirectory);
        }, directory);
        evidence["ocr_line_count"] = passLines.Sum(lines => lines.Count);
        evidence["ocr_passes"] = passLines.Count;
        var onlyName = problems.Count == 1 && differing.SequenceEqual(["preview"]);
        evidence["only_name_differs"] = onlyName;
        return problems.Count == 0
            ? new Result(true, null, evidence)
            : new Result(false, "The Export Audio Mixdown settings do not match a 48 kHz/24-bit WAV at the requested path: " + string.Join("; ", problems) + ".",
                evidence, onlyName);
    }
}
