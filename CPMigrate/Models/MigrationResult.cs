using CPMigrate.Fixers;
using CPMigrate.Services.Verify;

namespace CPMigrate.Models;

/// <summary>
/// Result of a CPM migration operation.
/// </summary>
public class MigrationResult
{
    /// <summary>
    /// Number of projects that were processed.
    /// </summary>
    public int ProjectsProcessed { get; init; }

    /// <summary>
    /// Number of unique packages found across all projects.
    /// </summary>
    public int PackagesCentralized { get; init; }

    /// <summary>
    /// Number of version conflicts that were detected and resolved.
    /// </summary>
    public int ConflictsResolved { get; init; }

    /// <summary>
    /// Path to the generated Directory.Packages.props file.
    /// </summary>
    public string PropsFilePath { get; init; } = string.Empty;

    /// <summary>
    /// Path to the backup directory, if backups were created.
    /// </summary>
    public string? BackupPath { get; init; }

    /// <summary>
    /// Whether this was a dry-run (no files modified).
    /// </summary>
    public bool WasDryRun { get; init; }

    /// <summary>
    /// Exit code for the operation.
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Analysis report, when run in analyze mode.
    /// </summary>
    public AnalysisReport? AnalysisReport { get; init; }

    /// <summary>
    /// Fix report, when analyze mode runs with --fix or --fix-dry-run.
    /// </summary>
    public FixReport? FixReport { get; init; }

    /// <summary>
    /// Findings that survived a <c>--fix</c> run, from a fresh scan of the modified tree.
    /// <see cref="AnalysisReport"/> is deliberately left as the pre-fix report so it lines up with
    /// <see cref="FixReport"/> — what was found, and what was done about it — while the gate and the
    /// remaining-issue count come from here, which is what is actually on disk.
    /// </summary>
    public AnalysisReport? PostFixAnalysisReport { get; init; }

    /// <summary>
    /// The package references the analysis was built from. Reporters that need to resolve a
    /// finding back to a project file (SARIF locations, for example) use this; analyzer issues
    /// themselves only carry project names.
    /// </summary>
    public ProjectPackageInfo? PackageInfo { get; init; }

    /// <summary>
    /// The directory the scan was rooted at, used to make reported file paths relative.
    /// </summary>
    public string? BasePath { get; init; }

    /// <summary>
    /// Projects that could not be scanned. Any non-zero value means the analysis is incomplete,
    /// so reporters must not present an empty finding list as a clean result.
    /// </summary>
    public int ScanFailures { get; init; }

    /// <summary>
    /// Opt-in package queries (<c>--audit</c>, <c>--outdated</c>, <c>--deprecated</c>, <c>--licenses</c>) that failed
    /// to return. Tracked apart from <see cref="ScanFailures"/> because the project's references
    /// were still read: the gap is in the extra findings, not in the inventory.
    /// </summary>
    public int DeepScanFailures { get; init; }

    /// <summary>
    /// Projects the scan set out to cover, for reporting the scale of any scan failures.
    /// </summary>
    public int ProjectsDiscovered { get; init; }

    /// <summary>
    /// The baseline file this run read or wrote, when one was involved.
    /// </summary>
    public string? BaselinePath { get; init; }

    /// <summary>
    /// True when the run recorded a baseline rather than gating on the findings.
    /// </summary>
    public bool BaselineWritten { get; init; }

    /// <summary>
    /// Baseline entries that matched no finding this run — debt accepted once and since fixed.
    /// Reported so the file can be pruned; a stale entry suppresses nothing but makes the baseline
    /// look like it is still doing work. Zero when no baseline was used or everything matched.
    /// </summary>
    public int? BaselineStaleEntries { get; init; }

    /// <summary>
    /// Distinct rule IDs cited by the baseline that the current catalog does not know — the mark of
    /// a renamed or deleted rule. Empty when every entry names a live rule.
    /// </summary>
    public IReadOnlyList<string>? BaselineUnknownRuleCodes { get; init; }

    /// <summary>
    /// Baseline fingerprints this run's findings actually matched. Batch-level staleness
    /// accounting needs the union across solutions; never serialized.
    /// </summary>
    public IReadOnlyCollection<string>? BaselineMatchedFingerprints { get; init; }

    /// <summary>
    /// Rules this run could not have judged (opt-in analyzers without data, policy-disabled rules).
    /// Entries citing them are excluded from staleness; batch accounting needs the union.
    /// </summary>
    public IReadOnlyCollection<AnalysisIssueCode>? BaselineUnevaluatedRuleCodes { get; init; }

    /// <summary>
    /// Guidance a completed operation wants carried into machine-readable output. Never serialized
    /// directly — CommandRouter maps these into OperationResult.Warnings.
    /// </summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>
    /// Findings that reached the <c>--fail-on</c> threshold — the subset <see cref="ExitCode"/>
    /// reflects. Recorded here rather than re-derived by reporters: the gate has exceptions (a
    /// successful <c>--fix</c> run does not gate on findings it just repaired), and a second
    /// implementation of the policy drifts from the first.
    /// </summary>
    public int? GatedIssueCount { get; init; }

    /// <summary>
    /// The resolved-graph verification, when <c>--verify</c> was passed. Null otherwise, so a run that
    /// never verified stays distinguishable from one that verified and found nothing — the same
    /// "absence is meaningful" contract the rest of the payload keeps.
    /// </summary>
    public VerificationReport? Verification { get; init; }
}
