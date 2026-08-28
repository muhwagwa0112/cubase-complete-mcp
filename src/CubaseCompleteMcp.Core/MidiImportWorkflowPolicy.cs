namespace CubaseCompleteMcp.Core;

public static class MidiImportWorkflowPolicy
{
    public const int RequiredAbsentDialogSamples = 3;

    public static bool ShouldUseMenuFallback(string? stage, int menuFallbackCount) =>
        string.Equals(stage, "none", StringComparison.Ordinal) && menuFallbackCount == 0;

    public static bool IsFileDialogClosureStable(int consecutiveAbsentSamples) =>
        consecutiveAbsentSamples >= RequiredAbsentDialogSamples;

    public static string? SubmissionUnverifiedReason(bool dialogClosed) =>
        dialogClosed ? null : "duplicate_midi_file_dialog";
}
