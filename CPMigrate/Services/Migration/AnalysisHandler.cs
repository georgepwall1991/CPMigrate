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
    private readonly Func<Options, Task<(string BasePath, List<string> ProjectPaths)>> _discoverProjects;

    private Dictionary<string, List<PackageReference>>? _cachedProjectScans;

    public AnalysisHandler(
        IProjectAnalyzer projectAnalyzer,
        IAnalysisService analysisService,
        IFixService fixService,
        IConsoleService consoleService,
        bool quietMode,
        Func<Options, Task<(string BasePath, List<string> ProjectPaths)>> discoverProjects)
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

        var (_, projectPaths) = await _discoverProjects(options);
        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to analyze.");
            return new MigrationResult { ExitCode = ExitCodes.NoProjectsFound };
        }

        var (packageInfo, scanFailures) = await PerformAnalysisScanAsync(options, projectPaths);

        if (!_quietMode)
        {
            _consoleService.WriteLine();
        }
        ReportScanFailures(scanFailures, projectPaths.Count);

        if (!_quietMode)
        {
            _consoleService.WriteAnalysisHeader(packageInfo.ProjectCount, packageInfo.TotalReferences, packageInfo.VulnerabilityCount);
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
        }

        return await ApplyAnalysisFixesIfNeededAsync(options, report, packageInfo);
    }

    private async Task<(ProjectPackageInfo PackageInfo, int ScanFailures)> PerformAnalysisScanAsync(
        Options options,
        List<string> projectPaths)
    {
        var allReferences = new List<PackageReference>();
        var allVulnerabilities = new List<VulnerabilityInfo>();
        var allOutdatedPackages = new List<OutdatedPackageInfo>();
        var allDeprecatedPackages = new List<DeprecatedPackageInfo>();
        var scanFailures = 0;

        if (_quietMode)
        {
            foreach (var projectPath in projectPaths)
            {
                var success = await ScanSingleProjectForAnalysisAsync(
                    options,
                    projectPath,
                    null,
                    allReferences,
                    allVulnerabilities,
                    allOutdatedPackages,
                    allDeprecatedPackages);

                if (!success)
                {
                    scanFailures++;
                }
            }
        }
        else
        {
            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[cyan]Scanning packages[/]", maxValue: projectPaths.Count);

                    foreach (var projectPath in projectPaths)
                    {
                        var projectName = Path.GetFileName(projectPath);
                        task.Description = $"[cyan]Scanning[/] [white]{Markup.Escape(projectName)}[/]";

                        var success = await ScanSingleProjectForAnalysisAsync(
                            options,
                            projectPath,
                            task,
                            allReferences,
                            allVulnerabilities,
                            allOutdatedPackages,
                            allDeprecatedPackages);

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

        return (new ProjectPackageInfo(allReferences, allVulnerabilities, allOutdatedPackages, allDeprecatedPackages), scanFailures);
    }

    private async Task<bool> ScanSingleProjectForAnalysisAsync(
        Options options,
        string projectPath,
        ProgressTask? task,
        List<PackageReference> allReferences,
        List<VulnerabilityInfo> allVulnerabilities,
        List<OutdatedPackageInfo> allOutdatedPackages,
        List<DeprecatedPackageInfo> allDeprecatedPackages)
    {
        var (references, success) = await ScanProjectReferencesAsync(options, projectPath);
        allReferences.AddRange(references);
        CacheScanResults(projectPath, references);

        if (options.AuditSecurity || options.AnalyzeOutdated || options.AnalyzeDeprecated)
        {
            await RunDeepScansAsync(options, projectPath, task, allVulnerabilities, allOutdatedPackages, allDeprecatedPackages);
        }

        return success;
    }

    private async Task<(List<PackageReference> References, bool Success)> ScanProjectReferencesAsync(Options options, string projectPath)
    {
        var (references, success) = await _projectAnalyzer.ScanResolvedPackagesAsync(projectPath, options.IncludeTransitive);
        if (!success && !options.IncludeTransitive)
        {
            (references, success) = _projectAnalyzer.ScanProjectPackages(projectPath);
        }

        return (references, success);
    }

    private void CacheScanResults(string projectPath, List<PackageReference> references)
    {
        _cachedProjectScans ??= new Dictionary<string, List<PackageReference>>(StringComparer.OrdinalIgnoreCase);
        _cachedProjectScans[projectPath] = references;
    }

    private async Task RunDeepScansAsync(
        Options options,
        string projectPath,
        ProgressTask? task,
        List<VulnerabilityInfo> allVulnerabilities,
        List<OutdatedPackageInfo> allOutdatedPackages,
        List<DeprecatedPackageInfo> allDeprecatedPackages)
    {
        var projectName = Path.GetFileName(projectPath);
        if (task != null)
        {
            task.Description = $"[cyan]Deep scanning[/] [white]{Markup.Escape(projectName)}[/]";
        }

        if (options.AuditSecurity)
        {
            await ScanVulnerabilitiesAsync(projectPath, allVulnerabilities);
        }

        if (options.AnalyzeOutdated)
        {
            await ScanOutdatedPackagesAsync(options, projectPath, allOutdatedPackages);
        }

        if (options.AnalyzeDeprecated)
        {
            await ScanDeprecatedPackagesAsync(options, projectPath, allDeprecatedPackages);
        }
    }

    private async Task ScanVulnerabilitiesAsync(string projectPath, List<VulnerabilityInfo> allVulnerabilities)
    {
        var (vulnerabilities, auditSuccess) = await _projectAnalyzer.ScanVulnerabilitiesAsync(projectPath);
        if (auditSuccess)
        {
            allVulnerabilities.AddRange(vulnerabilities);
        }
    }

    private async Task ScanOutdatedPackagesAsync(Options options, string projectPath, List<OutdatedPackageInfo> allOutdatedPackages)
    {
        var (outdated, outdatedSuccess) = await _projectAnalyzer.ScanOutdatedPackagesAsync(
            projectPath,
            options.IncludeTransitive,
            options.IncludePrerelease);
        if (outdatedSuccess)
        {
            allOutdatedPackages.AddRange(outdated);
        }
    }

    private async Task ScanDeprecatedPackagesAsync(Options options, string projectPath, List<DeprecatedPackageInfo> allDeprecatedPackages)
    {
        var (deprecated, deprecatedSuccess) = await _projectAnalyzer.ScanDeprecatedPackagesAsync(
            projectPath,
            options.IncludeTransitive,
            options.IncludePrerelease);
        if (deprecatedSuccess)
        {
            allDeprecatedPackages.AddRange(deprecated);
        }
    }

    private void ReportScanFailures(int scanFailures, int totalProjects)
    {
        if (scanFailures > 0)
        {
            var failureRate = (double)scanFailures / totalProjects * 100;
            _consoleService.Warning($"{scanFailures} of {totalProjects} projects ({failureRate:F0}%) failed to scan.");
            if (failureRate > 50)
            {
                _consoleService.Warning("High failure rate detected - analysis results may be incomplete.");
            }
        }
    }

    private async Task<MigrationResult> ApplyAnalysisFixesIfNeededAsync(
        Options options,
        AnalysisReport report,
        ProjectPackageInfo packageInfo)
    {
        FixReport? fixReport = null;

        if ((options.Fix || options.FixDryRun) && report.HasIssues)
        {
            if (!_quietMode)
            {
                _consoleService.WriteLine();
                _consoleService.Banner(options.FixDryRun ? "FIX DRY RUN - Showing proposed changes" : "APPLYING FIXES");
                _consoleService.WriteLine();
            }

            fixReport = _fixService.ApplyFixes(report, packageInfo, options, options.FixDryRun);

            if (fixReport.HasChanges && !options.FixDryRun)
            {
                return new MigrationResult
                {
                    ProjectsProcessed = packageInfo.ProjectCount,
                    PackagesCentralized = packageInfo.TotalReferences,
                    AnalysisReport = report,
                    FixReport = fixReport,
                    ExitCode = fixReport.GetFailedFixes().Count > 0
                        ? ExitCodes.AnalysisIssuesFound
                        : ExitCodes.Success
                };
            }
        }

        return await Task.FromResult(new MigrationResult
        {
            ProjectsProcessed = packageInfo.ProjectCount,
            PackagesCentralized = packageInfo.TotalReferences,
            AnalysisReport = report,
            FixReport = fixReport,
            ExitCode = report.HasIssues ? ExitCodes.AnalysisIssuesFound : ExitCodes.Success
        });
    }
}
