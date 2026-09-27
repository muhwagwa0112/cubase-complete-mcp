using CubaseCompleteMcp.Broker;

namespace CubaseCompleteMcp.Tests;

public sealed class WindowOwnershipPolicyTests
{
    private const int CubaseProcessId = 4242;
    private const int ForeignProcessId = 777;

    private static readonly IReadOnlyList<string> MidiImportFileFragments =
        WindowOwnershipPolicy.TitleFragmentsFor(CubaseDialogStage.MidiImportFileDialog);

    [Fact]
    public void ForeignWindowWithMatchingTitleInsideCubaseIsRejectedWhenIdentityIsUnverified()
    {
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            cubaseIdentityVerified: false,
            candidateProcessId: ForeignProcessId,
            matchedTitleFragments: ["Import MIDI File"],
            rectangleInsideCubaseWindow: true));

        Assert.False(decision.Accepted);
        Assert.Equal(WindowOwnershipRejection.CubaseProcessIdentityUnverified, decision.Rejection);
        Assert.Equal("CubaseProcessIdentityUnverified", decision.RejectionReason);
        Assert.False(string.IsNullOrWhiteSpace(decision.Detail));
    }

    [Fact]
    public void ForeignWindowWithMatchingTitleInsideCubaseIsRejectedEvenWithVerifiedCubaseIdentity()
    {
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            candidateProcessId: ForeignProcessId,
            matchedTitleFragments: ["Import MIDI File"],
            rectangleInsideCubaseWindow: true));

        Assert.False(decision.Accepted);
        Assert.Equal(WindowOwnershipRejection.OwnerChainForeignProcess, decision.Rejection);
        Assert.Equal("OwnerChainForeignProcess", decision.RejectionReason);
    }

    [Fact]
    public void CubaseProcessWindowIsAcceptedByProcessIdentity()
    {
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            candidateProcessId: CubaseProcessId,
            matchedTitleFragments: ["MIDI 파일 가져오기"],
            rectangleInsideCubaseWindow: false));

        Assert.True(decision.Accepted);
        Assert.Equal(WindowOwnershipRejection.None, decision.Rejection);
        Assert.Contains("identity-verified Cubase process", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogOwnedByCubaseTopLevelWindowIsAccepted()
    {
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            candidateProcessId: ForeignProcessId,
            ownerChainProcessId: CubaseProcessId,
            ownerChainReachedCubaseTopLevel: true,
            matchedTitleFragments: ["Import MIDI File"],
            rectangleInsideCubaseWindow: true));

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void OwnedDialogOutsideTheCubaseWindowRectangleIsRejected()
    {
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            candidateProcessId: ForeignProcessId,
            ownerChainProcessId: CubaseProcessId,
            ownerChainReachedCubaseTopLevel: true,
            matchedTitleFragments: ["Import MIDI File"],
            rectangleInsideCubaseWindow: false));

        Assert.False(decision.Accepted);
        Assert.Equal(WindowOwnershipRejection.OwnerWindowNotContainedInCubaseWindow, decision.Rejection);
    }

    [Fact]
    public void OwnedDialogThatNeverReachesACubaseTopLevelWindowIsRejected()
    {
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            candidateProcessId: ForeignProcessId,
            ownerChainProcessId: CubaseProcessId,
            ownerChainReachedCubaseTopLevel: false,
            matchedTitleFragments: ["Import MIDI File"],
            rectangleInsideCubaseWindow: true));

        Assert.False(decision.Accepted);
        Assert.Equal(WindowOwnershipRejection.OwnerChainNotCubaseTopLevel, decision.Rejection);
    }

    [Fact]
    public void ReusedCubaseProcessIdIsRejected()
    {
        // The window reports the Cubase process id, but the process start time no longer matches the
        // inspected process, so the id was reused by another process.
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            cubaseIdentityVerified: false,
            candidateProcessId: CubaseProcessId,
            matchedTitleFragments: ["Import MIDI File"],
            rectangleInsideCubaseWindow: true));

        Assert.False(decision.Accepted);
        Assert.Equal(WindowOwnershipRejection.CubaseProcessIdentityUnverified, decision.Rejection);
    }

    [Fact]
    public void WindowCarryingOnlyAnotherStagesTitleIsRejected()
    {
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            expectedStage: CubaseDialogStage.ProjectFolderChoice,
            matchedTitleFragments: ["Import MIDI File"]));

        Assert.False(decision.Accepted);
        Assert.Equal(WindowOwnershipRejection.TitleRoleMismatch, decision.Rejection);
    }

    [Fact]
    public void WindowWithoutTheDeclaredStageTitleIsRejected()
    {
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            expectedStage: CubaseDialogStage.ProjectFolderChoice,
            matchedTitleFragments: []));

        Assert.False(decision.Accepted);
        Assert.Equal(WindowOwnershipRejection.TitleRoleMismatch, decision.Rejection);
    }

    [Fact]
    public void CubaseProcessWindowWithStructuralStageRoleIsAcceptedWithoutTitleFragments()
    {
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            expectedStage: CubaseDialogStage.MidiImportProjectChoice,
            structuralRoleConfirmed: true));

        Assert.True(decision.Accepted);
    }

    [Theory]
    [InlineData(false, true, true, CubaseDialogStage.MidiImportFileDialog, WindowOwnershipRejection.WindowNotVisible)]
    [InlineData(true, false, true, CubaseDialogStage.MidiImportFileDialog, WindowOwnershipRejection.TitleEmpty)]
    [InlineData(true, true, false, CubaseDialogStage.MidiImportFileDialog, WindowOwnershipRejection.RectangleEmpty)]
    [InlineData(true, true, true, CubaseDialogStage.None, WindowOwnershipRejection.StageUnspecified)]
    public void CandidateWithoutBasicWindowFactsIsRejected(bool isVisible, bool hasTitle, bool hasRectangle,
        CubaseDialogStage expectedStage, WindowOwnershipRejection expectedRejection)
    {
        var decision = WindowOwnershipPolicy.Evaluate(Evidence(
            isVisible: isVisible,
            hasNonEmptyTitle: hasTitle,
            hasNonEmptyRectangle: hasRectangle,
            expectedStage: expectedStage,
            matchedTitleFragments: ["Import MIDI File"]));

        Assert.False(decision.Accepted);
        Assert.Equal(expectedRejection, decision.Rejection);
    }

    private static WindowOwnershipEvidence Evidence(
        bool cubaseIdentityVerified = true,
        int candidateProcessId = CubaseProcessId,
        int? ownerChainProcessId = null,
        bool ownerChainReachedCubaseTopLevel = false,
        CubaseDialogStage expectedStage = CubaseDialogStage.MidiImportFileDialog,
        IReadOnlyList<string>? matchedTitleFragments = null,
        IReadOnlyList<string>? declaredTitleFragments = null,
        bool structuralRoleConfirmed = false,
        bool isVisible = true,
        bool hasNonEmptyTitle = true,
        bool hasNonEmptyRectangle = true,
        bool rectangleInsideCubaseWindow = false) => new()
    {
        CandidateHandle = new IntPtr(0x4B1D),
        CandidateProcessId = candidateProcessId,
        CubaseProcessId = CubaseProcessId,
        CubaseIdentityVerified = cubaseIdentityVerified,
        OwnerChainProcessId = ownerChainProcessId,
        OwnerChainReachedCubaseTopLevel = ownerChainReachedCubaseTopLevel,
        ExpectedStage = expectedStage,
        MatchedTitleFragments = matchedTitleFragments ?? [],
        DeclaredTitleFragments = declaredTitleFragments ?? WindowOwnershipPolicy.TitleFragmentsFor(expectedStage),
        StructuralRoleConfirmed = structuralRoleConfirmed,
        IsVisible = isVisible,
        HasNonEmptyTitle = hasNonEmptyTitle,
        HasNonEmptyRectangle = hasNonEmptyRectangle,
        RectangleInsideCubaseWindow = rectangleInsideCubaseWindow
    };
}
