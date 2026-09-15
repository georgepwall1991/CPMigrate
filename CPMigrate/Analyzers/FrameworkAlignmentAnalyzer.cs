using CPMigrate.Models;
using CPMigrate.Services;

namespace CPMigrate.Analyzers;

/// <summary>
/// Analyzes Target Framework divergence across projects.
/// Divergent frameworks can lead to inconsistent package resolution in CPM.
/// </summary>
public class FrameworkAlignmentAnalyzer : IAnalyzer
{
    private readonly IProjectFileScanner _projectFileScanner;

    public FrameworkAlignmentAnalyzer(IProjectFileScanner? projectFileScanner = null)
    {
        _projectFileScanner = projectFileScanner ?? new ProjectFileScanner(SilentConsoleService.Instance);
    }

    public string Name => "Framework Alignment";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        // Each project is identified by its whole declared target set — "net8.0;net10.0" and
        // "net10.0;net8.0" are the same set, not a divergence — and projects whose targets cannot
        // be read (unparseable file, expression-valued TFM) are unexamined rather than reported
        // under a synthetic "Unknown" framework.
        var configurations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var distinctFrameworks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unexamined = new List<string>();

        foreach (var path in packageInfo.References.Select(r => r.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var declared = _projectFileScanner.GetDeclaredTargetFrameworks(path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (declared.Count == 0)
            {
                unexamined.Add(packageInfo.ProjectId(path));
                continue;
            }

            var key = string.Join(';', declared);
            if (!configurations.TryGetValue(key, out var projects))
            {
                projects = new List<string>();
                configurations[key] = projects;
            }

            projects.Add(packageInfo.ProjectId(path));
            distinctFrameworks.UnionWith(declared);
        }

        if (configurations.Count <= 1)
        {
            return new AnalyzerResult(Name, []);
        }

        var tfmList = string.Join(", ", distinctFrameworks);
        var unexaminedNote = unexamined.Count > 0
            ? $" {unexamined.Count} project(s) could not be inspected for their target framework."
            : string.Empty;
        var issue = new AnalysisIssue(
            "Multiple Frameworks",
            $"Repository uses {configurations.Count} different Target Frameworks: {tfmList}.{unexaminedNote} Ensure package versions in Directory.Packages.props are compatible with all.",
            configurations.Values.SelectMany(v => v).ToList(),
            AnalysisIssueCode.FrameworkAlignment,
            AnalysisSeverity.Info,
            Fixable: false);

        return new AnalyzerResult(Name, [issue]);
    }
}
