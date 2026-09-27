namespace CubaseCompleteMcp.Core;

public sealed record MidiImportVerificationResult(
    bool Verified,
    string Method,
    string? UnverifiedReason,
    string[] ExpectedTrackNames,
    string[] DirectObservedTrackNames,
    string[] OcrObservedTrackNames,
    string[] ObservedTrackNames);

public static class MidiImportVerification
{
    public static MidiImportVerificationResult Evaluate(
        MidiCompositionDocument document,
        IEnumerable<string?> directNames,
        string? ocrText,
        bool fileDialogRemains,
        bool directObservationOk)
    {
        var expected = document.Tracks.Skip(1).Select(track => track.Name?.Trim() ?? string.Empty).ToArray();
        var ambiguous = expected.Length == 0
            || expected.Any(string.IsNullOrWhiteSpace)
            || expected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Length;
        var validExpected = expected.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var direct = directNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim())
            .Where(name => validExpected.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var ocr = validExpected.Where(name => !string.IsNullOrWhiteSpace(ocrText)
            && ContainsBounded(ocrText!, name)).ToArray();
        var observed = direct.Concat(ocr).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var allDirect = !ambiguous && expected.All(name => direct.Contains(name, StringComparer.OrdinalIgnoreCase));
        var allOcr = !ambiguous && expected.All(name => ocr.Contains(name, StringComparer.OrdinalIgnoreCase));
        var allCombined = !ambiguous && expected.All(name => observed.Contains(name, StringComparer.OrdinalIgnoreCase));
        var verified = !fileDialogRemains && directObservationOk && allCombined;
        var method = verified
            ? allDirect ? "direct_access_track_names"
            : allOcr ? "project_ocr_track_names"
            : "combined_track_names"
            : "none";
        var reason = verified ? null
            : fileDialogRemains ? "midi_file_dialog_remains"
            : !directObservationOk ? "direct_postcondition_failed"
            : ambiguous ? "ambiguous_expected_track_names"
            : "expected_track_names_not_observed";
        return new MidiImportVerificationResult(verified, method, reason, expected, direct, ocr, observed);
    }

    /// <summary>
    /// Matches a track name only when the OCR text contains it as a whole token. A plain substring search would
    /// report "Bass" as observed when the imported project only contains "Bassoon".
    /// </summary>
    private static bool ContainsBounded(string text, string name)
    {
        var index = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 ? '\0' : text[index - 1];
            var after = index + name.Length >= text.Length ? '\0' : text[index + name.Length];
            if (!IsNameCharacter(before) && !IsNameCharacter(after))
            {
                return true;
            }

            index = text.IndexOf(name, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsNameCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}
