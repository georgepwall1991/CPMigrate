using CPMigrate.Fixers;
using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services.Migration;

internal sealed class AnalysisHandler
{
    private readonly IProjectAnalyzer _projectAnalyzer;
    private readonly IAnalysisService _analysisService;
    private readonly IFixService _fixService;
    private readonly IConsoleService _consoleService;
    private readonly bool _quietMode;
    private readonly Func<
        Options,
        Task<(string BasePath, List<string> ProjectPaths)>
    > _discoverProjects;

    private Dictionary<string, List<PackageReference>>? _cachedProjectScans;

    public AnalysisHandler(
        IProjectAnalyzer projectAnalyzer,
        IAnalysisService analysisService,
        IFixService fixService,
        IConsoleService consoleService,
        bool quietMode,
        Func<Options, Task<(string BasePath, List<string> ProjectPaths)>> discoverProjects
    )
    {
        _projectAnalyzer = projectAnalyzer;
        _analysisService = analysisService;
        _fixService = fixService;
        _consoleService = consoleService;
        _quietMode = quietMode;
        _discoverProjects = discoverProjects;
    }

    public async Task<MigrationResult> ExecuteAsync(Options options)
    {
        if (!_quietMode)
        {
            _consoleService.Banner("ANALYZE MODE - Scanning for package issues");
            _consoleService.WriteLine();
        }

        var (basePath, projectPaths) = await _discoverProjects(options);
        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to analyze.");
            return new MigrationResult { ExitCode = ExitCodes.NoProjectsFound };
        }

        var (packageInfo, scanFailures, deepScanFailures) = await PerformAnalysisScanAsync(
            options,
            projectPaths
        );

        if (!_quietMode)
        {
            _consoleService.WriteLine();
        }
        ReportScanFailures(scanFailures, deepScanFailures, projectPaths.Count);

        if (!_quietMode)
        {
            _consoleService.WriteAnalysisHeader(
                packageInfo.ProjectCount,
                packageInfo.TotalReferences,
                packageInfo.VulnerabilityCount
            );
        }

        var report = _analysisService.Analyze(packageInfo);

        if (!_quietMode)
        {
            foreach (var result in report.Results)
            {
                _consoleService.WriteAnalyzerResult(result);
            }
        }

        if (!_quietMode)
        {
            _consoleService.WriteAnalysisSummary(report);

            // With --fix pending, these findings may not survive: the gate is decided against a
            // rescan of the modified tree, so announcing a verdict here could contradict the exit
            // code. The post-fix decision is reported once the rescan has happened.
            if (!options.Fix)
            {
                ReportThresholdDecision(options, report);
            }
        }

