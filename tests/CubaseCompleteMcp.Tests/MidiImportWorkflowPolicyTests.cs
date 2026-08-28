using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class MidiImportWorkflowPolicyTests
{
    [Fact]
    public void MenuFallbackIsAllowedOnlyOnceAfterNoStage()
    {
        Assert.True(MidiImportWorkflowPolicy.ShouldUseMenuFallback("none", 0));
        Assert.False(MidiImportWorkflowPolicy.ShouldUseMenuFallback("project_choice", 0));
        Assert.False(MidiImportWorkflowPolicy.ShouldUseMenuFallback("midi_file_dialog", 0));
        Assert.False(MidiImportWorkflowPolicy.ShouldUseMenuFallback("none", 1));
    }

    [Fact]
    public void FileDialogMustBeAbsentForThreeSamples()
    {
        Assert.False(MidiImportWorkflowPolicy.IsFileDialogClosureStable(1));
        Assert.False(MidiImportWorkflowPolicy.IsFileDialogClosureStable(2));
        Assert.True(MidiImportWorkflowPolicy.IsFileDialogClosureStable(3));
    }

    [Fact]
    public void RemainingDialogReturnsDuplicateReason()
    {
        Assert.Equal("duplicate_midi_file_dialog",
            MidiImportWorkflowPolicy.SubmissionUnverifiedReason(false));
        Assert.Null(MidiImportWorkflowPolicy.SubmissionUnverifiedReason(true));
    }
}
