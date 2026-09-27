namespace CubaseCompleteMcp.Broker;

/// <summary>
/// Workflow stage that expects a Cubase dialog window. Every window-discovery call site declares the
/// stage it is looking for so that a window matched only by a stray title fragment cannot be claimed
/// by an unrelated workflow.
/// </summary>
public enum CubaseDialogStage
{
    /// <summary>No stage was declared. Discovery is refused.</summary>
    None = 0,

    /// <summary>Open/save file dialog, identified by a file-name edit plus an accept button.</summary>
    FileDialog,

    /// <summary>"Import MIDI File" file dialog.</summary>
    MidiImportFileDialog,

    /// <summary>MIDI import project question, identified by its button set rather than its title.</summary>
    MidiImportProjectChoice,

    /// <summary>MIDI import options dialog.</summary>
    MidiImportOptions,

    /// <summary>"Set Project Folder" dialog.</summary>
    ProjectFolderChoice,

    /// <summary>Caller-supplied modal lookup that declares its own title fragments.</summary>
    GenericModal
}

/// <summary>Machine-readable reason why a window was not accepted as a Cubase dialog.</summary>
public enum WindowOwnershipRejection
{
    None = 0,
    WindowNotVisible,
    TitleEmpty,
    RectangleEmpty,
    StageUnspecified,
    CubaseProcessIdentityUnverified,
    TitleRoleMismatch,
    OwnerChainForeignProcess,
    OwnerChainNotCubaseTopLevel,
    OwnerWindowNotContainedInCubaseWindow
}

/// <summary>Result of applying <see cref="WindowOwnershipPolicy"/> to one candidate window.</summary>
public sealed record WindowOwnershipDecision(bool Accepted, WindowOwnershipRejection Rejection, string Detail)
{
    public static WindowOwnershipDecision Accept(string detail) =>
        new(true, WindowOwnershipRejection.None, detail);

    public static WindowOwnershipDecision Reject(WindowOwnershipRejection rejection, string detail) =>
        new(false, rejection, detail);

    /// <summary>Machine-readable rejection token; "None" when the window was accepted.</summary>
    public string RejectionReason => Rejection.ToString();
}

/// <summary>
/// OS-observed facts about one candidate window plus the workflow stage the caller is looking for.
/// Geometry is recorded only as an additional filter: screen overlap never grants ownership.
/// </summary>
public sealed record WindowOwnershipEvidence
{
    public required IntPtr CandidateHandle { get; init; }

    /// <summary>Process that owns the candidate window; 0 when the lookup failed.</summary>
    public required int CandidateProcessId { get; init; }

    public required int CubaseProcessId { get; init; }

    /// <summary>True when the live process behind <see cref="CubaseProcessId"/> was re-verified by id and start time.</summary>
    public required bool CubaseIdentityVerified { get; init; }

    /// <summary>Process that owns the window reached by walking GW_OWNER, if the chain reached any window.</summary>
    public required int? OwnerChainProcessId { get; init; }

    /// <summary>True when the GW_OWNER chain reached a top-level window of the Cubase process.</summary>
    public required bool OwnerChainReachedCubaseTopLevel { get; init; }

    public required CubaseDialogStage ExpectedStage { get; init; }

    /// <summary>Declared title fragments of the stage that the candidate title actually matched.</summary>
    public required IReadOnlyList<string> MatchedTitleFragments { get; init; }

    /// <summary>Title fragments the stage expects; empty when the stage is identified structurally.</summary>
    public required IReadOnlyList<string> DeclaredTitleFragments { get; init; }

    /// <summary>True when the caller already confirmed the stage's structural role on this window.</summary>
    public required bool StructuralRoleConfirmed { get; init; }

    public required bool IsVisible { get; init; }
    public required bool HasNonEmptyTitle { get; init; }
    public required bool HasNonEmptyRectangle { get; init; }

    /// <summary>Whether the candidate rectangle's center lies inside a top-level Cubase window rectangle.</summary>
    public required bool RectangleInsideCubaseWindow { get; init; }

    public bool CandidateProcessIdMatchesCubase => CandidateProcessId != 0 && CandidateProcessId == CubaseProcessId;
}