        return await ApplyAnalysisFixesIfNeededAsync(
            options,
            report,
            packageInfo,
            basePath,
            scanFailures,
            deepScanFailures,
            projectPaths.Count
        );
    }

    private async Task<(
        ProjectPackageInfo PackageInfo,
        int ScanFailures,
        int DeepScanFailures
    )> PerformAnalysisScanAsync(Options options, List<string> projectPaths)
    {
        var allReferences = new List<PackageReference>();
        var allVulnerabilities = new List<VulnerabilityInfo>();
        var allOutdatedPackages = new List<OutdatedPackageInfo>();
        var allDeprecatedPackages = new List<DeprecatedPackageInfo>();
        var scanFailures = 0;
        var deepScanFailures = 0;

        if (_quietMode)
        {
            foreach (var projectPath in projectPaths)
            {
                var (success, deepFailures) = await ScanSingleProjectForAnalysisAsync(
                    options,
                    projectPath,
                    null,
                    allReferences,
                    allVulnerabilities,
                    allOutdatedPackages,
                    allDeprecatedPackages
                );

                deepScanFailures += deepFailures;
                if (!success)
                {
                    scanFailures++;
                }
            }
        }
        else
        {
            await AnsiConsole
                .Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn()
                )
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask(
                        "[cyan]Scanning packages[/]",
                        maxValue: projectPaths.Count
                    );

                    foreach (var projectPath in projectPaths)
                    {
                        var projectName = Path.GetFileName(projectPath);
                        task.Description =
                            $"[cyan]Scanning[/] [white]{Markup.Escape(projectName)}[/]";

                        var (success, deepFailures) = await ScanSingleProjectForAnalysisAsync(
                            options,
                            projectPath,
                            task,
                            allReferences,
                            allVulnerabilities,
                            allOutdatedPackages,
                            allDeprecatedPackages
                        );

                        deepScanFailures += deepFailures;
                        if (!success)
                        {
                            scanFailures++;
                        }

                        task.Increment(1);
                        await Task.Delay(30);
                    }

                    task.Description = "[green]Scan complete[/]";
                });
        }

        return (
            new ProjectPackageInfo(
                allReferences,
                allVulnerabilities,
                allOutdatedPackages,
                allDeprecatedPackages
            ),
            scanFailures,
            deepScanFailures
        );
    }

    private async Task<(
        bool ReferencesScanned,
        int DeepScanFailures
    )> ScanSingleProjectForAnalysisAsync(
        Options options,
        string projectPath,
        ProgressTask? task,
        List<PackageReference> allReferences,
        List<VulnerabilityInfo> allVulnerabilities,
        List<OutdatedPackageInfo> allOutdatedPackages,
        List<DeprecatedPackageInfo> allDeprecatedPackages
    )
    {
        var (references, success) = await ScanProjectReferencesAsync(options, projectPath);
        allReferences.AddRange(references);
        CacheScanResults(projectPath, references);

        var deepScanFailures = 0;
        if (options.AuditSecurity || options.AnalyzeOutdated || options.AnalyzeDeprecated)
        {
            deepScanFailures = await RunDeepScansAsync(
                options,
                projectPath,
                task,
                allVulnerabilities,
                allOutdatedPackages,
                allDeprecatedPackages
            );
        }

        return (success, deepScanFailures);
    }

    private async Task<(
        List<PackageReference> References,
        bool Success
    )> ScanProjectReferencesAsync(Options options, string projectPath)
    {
        var (references, success) = await _projectAnalyzer.ScanResolvedPackagesAsync(
            projectPath,
            options.IncludeTransitive
        );
        if (!success && !options.IncludeTransitive)
        {
            (references, success) = _projectAnalyzer.ScanProjectPackages(projectPath);
        }

        return (references, success);
    }

    private void CacheScanResults(string projectPath, List<PackageReference> references)
    {
        _cachedProjectScans ??= new Dictionary<string, List<PackageReference>>(
            StringComparer.OrdinalIgnoreCase
        );
        _cachedProjectScans[projectPath] = references;
    }

    /// <summary>
    /// Runs the opt-in package queries. Returns the number that did not complete: a failed audit
    /// or inventory query yields no findings, which is indistinguishable from a clean result
    /// unless the failure is counted.
    /// </summary>
    private async Task<int> RunDeepScansAsync(
        Options options,
        string projectPath,
        ProgressTask? task,
        List<VulnerabilityInfo> allVulnerabilities,
        List<OutdatedPackageInfo> allOutdatedPackages,
        List<DeprecatedPackageInfo> allDeprecatedPackages
    )
    {
        var projectName = Path.GetFileName(projectPath);
        if (task != null)
        {
            task.Description = $"[cyan]Deep scanning[/] [white]{Markup.Escape(projectName)}[/]";
        }

        var failures = 0;

        if (
            options.AuditSecurity
            && !await ScanVulnerabilitiesAsync(projectPath, allVulnerabilities)
        )
        {
            failures++;
        }

        if (
            options.AnalyzeOutdated
            && !await ScanOutdatedPackagesAsync(options, projectPath, allOutdatedPackages)
        )
        {
            failures++;
        }

        if (
            options.AnalyzeDeprecated
            && !await ScanDeprecatedPackagesAsync(options, projectPath, allDeprecatedPackages)
        )
        {
            failures++;
        }

        return failures;
    }

    private async Task<bool> ScanVulnerabilitiesAsync(
        string projectPath,
        List<VulnerabilityInfo> allVulnerabilities
    )
    {
        var (vulnerabilities, auditSuccess) = await _projectAnalyzer.ScanVulnerabilitiesAsync(
            projectPath
        );
        if (auditSuccess)
        {
            allVulnerabilities.AddRange(vulnerabilities);
        }

        return auditSuccess;
    }

    private async Task<bool> ScanOutdatedPackagesAsync(
        Options options,
        string projectPath,
        List<OutdatedPackageInfo> allOutdatedPackages
    )
    {
        var (outdated, outdatedSuccess) = await _projectAnalyzer.ScanOutdatedPackagesAsync(
            projectPath,
            options.IncludeTransitive,
            options.IncludePrerelease
        );
        if (outdatedSuccess)
        {
            allOutdatedPackages.AddRange(outdated);
        }

        return outdatedSuccess;
    }

    private async Task<bool> ScanDeprecatedPackagesAsync(
        Options options,
        string projectPath,
        List<DeprecatedPackageInfo> allDeprecatedPackages
    )
    {
        var (deprecated, deprecatedSuccess) = await _projectAnalyzer.ScanDeprecatedPackagesAsync(
            projectPath,
            options.IncludeTransitive,
            options.IncludePrerelease
        );
        if (deprecatedSuccess)
        {
            allDeprecatedPackages.AddRange(deprecated);
        }

        return deprecatedSuccess;
    }

    private void ReportScanFailures(int scanFailures, int deepScanFailures, int totalProjects)
    {
        if (deepScanFailures > 0)
        {
            // Previously silent: a failed audit or inventory query simply contributed no findings,
            // which reads identically to a clean result.
            _consoleService.Warning(
                $"{deepScanFailures} package quer(ies) failed (--audit/--outdated/--deprecated); "
                    + "those findings are missing, not absent."
            );
        }

        if (scanFailures > 0)
        {
            var failureRate = (double)scanFailures / totalProjects * 100;
            _consoleService.Warning(
                $"{scanFailures} of {totalProjects} projects ({failureRate:F0}%) failed to scan."
            );
            if (failureRate > 50)
            {
                _consoleService.Warning(
                    "High failure rate detected - analysis results may be incomplete."
                );
            }
        }
    }

    private async Task<MigrationResult> ApplyAnalysisFixesIfNeededAsync(
        Options options,
        AnalysisReport report,
        ProjectPackageInfo packageInfo,
        string basePath,
        int scanFailures,
        int deepScanFailures,
        int projectsDiscovered
    )
    {
        FixReport? fixReport = null;

        if ((options.Fix || options.FixDryRun) && report.HasIssues)
        {
            if (!_quietMode)
            {
                _consoleService.WriteLine();
                _consoleService.Banner(
                    options.FixDryRun ? "FIX DRY RUN - Showing proposed changes" : "APPLYING FIXES"
                );
                _consoleService.WriteLine();
            }

            fixReport = _fixService.ApplyFixes(report, packageInfo, options, options.FixDryRun);

            if (fixReport.HasChanges && !options.FixDryRun)
            {
                // The fixes were written, so the pre-fix report no longer describes the tree. The
                // gate has to run against what is actually on disk now: an issue's Fixable flag says
                // a fixer *exists*, not that it ran or succeeded, so trusting it would let an
                // unrepaired High finding exit successfully. Re-scanning is the only honest answer.
                var (postFixReport, postFixScanFailures, postFixDeepScanFailures) =
                    await ReanalyzeAfterFixesAsync(options, projectPaths: null);

                if (!_quietMode)
                {
                    _consoleService.Dim(
                        $"After fixes: {postFixReport.TotalIssues} finding(s) remain."
                    );
                    ReportThresholdDecision(options, postFixReport);
                }

                return new MigrationResult
                {
                    ProjectsProcessed = packageInfo.ProjectCount,
                    PackagesCentralized = packageInfo.TotalReferences,
                    AnalysisReport = report,
                    PostFixAnalysisReport = postFixReport,
                    FixReport = fixReport,
                    PackageInfo = packageInfo,
                    BasePath = basePath,
                    ScanFailures = postFixScanFailures,
                    DeepScanFailures = postFixDeepScanFailures,
                    ProjectsDiscovered = projectsDiscovered,
                    GatedIssueCount = CountGatedIssues(postFixReport, options.FailOn),
                    ExitCode = ResolveExitCodeAfterFixes(
                        postFixReport,
                        fixReport,
                        options.FailOn,
                        postFixScanFailures,
                        postFixDeepScanFailures
                    ),
                };
            }
        }

        return await Task.FromResult(
            new MigrationResult
            {
                ProjectsProcessed = packageInfo.ProjectCount,
                PackagesCentralized = packageInfo.TotalReferences,
                AnalysisReport = report,
                FixReport = fixReport,
                PackageInfo = packageInfo,
                BasePath = basePath,
                ScanFailures = scanFailures,
                DeepScanFailures = deepScanFailures,
                ProjectsDiscovered = projectsDiscovered,
                GatedIssueCount = CountGatedIssues(report, options.FailOn),
                ExitCode = ResolveExitCode(report, options.FailOn, scanFailures, deepScanFailures),
            }
        );
    }

    /// <summary>
    /// Chooses the exit code for an analysis run.
    ///
    /// Findings only fail the build when they reach the <c>--fail-on</c> threshold, so a team can
    /// gate on vulnerabilities without gating on informational debt. An incomplete scan still
    /// reports <see cref="ExitCodes.IncompleteAnalysis"/> regardless of the threshold: zero
    /// findings from a scan that did not finish is an unknown, not a clean result, and no severity
    /// setting should be able to hide an unexamined project.
    /// </summary>
    private static int ResolveExitCode(
        AnalysisReport report,
        FailOnSeverity failOn,
        int scanFailures,
        int deepScanFailures
    )
    {
        if (ReachesFailureThreshold(report, failOn))
        {
            return ExitCodes.AnalysisIssuesFound;
        }

        return scanFailures > 0 || deepScanFailures > 0
            ? ExitCodes.IncompleteAnalysis
            : ExitCodes.Success;
    }

    /// <summary>
    /// Explains the gate decision whenever a non-default threshold is in play. Without this, a run
    /// that prints "7 issues found" and then exits 0 looks like a bug rather than a policy.
    /// </summary>
    private void ReportThresholdDecision(Options options, AnalysisReport report)
    {
        if (options.FailOn == FailOnSeverity.Info || !report.HasIssues)
        {
            return;
        }

        var gating = ReachesFailureThreshold(report, options.FailOn);
        if (options.FailOn == FailOnSeverity.Never)
        {
            _consoleService.Dim(
                $"--fail-on Never: reporting {report.TotalIssues} finding(s) without failing the build."
            );
            return;
        }

        var counted = report.CountAtOrAbove((AnalysisSeverity)options.FailOn);
        _consoleService.Dim(
            gating
                ? $"--fail-on {options.FailOn}: {counted} of {report.TotalIssues} finding(s) meet the threshold."
                : $"--fail-on {options.FailOn}: none of {report.TotalIssues} finding(s) reach the threshold "
                    + $"(worst is {report.HighestSeverity}), so the build is not failed."
        );
    }

    /// <summary>
    /// Returns true when at least one finding is at or above the configured threshold.
    /// </summary>
    private static bool ReachesFailureThreshold(AnalysisReport report, FailOnSeverity failOn)
    {
        return CountGatedIssues(report, failOn) > 0;
    }

    /// <summary>
    /// Findings that reach the threshold. <see cref="FailOnSeverity.Never"/> sits above every real
    /// severity, so nothing counts.
    /// </summary>
    private static int CountGatedIssues(AnalysisReport report, FailOnSeverity failOn)
    {
        return failOn == FailOnSeverity.Never ? 0 : report.CountAtOrAbove((AnalysisSeverity)failOn);
    }

    /// <summary>
    /// Re-scans the tree after fixes have been written, so the gate is evaluated against reality
    /// rather than against the report that prompted the fixes.
    /// </summary>
    private async Task<(
        AnalysisReport Report,
        int ScanFailures,
        int DeepScanFailures
    )> ReanalyzeAfterFixesAsync(Options options, List<string>? projectPaths)
    {
        var paths = projectPaths;
        if (paths is null)
        {
            var (_, discovered) = await _discoverProjects(options);
            paths = discovered;
        }

        // The cache holds pre-fix references; the point of this pass is to read the new files.
        _cachedProjectScans = null;

        var (packageInfo, scanFailures, deepScanFailures) = await PerformAnalysisScanAsync(
            options,
            paths
        );

        return (_analysisService.Analyze(packageInfo), scanFailures, deepScanFailures);
    }

    /// <summary>
    /// Chooses the exit code for a run that wrote fixes. A fix that failed to apply is a failure
    /// regardless of severity; otherwise the gate applies to whatever the fixers could not repair.
    /// </summary>
    private static int ResolveExitCodeAfterFixes(
        AnalysisReport report,
        FixReport fixReport,
        FailOnSeverity failOn,
        int scanFailures,
        int deepScanFailures
    )
    {
        if (fixReport.GetFailedFixes().Count > 0)
        {
            return ExitCodes.AnalysisIssuesFound;
        }

        if (CountGatedIssues(report, failOn) > 0)
        {
            return ExitCodes.AnalysisIssuesFound;
        }

        return scanFailures > 0 || deepScanFailures > 0
            ? ExitCodes.IncompleteAnalysis
            : ExitCodes.Success;
    }
}
