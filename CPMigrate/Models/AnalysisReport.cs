namespace CPMigrate.Models;

/// <summary>
/// Combined report from all analyzers containing the complete analysis results.
/// </summary>
/// <param name="ProjectsScanned">Number of projects that were scanned.</param>
/// <param name="TotalPackageReferences">Total number of package references found across all projects.</param>
/// <param name="Results">Results from each analyzer.</param>
public record AnalysisReport(
    int ProjectsScanned,
    int TotalPackageReferences,
    IReadOnlyList<AnalyzerResult> Results
)
{
    /// <summary>
    /// Returns the total number of issues found across all analyzers.
    /// </summary>
    public int TotalIssues => Results.Sum(r => r.Issues.Count);

    /// <summary>
    /// Returns true if any analyzer found issues.
    /// </summary>
    public bool HasIssues => TotalIssues > 0;

    /// <summary>
    /// The severity of the worst finding, or null when nothing was found.
    /// </summary>
    public AnalysisSeverity? HighestSeverity =>
        Results
            .SelectMany(result => result.Issues)
            .Select(issue => (AnalysisSeverity?)issue.Severity)
            .Max();

    /// <summary>
    /// Counts findings at or above a severity. This is what a CI gate keys off: a team with
    /// existing informational debt needs to fail on vulnerabilities without failing on everything,
    /// otherwise the gate gets disabled and the real finding lands with the noise.
    /// </summary>
    /// <param name="threshold">The lowest severity that counts.</param>
    public int CountAtOrAbove(AnalysisSeverity threshold)
    {
        return Results.Sum(result =>
            result.Issues.Count(issue => !issue.Suppressed && issue.Severity >= threshold)
        );
    }

    /// <summary>
    /// Findings a baseline has accepted. Counted separately from <see cref="TotalIssues"/> because
    /// they remain visible in every report — accepting debt should not hide it — while being excluded
    /// from the gate.
    /// </summary>
    public int SuppressedCount => Results.Sum(result => result.Issues.Count(issue => issue.Suppressed));


}
