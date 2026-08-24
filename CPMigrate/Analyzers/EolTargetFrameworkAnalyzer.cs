using CPMigrate.Models;
using CPMigrate.Services;

namespace CPMigrate.Analyzers;

/// <summary>
/// Analyzes Target Framework declarations for end-of-life .NET runtimes.
/// A runtime past end of life stops receiving security updates, so every package resolved for it
/// ships into an unsupported target.
/// </summary>
public class EolTargetFrameworkAnalyzer : IAnalyzer
{
    private readonly IProjectFileScanner _projectFileScanner;

    public EolTargetFrameworkAnalyzer(IProjectFileScanner? projectFileScanner = null)
    {
        _projectFileScanner = projectFileScanner ?? new ProjectFileScanner(SilentConsoleService.Instance);
    }

    public string Name => "End-of-Life Target Framework";

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        var issues = new List<AnalysisIssue>();

        foreach (var path in packageInfo.GetProjectsScanned())
        {
            // Every literal declaration is judged, not whichever assignment the file happens to
            // list first: a project can assign TargetFramework more than once, and order-dependent
            // reading would let an inactive or overridden EOL target hide behind a newer one.
            var targets = _projectFileScanner.GetDeclaredTargetFrameworks(path);
            if (targets.Count == 0)
            {
                // Gated on data like its siblings: TFMs that cannot be read are not judged.
                continue;
            }

            var eol = targets
                .Where(IsEndOfLife)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (eol.Count == 0)
            {
                continue;
            }

            var tfmList = string.Join(", ", eol);
            issues.Add(
                new AnalysisIssue(
                    tfmList,
                    $"Project targets end-of-life .NET version(s): {tfmList}. These runtimes no "
                        + "longer receive security updates; retarget to a supported release.",
                    [packageInfo.ProjectId(path)],
                    AnalysisIssueCode.EolTargetFramework,
                    AnalysisSeverity.Moderate,
                    Fixable: false
                )
            );
        }

        return new AnalyzerResult(Name, issues);
    }

    /// <summary>
    /// Whether a declared target is an end-of-life runtime.
    ///
    /// Scope is runtime targets only: <c>netstandard</c> is a compile-time surface rather than a
    /// runtime and <c>uap</c> follows its own lifecycle, while .NET Framework (<c>net48</c> and
    /// friends) is still supported. Matching is case-insensitive because MSBuild accepts any casing.
    /// </summary>
    private static bool IsEndOfLife(string target)
    {
        var normalized = target.ToLowerInvariant();

        if (normalized.StartsWith("netcoreapp", StringComparison.Ordinal))
        {
            return true;
        }

        if (!normalized.StartsWith("net", StringComparison.Ordinal))
        {
            return false;
        }

        var major = normalized["net".Length..].Split('.')[0];
        return major is "5" or "6" or "7" or "9";
    }
}
