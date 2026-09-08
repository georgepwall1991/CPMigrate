using CPMigrate.Services.Remediation;

namespace CPMigrate.Models;

/// <summary>
/// Result of a <c>--remediate</c> run.
///
/// The shape is deliberately a receipt rather than a summary: it records what was planned, what was
/// applied, what verification did to it, and — the part that separates a claim from a proof — what a
/// second vulnerability scan found afterwards.
/// </summary>
public class RemediationResult
{
    /// <summary>Exit code for the operation.</summary>
    public int ExitCode { get; init; }

    /// <summary>Every package the plan considered, with its outcome.</summary>
    public IReadOnlyList<RemediationAction> Actions { get; init; } = [];

    /// <summary>Packages whose fix was written and survived verification.</summary>
    public IReadOnlyList<string> Remediated { get; init; } = [];

    /// <summary>
    /// Packages whose fix was written but reverted because verification went red. Under
    /// <c>--bisect</c> these are the isolated culprits; without it, one failure holds back the set.
    /// </summary>
    public IReadOnlyList<string> HeldBack { get; init; } = [];

    /// <summary>Advisories the SDK reported before remediation ran.</summary>
    public int AdvisoriesBefore { get; init; }

    /// <summary>
    /// Advisories the SDK still reports after remediation, from a fresh scan rather than by
    /// subtraction. Null when no re-scan happened — a dry run, or nothing applied.
    /// </summary>
    public int? AdvisoriesAfter { get; init; }

    /// <summary>Whether the re-scan completed. A scan that did not finish cannot prove anything clean.</summary>
    public bool ReVerified { get; init; }

    /// <summary>Number of restore+test cycles executed.</summary>
    public int VerificationRuns { get; init; }

    /// <summary>Whether bisection stopped early because it exhausted its budget.</summary>
    public bool BisectBudgetExhausted { get; init; }

    /// <summary>Whether the props file was restored to its pre-run state.</summary>
    public bool WasRolledBack { get; init; }

    /// <summary>Whether this was a plan-only run.</summary>
    public bool DryRun { get; init; }

    /// <summary>Guidance carried to machine-readable consumers whose consoles are silenced.</summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>Errors that ended the run early.</summary>
    public IReadOnlyList<string>? Errors { get; init; }
}
