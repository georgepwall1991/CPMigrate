using CPMigrate.Models;

namespace CPMigrate.Fixers;

/// <summary>
/// Represents a change made to a file.
/// </summary>
/// <param name="FilePath">Full path to the modified file.</param>
/// <param name="ChangeType">Type of change (e.g., "Modified", "Deleted").</param>
/// <param name="Before">Content before the change (for diff display).</param>
/// <param name="After">Content after the change (for diff display).</param>
public record FileChange(
    string FilePath,
    string ChangeType,
    string Before,
    string After
);

/// <summary>
/// Result of applying a fix.
/// </summary>
/// <param name="Success">Whether the fix was applied successfully.</param>
/// <param name="Description">Description of what was fixed.</param>
/// <param name="Changes">List of file changes made.</param>
public record FixResult(
    bool Success,
    string Description,
    IReadOnlyList<FileChange> Changes
)
{
    /// <summary>
    /// Creates a successful fix result.
    /// </summary>
    public static FixResult Succeeded(string description, IReadOnlyList<FileChange> changes)
        => new(true, description, changes);

    /// <summary>
    /// Creates a failed fix result.
    /// </summary>
    public static FixResult Failed(string reason)
        => new(false, reason, Array.Empty<FileChange>());

    /// <summary>
    /// Creates a result indicating no fix was needed.
    /// </summary>
    public static FixResult NoFixNeeded(string reason)
        => new(true, reason, Array.Empty<FileChange>());

    /// <summary>
    /// Creates a result for a fix that changed some files but could not change others.
    ///
    /// Not <see cref="Succeeded"/>: an issue spanning several projects where one write failed is still
    /// present, so counting it as fixed hides the remainder — <see cref="FixReport.GetFailedFixes"/> would
    /// omit it and the summary would say nothing. Not <see cref="Failed"/> with the changes dropped either,
    /// because the files that *were* changed have to be reported and, under a dry run, previewed.
    /// </summary>
    public static FixResult PartiallyApplied(string reason, IReadOnlyList<FileChange> changes)
        => new(false, reason, changes);
}

/// <summary>
/// Complete fix report containing all fix results.
/// </summary>
public class FixReport
{
    public List<FixResult> Results { get; } = new();
    public int TotalFixesApplied => Results.Count(r => r.Success && r.Changes.Count > 0);
    public int TotalFileChanges => Results.Sum(r => r.Changes.Count);
    public bool HasChanges => TotalFileChanges > 0;

    /// <summary>
    /// Gets the failed fix results.
    /// </summary>
    public IReadOnlyList<FixResult> GetFailedFixes() => Results.Where(r => !r.Success).ToList();
}

/// <summary>
/// Interface for fixers that can automatically resolve detected issues.
/// </summary>
public interface IFixer
{
    /// <summary>
    /// The display name for this fixer.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Determines if this fixer can handle the given issue.
    /// </summary>
    /// <param name="issue">The analysis issue to check.</param>
    /// <returns>True if this fixer can fix the issue.</returns>
    bool CanFix(AnalysisIssue issue);

    /// <summary>
    /// Compatibility overload for callers that still pass CLI options directly.
    /// </summary>
    FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun);

    /// <summary>
    /// Applies a fix for the given issue.
    /// </summary>
    /// <param name="issue">The issue to fix.</param>
    /// <param name="packageInfo">Package information from the analysis.</param>
    /// <param name="request">Mode-specific fix request settings.</param>
    /// <returns>Result of the fix operation.</returns>
    FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request);
}
