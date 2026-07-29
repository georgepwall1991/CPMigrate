namespace CPMigrate.Models;

/// <summary>
/// Stable issue codes for analyzer findings.
/// </summary>
public enum AnalysisIssueCode
{
    Unknown,
    VersionInconsistency,
    DuplicatePackageCasing,
    RedundantReference,
    TransitiveConflict,
    SecurityVulnerability,
    RedundantDirectReference,
    FrameworkAlignment,
    OutdatedPackage,
    DeprecatedPackage,

    /// <summary>A Directory.Packages.props exists without ManagePackageVersionsCentrally enabled.</summary>
    CpmNotEnabled,

    /// <summary>A project pins a version inline while central package management is in force.</summary>
    InlineVersionUnderCpm,

    /// <summary>A referenced package has no version, inline or central — restore will fail.</summary>
    MissingPackageVersion,

    /// <summary>A central PackageVersion entry no project references.</summary>
    OrphanedPackageVersion
}

/// <summary>
/// Severity level for analysis findings.
/// </summary>
public enum AnalysisSeverity
{
    Info,
    Low,
    Moderate,
    High,
    Critical
}

/// <summary>
/// Represents a single issue found by an analyzer.
/// </summary>
/// <param name="PackageName">The name of the package with the issue.</param>
/// <param name="Description">A description of the issue found.</param>
/// <param name="AffectedProjects">List of project names/paths affected by this issue.</param>
/// <param name="IssueCode">Stable issue code for programmatic matching.</param>
/// <param name="Severity">Issue severity for reporting and CI policy decisions.</param>
/// <param name="Fixable">Whether a built-in fixer can address this issue.</param>
/// <param name="Metadata">Optional structured metadata for machine consumers.</param>
/// <param name="Suppressed">
/// Whether a baseline has accepted this finding. Suppressed findings are still reported — the debt
/// stays visible — but they do not fail the build, which is what lets a repository with existing
/// debt adopt a CI gate that only catches new problems.
/// </param>
public record AnalysisIssue(
    string PackageName,
    string Description,
    IReadOnlyList<string> AffectedProjects,
    AnalysisIssueCode IssueCode = AnalysisIssueCode.Unknown,
    AnalysisSeverity Severity = AnalysisSeverity.Info,
    bool Fixable = false,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool Suppressed = false
);
