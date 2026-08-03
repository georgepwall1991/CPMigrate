using CPMigrate.Models;

namespace CPMigrate.Services.Verify;

/// <summary>
/// Projects a <see cref="VerificationReport"/> into the machine-readable payload.
/// </summary>
/// <remarks>
/// A separate type rather than serialization attributes on the report, for the reason the rest of the
/// payload is built this way: the wire contract is versioned and the internal model is not, so an
/// internal rename must not become a breaking schema change nobody noticed.
/// </remarks>
public static class VerificationPayload
{
    /// <summary>
    /// Builds the payload, or null when the run did not verify.
    /// </summary>
    /// <remarks>
    /// Null rather than a zeroed object. "Absent means the run never verified" is the same contract
    /// <c>summary.disabledRules</c> keeps, and it is the only way a consumer can tell a clean
    /// verification from no verification at all.
    /// </remarks>
    public static VerificationInfo? From(VerificationReport? report, bool strict)
    {
        if (report is null)
        {
            return null;
        }

        return new VerificationInfo
        {
            Verdict = Camel(report.Verdict.ToString()),
            Passed = report.Passed(strict),
            Strict = strict,
            RolledBack = report.RolledBack,
            ProjectsRestored = report.ProjectsRestored,
            ProjectsExpected = report.ProjectsExpected,
            ResolvedVersions = report.ResolvedVersionCount,
            Unchanged = report.UnchangedCount,
            Changed = report.ChangedCount,
            Unexplained = report.UnexplainedCount,
            FailureReason = report.FailureReason,
            Changes = [.. report.Changes.Select(ToChangeInfo)],
            Decisions = [.. report.Decisions.Select(ToDecisionInfo)],
            IntegrityFailures =
                report.IntegrityFailures.Count == 0
                    ? null
                    : [.. report.IntegrityFailures.Select(ToIntegrityFailureInfo)],
        };
    }

    private static VerificationChangeInfo ToChangeInfo(AttributedChange change) =>
        new()
        {
            Project = change.Change.ProjectPath,
            TargetFramework = change.Change.TargetFramework,
            PackageId = change.Change.PackageId,
            Kind = Camel(change.Change.Kind.ToString()),
            Before = change.Change.Before,
            After = change.Change.After,
            Direction = Camel(change.Change.Direction.ToString()),
            Direct = change.Change.IsDirect,
            Explanation = Camel(change.Kind.ToString()),
            CausedBy = change.CausedBy,
            Description = change.Description,
        };

    private static VerificationDecisionInfo ToDecisionInfo(MigrationDecision decision) =>
        new()
        {
            PackageId = decision.PackageId,
            ResolvedVersion = decision.ResolvedVersion,
            Source = Camel(decision.Source.ToString()),
            Candidates =
            [
                .. decision.Candidates.Select(candidate => new VerificationCandidateInfo
                {
                    Version = candidate.Version,
                    Projects = [.. candidate.Projects],
                }),
            ],
        };

    private static VerificationIntegrityFailureInfo ToIntegrityFailureInfo(
        GraphIntegrityFailure failure
    ) =>
        new()
        {
            Project = failure.ProjectPath,
            TargetFramework = failure.TargetFramework,
            Reason = failure.Reason,
        };

    /// <summary>
    /// Lower-cases the first letter of an enum name, so the wire values match the camelCase the rest
    /// of the payload uses rather than exposing C# naming through the contract.
    /// </summary>
    private static string Camel(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
