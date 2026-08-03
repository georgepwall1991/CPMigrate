using CPMigrate.Models;

namespace CPMigrate.Services.Verify;

/// <summary>
/// Answers the only question a reviewer of a migration PR actually has: does this change what we ship?
/// </summary>
/// <remarks>
/// A migration rewrites every project file and picks a winner for every version conflict, and until
/// now the only evidence it worked was that it did not throw. <c>--update-packages</c> has restored
/// and tested since 3.0; the namesake operation — the more invasive of the two — verified nothing.
/// </remarks>
public sealed class MigrationVerifier
{
    private readonly IGraphSnapshotService _snapshots;

    public MigrationVerifier(IGraphSnapshotService snapshots)
    {
        _snapshots = snapshots;
    }

    /// <summary>
    /// Captures the graph as it stands, before anything is written.
    /// </summary>
    public Task<GraphSnapshotResult> CaptureAsync(
        string restoreTargetPath,
        IReadOnlyList<string> projectPaths,
        string? basePath
    ) => _snapshots.CaptureAsync(restoreTargetPath, projectPaths, basePath);

    /// <summary>
    /// Reaches a verdict on two captures and the decisions taken between them.
    /// </summary>
    public static VerificationReport Compare(
        GraphSnapshotResult baseline,
        GraphSnapshotResult after,
        IReadOnlyList<MigrationDecision> decisions
    )
    {
        if (!baseline.RestoreSucceeded)
        {
            return Failed(
                decisions,
                "the solution did not restore before the migration, so there is no baseline to "
                    + "measure it against",
                baseline.RestoreOutput,
                projectsExpected: 0
            );
        }

        if (!after.RestoreSucceeded)
        {
            // The loudest possible outcome, and the one worth the whole feature on its own: the
            // migration produced a tree that does not restore. Nothing else CPMigrate checks would
            // have noticed.
            //
            // The baseline's coverage is carried through, because it is real. Reporting 0/0 here
            // would make a migration that broke six working projects indistinguishable from a run
            // that never measured anything — and the first is far worse news than the second.
            return Failed(
                decisions,
                "the solution does not restore after the migration",
                after.RestoreOutput,
                baseline.Snapshot.ProjectCount
            );
        }

        var diff = GraphDiff.Compare(baseline.Snapshot, after.Snapshot);

        if (diff.IntegrityFailures.Count > 0)
        {
            return new VerificationReport(
                VerificationVerdict.Failed,
                after.Snapshot.Projects.Count,
                baseline.Snapshot.ProjectCount,
                after.Snapshot.ResolvedVersionCount,
                diff.UnchangedCount,
                [],
                diff.IntegrityFailures,
                decisions,
                "the graph before and after the migration do not cover the same projects, so they "
                    + "cannot be compared"
            );
        }

        var attributed = DriftAttributor.Attribute(
            diff.Changes,
            decisions,
            baseline.Snapshot,
            after.Snapshot
        );

        var verdict = attributed.Count switch
        {
            0 => VerificationVerdict.Unchanged,
            _ when attributed.Any(
                    (AttributedChange change) => change.Kind == DriftExplanation.Unexplained
                ) => VerificationVerdict.UnexplainedDrift,
            _ => VerificationVerdict.ExplainedDrift,
        };

        return new VerificationReport(
            verdict,
            after.Snapshot.Projects.Count,
            baseline.Snapshot.ProjectCount,
            after.Snapshot.ResolvedVersionCount,
            diff.UnchangedCount,
            attributed,
            [],
            decisions,
            FailureReason: null
        );
    }

    private static VerificationReport Failed(
        IReadOnlyList<MigrationDecision> decisions,
        string reason,
        string output,
        int projectsExpected
    )
    {
        return new VerificationReport(
            VerificationVerdict.Failed,
            ProjectsRestored: 0,
            projectsExpected,
            ResolvedVersionCount: 0,
            UnchangedCount: 0,
            [],
            [],
            decisions,
            $"{reason}. {Tail(output)}".TrimEnd()
        );
    }

    /// <summary>
    /// The last few lines of a restore log. The whole thing is unreadable in a terminal and useless
    /// in a PR comment, and the NU error that matters is almost always at the end.
    /// </summary>
    private static string Tail(string output)
    {
        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(5)
            .ToList();

        return lines.Count == 0 ? string.Empty : string.Join(" ", lines);
    }
}

/// <summary>What verification concluded.</summary>
public enum VerificationVerdict
{
    /// <summary>Every resolved version is exactly what it was.</summary>
    Unchanged,

    /// <summary>The graph moved, and every change follows from a decision the migration made.</summary>
    ExplainedDrift,

    /// <summary>The graph moved in a way nothing the migration decided accounts for.</summary>
    UnexplainedDrift,

    /// <summary>
    /// No verdict could be reached — restore failed, or the two captures do not describe the same
    /// solution. Never treated as clean: not knowing is not the same as knowing nothing is wrong.
    /// </summary>
    Failed,
}

/// <summary>The verification receipt: what was compared, what moved, and what accounts for it.</summary>
public sealed record VerificationReport(
    VerificationVerdict Verdict,
    int ProjectsRestored,
    int ProjectsExpected,
    int ResolvedVersionCount,
    int UnchangedCount,
    IReadOnlyList<AttributedChange> Changes,
    IReadOnlyList<GraphIntegrityFailure> IntegrityFailures,
    IReadOnlyList<MigrationDecision> Decisions,
    string? FailureReason
)
{
    /// <summary>
    /// Whether the migration was actually undone — set from what happened, not from
    /// <see cref="ShouldRollBack"/>, which is only the intent.
    /// </summary>
    /// <remarks>
    /// They come apart in the case that matters most. A rollback needs a backup, and it needs to be
    /// allowed to run unattended; a report asserting the tree was restored when it was not is worse
    /// than one admitting it could not be, because the first tells someone not to look.
    /// </remarks>
    public bool RolledBack { get; init; }

    /// <summary>
    /// Whether the run should be allowed to stand. Under <paramref name="strict"/>, drift the report
    /// can explain still fails — that is what a team asking for a literal no-op is asking for.
    /// </summary>
    public bool Passed(bool strict) =>
        Verdict switch
        {
            VerificationVerdict.Unchanged => true,
            VerificationVerdict.ExplainedDrift => !strict,
            _ => false,
        };

    /// <summary>
    /// Whether the migration should be undone.
    ///
    /// Deliberately not the same question as <see cref="Passed"/>. Drift that fails only because of
    /// <c>--verify-strict</c> is drift the report can account for, and the tree is left in place so it
    /// can be read — the person who asked for a no-op wants to see what stopped it being one. Drift
    /// nothing explains, and a run that reached no verdict at all, are undone.
    /// </summary>
    public bool ShouldRollBack =>
        Verdict is VerificationVerdict.UnexplainedDrift or VerificationVerdict.Failed;

    public int ChangedCount => Changes.Count;

    public int UnexplainedCount =>
        Changes.Count((AttributedChange change) => change.Kind == DriftExplanation.Unexplained);
}