/// <summary>
/// Pure ownership decision for Cubase dialog windows. A window is only accepted when it runs inside
/// the identity-verified Cubase process, or when its owner chain reaches a top-level window of that
/// process. Either way the candidate must carry the title or structural role of the declared workflow
/// stage, and an owner-chain candidate must additionally stay inside a Cubase window rectangle.
/// On-screen overlap with Cubase is never sufficient evidence of ownership.
/// </summary>
public static class WindowOwnershipPolicy
{
    /// <summary>Title fragments that identify a stage; stages identified structurally declare none.</summary>
    public static IReadOnlyList<string> TitleFragmentsFor(CubaseDialogStage stage) => stage switch
    {
        CubaseDialogStage.MidiImportFileDialog => ["Import MIDI File", "MIDI 파일 가져오기", "MIDI 파일 열기"],
        CubaseDialogStage.ProjectFolderChoice => ["Set Project Folder", "프로젝트 폴더"],
        CubaseDialogStage.MidiImportOptions => ["Import", "MIDI", "가져오기"],
        _ => []
    };

    /// <summary>
    /// Decides whether the candidate window may be treated as a dialog of the declared workflow
    /// stage. Returns the first applicable machine-readable rejection reason when it may not.
    /// Order of the gates: visibility, title, rectangle, declared stage, re-verified Cubase identity,
    /// stage role, then ownership by process id or by owner chain with containment as a final filter.
    /// </summary>
    public static WindowOwnershipDecision Evaluate(WindowOwnershipEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!evidence.IsVisible)
            return WindowOwnershipDecision.Reject(WindowOwnershipRejection.WindowNotVisible,
                "the candidate window is not visible");
        if (!evidence.HasNonEmptyTitle)
            return WindowOwnershipDecision.Reject(WindowOwnershipRejection.TitleEmpty,
                "the candidate window has no title to bind the workflow stage to");
        if (!evidence.HasNonEmptyRectangle)
            return WindowOwnershipDecision.Reject(WindowOwnershipRejection.RectangleEmpty,
                "the candidate window has no usable on-screen rectangle");
        if (evidence.ExpectedStage == CubaseDialogStage.None)
            return WindowOwnershipDecision.Reject(WindowOwnershipRejection.StageUnspecified,
                "no workflow stage was declared for the lookup");
        if (!evidence.CubaseIdentityVerified)
            return WindowOwnershipDecision.Reject(WindowOwnershipRejection.CubaseProcessIdentityUnverified,
                "the Cubase process could not be re-verified by process id and start time");

        if (!HasStageRoleEvidence(evidence))
            return WindowOwnershipDecision.Reject(WindowOwnershipRejection.TitleRoleMismatch,
                "the candidate carries neither the title nor the structural role of stage "
                + evidence.ExpectedStage);

        if (evidence.CandidateProcessIdMatchesCubase)
            return WindowOwnershipDecision.Accept(
                $"the window runs in the identity-verified Cubase process (pid {evidence.CubaseProcessId})");

        if (evidence.OwnerChainProcessId != evidence.CubaseProcessId)
            return WindowOwnershipDecision.Reject(WindowOwnershipRejection.OwnerChainForeignProcess,
                evidence.OwnerChainProcessId is int ownerProcessId
                    ? $"the owner chain ends in process {ownerProcessId}, not the Cubase process"
                    : "the window has no owner chain reaching the Cubase process");

        if (!evidence.OwnerChainReachedCubaseTopLevel)
            return WindowOwnershipDecision.Reject(WindowOwnershipRejection.OwnerChainNotCubaseTopLevel,
                "the owner chain did not reach a top-level window of the Cubase process");

        if (!evidence.RectangleInsideCubaseWindow)
            return WindowOwnershipDecision.Reject(WindowOwnershipRejection.OwnerWindowNotContainedInCubaseWindow,
                "the owned window is not contained by any Cubase window rectangle");

        return WindowOwnershipDecision.Accept(
            $"the window is owned by a top-level window of the identity-verified Cubase process (pid {evidence.CubaseProcessId})");
    }

    /// <summary>
    /// A candidate carries its stage role either structurally (the caller confirmed the stage's control
    /// signature on the window) or by title (a declared fragment of the stage matched the title).
    /// </summary>
    private static bool HasStageRoleEvidence(WindowOwnershipEvidence evidence)
    {
        if (evidence.StructuralRoleConfirmed) return true;
        return evidence.DeclaredTitleFragments.Count > 0
            && evidence.MatchedTitleFragments.Count > 0
            && evidence.MatchedTitleFragments.All(fragment =>
                evidence.DeclaredTitleFragments.Contains(fragment, StringComparer.OrdinalIgnoreCase));
    }
}
