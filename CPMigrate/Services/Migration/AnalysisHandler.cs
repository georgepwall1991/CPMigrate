using System.Globalization;
using CPMigrate.Fixers;
using CPMigrate.Licensing;
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
    private readonly BaselineService _baselineService = new();
    private readonly Func<
        Options,
        Task<(string BasePath, List<string> ProjectPaths)>
    > _discoverProjects;
    private readonly LicenseScanService _licenseScanService;

    private Dictionary<string, List<PackageReference>>? _cachedProjectScans;

    public AnalysisHandler(
        IProjectAnalyzer projectAnalyzer,
        IAnalysisService analysisService,
        IFixService fixService,
        IConsoleService consoleService,
        bool quietMode,
        Func<Options, Task<(string BasePath, List<string> ProjectPaths)>> discoverProjects,
        LicenseScanService? licenseScanService = null
    )
    {
        _projectAnalyzer = projectAnalyzer;
        _analysisService = analysisService;
        _fixService = fixService;
        _consoleService = consoleService;
        _quietMode = quietMode;
        _discoverProjects = discoverProjects;
        _licenseScanService = licenseScanService ?? new LicenseScanService();
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
            projectPaths,
            basePath
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

        // Ahead of the baseline, and ahead of --write-baseline: a rule a team has switched off
        // produces no findings, so none of its findings belong in a file recording accepted debt.
        var rulePolicy = options.ResolveRulePolicy();
        report = rulePolicy.Apply(report);
        ReportRulePolicy(rulePolicy);

        if (options.WriteBaseline)
        {
            return await WriteBaselineAsync(
                options,
                report,
                packageInfo,
                basePath,
                scanFailures,
                deepScanFailures,
                projectPaths.Count
            );
        }

        var (baselinedReport, baselineError) = ApplyBaseline(options, report);
        if (baselineError is not null)
        {
            _consoleService.Error(baselineError);
            return new MigrationResult
            {
                AnalysisReport = report,
                PackageInfo = packageInfo,
                BasePath = basePath,
                ExitCode = ExitCodes.ValidationError,
            };
        }

        report = baselinedReport;

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

    /// <summary>
    /// Scans every project: references first, sequentially, then the opt-in package queries
    /// concurrently.
    ///
    /// The split is not arbitrary. Reading references goes through MSBuild's object model, whose
    /// static caches are not thread-safe — running it concurrently produced projects reporting each
    /// other's package versions, which silently *erased* version-inconsistency findings rather than
    /// crashing. That is the worst possible failure for an analyzer, so that pass stays serial.
    ///
    /// The expensive part is the other one: <c>--audit</c>, <c>--outdated</c>, and
    /// <c>--deprecated</c> each shell out to <c>dotnet package list</c> and wait on the network, once
    /// per project. Those are separate processes with no shared state, and they dominate the wall
    /// clock on any solution large enough to care — so that is where the concurrency goes.
    ///
    /// Results are merged in project order regardless of completion order, so the report is identical
    /// run to run.
    /// </summary>
    private async Task<(
        ProjectPackageInfo PackageInfo,
        int ScanFailures,
        int DeepScanFailures
    )> PerformAnalysisScanAsync(Options options, List<string> projectPaths, string basePath)
    {
        var results = new ProjectScanResult[projectPaths.Count];

        if (_quietMode)
        {
            await ScanProjectsAsync(options, projectPaths, results, progress: null);
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
                        $"[cyan]Scanning {projectPaths.Count} project(s)[/]",
                        maxValue: projectPaths.Count
                    );

                    await ScanProjectsAsync(options, projectPaths, results, task);

                    task.Description = "[green]Scan complete[/]";
                });
        }

        var (packageInfo, deepScanFailures) = AttachLicenseScan(
            options,
            new ProjectPackageInfo(
                results.SelectMany(r => r.References).ToList(),
                results.SelectMany(r => r.Vulnerabilities).ToList(),
                results.SelectMany(r => r.Outdated).ToList(),
                results.SelectMany(r => r.Deprecated).ToList(),
                basePath,
                projectPaths,
                CollectDeclaredReferences(results)
            ),
            results.Sum(r => r.DeepScanFailures)
        );

        // A project whose declarations could not be read was not fully examined — RedundantReference
        // could not run against it. Counting only resolved-scan failures let that pass as a clean,
        // complete report with a success exit code, which is the failure mode this release is about.
        // Counted once per project, not twice, when both reads failed.
        return (
            packageInfo,
            results.Count(r => !r.ReferencesScanned || r.DeclaredReferences is null),
            deepScanFailures
        );
    }

    /// <summary>
    /// <c>--licenses</c> reads nuspecs once for the whole scan. Missing files are deep-scan
    /// failures, the same contract as a failed <c>--audit</c> query: unknown is not clean.
    /// </summary>
    private (ProjectPackageInfo PackageInfo, int DeepScanFailures) AttachLicenseScan(
        Options options,
        ProjectPackageInfo packageInfo,
        int deepScanFailures
    )
    {
        if (!options.AnalyzeLicenses)
        {
            return (packageInfo, deepScanFailures);
        }

        var licenseScan = _licenseScanService.Scan(packageInfo.References, options.IncludeTransitive);
        return (packageInfo with { Licenses = licenseScan.Licenses }, deepScanFailures + licenseScan.Failures);
    }

    /// <summary>
    /// Merges the per-project declaration scans, and says so when some of them failed.
    ///
    /// A partial failure cannot be papered over by falling back to the resolved list for the missing
    /// projects: the resolved list has already collapsed the duplicates the declarations exist to reveal,
    /// so substituting it would answer a question it cannot answer. The affected projects are simply not
    /// examined for duplicate declarations — which is the honest outcome, but only if it is said out loud.
    /// Left silent, "no duplicates found" would be indistinguishable from "we could not look".
    /// </summary>
    private List<PackageReference>? CollectDeclaredReferences(ProjectScanResult[] results)
    {
        var unread = results.Count(result => result.DeclaredReferences is null);

        if (unread > 0)
        {
            _consoleService.Warning(
                $"Could not read package declarations from {unread} of {results.Length} project file(s); "
                    + "those projects were not checked for duplicate references."
            );
        }

        if (unread == results.Length)
        {
            // Nothing could be read at all. GetDeclaredReferences falls back to the resolved list, which
            // is the pre-3.21.0 behaviour: duplicates stay invisible rather than being misreported. The
            // warning above is emitted first — returning early used to skip it, which is the silence this
            // release exists to remove.
            return null;
        }

        return results
            .Where(result => result.DeclaredReferences is not null)
            .SelectMany(result => result.DeclaredReferences!)
            .ToList();
    }

    /// <summary>
    /// Gathers references, then runs the opt-in package queries with bounded concurrency.
    ///
    /// The reference pass is serial in both halves, for two different reasons — see the notes inside. The
    /// opt-in queries are the part that is safe to parallelise, and they are.
    /// </summary>
    private async Task ScanProjectsAsync(
        Options options,
        List<string> projectPaths,
        ProjectScanResult[] results,
        ProgressTask? progress
    )
    {
        var deepScansRequested =
            options.AuditSecurity || options.AnalyzeOutdated || options.AnalyzeDeprecated;

        // Phase one: the subprocess per project, concurrent.
        //
        // `dotnet package list` restores, and its assets file goes in the project's obj. Two projects in one
        // directory therefore share a project.assets.json and cannot be queried at once: the loser reports
        // the other project's packages, and a version-inconsistency finding vanishes with a clean exit code.
        // Verified with two projects in one directory pinned to 13.0.1 and 12.0.3 — both reported 13.0.1.
        //
        // Those projects are serialised against each other and nothing else is. 3.26.0 instead redirected
        // every invocation to a private intermediate directory, and that was wrong: the redirection has to
        // go in as environment variables, an environment property applies to the whole build graph rather
        // than the one project, and so restoring anything with a ProjectReference told its references to
        // write to the same place. They collided inside a single invocation and the query returned no
        // frameworks at all. On Serilog that silently lost three of six projects, and it reproduced with no
        // concurrency at all — the redirection alone did it.
        //
        // A project's own directory is a fact rather than a prediction, which is the difference between this
        // and the two attempts that failed. It does not cover a project that redirects its obj to somewhere
        // another project also uses; that needs full MSBuild evaluation, it is what 3.24.0 spent eight
        // review rounds failing to answer, and it is rarer than the layout this does cover.
        // Grouping by directory misses a project that redirects its obj to somewhere another project also
        // writes. That case is checked for separately and answered by running the whole scan serially —
        // conservatively, since the check is a heuristic rather than a guarantee.
        var maxConcurrency = options.ResolveScanParallelism();
        var resolved = new (List<PackageReference> References, bool Success)[projectPaths.Count];

        // Scheduled by directory rather than guarded by a lock. Projects in one directory run in sequence
        // within their group; the groups run against each other.
        //
        // A lock was the first shape and it blocked badly: Parallel.ForEachAsync would start several
        // projects from the same directory, all but one would sit waiting on the semaphore while still
        // holding a worker and a global scan slot, and unrelated directories could not start at all. On a
        // solution with a few crowded directories that is close to serial. Grouping avoids the queue
        // entirely — nothing ever waits for a directory, because only one project from it is ever in flight.
        var groups = Enumerable
            .Range(0, projectPaths.Count)
            .GroupBy(
                index => ProjectDirectoryScanLock.DirectoryKeyFor(projectPaths[index]),
                StringComparer.OrdinalIgnoreCase
            )
            .ToList();

        // A solution that redirects intermediate output somewhere shared cannot be grouped by directory at
        // all, because the sharing is not visible in the paths. Those run one project at a time and, since
        // --batch-parallel has other solutions running too, hold a process-wide lock while they do.
        //
        // Ordinary scans take the shared side of a second lock; a scan that might redirect takes it
        // exclusively. Excluding only other redirecting scans was not enough — a redirect aimed at an
        // ordinary project's directory races that project's restore, and directory keys cannot see it,
        // because the sharing is not in the paths.
        var mightRedirect = ProjectDirectoryScanLock.MightRedirectIntermediateOutput(projectPaths);
        using var redirectLock = mightRedirect
            ? await ProjectDirectoryScanLock.AcquireRedirectingAsync()
            : await ProjectDirectoryScanLock.AcquireOrdinaryAsync();

        await Parallel.ForEachAsync(
            groups,
            new ParallelOptions { MaxDegreeOfParallelism = mightRedirect ? 1 : maxConcurrency },
            async (group, _) =>
            {
                foreach (var index in group)
                {
                    using var slot = await ScanConcurrencyGate.AcquireAsync(maxConcurrency);

                    // Grouping keeps one solution's same-directory projects in sequence; this keeps them in
                    // sequence against other solutions', which --batch-parallel runs at the same time.
                    using var directorySlot = await ProjectDirectoryScanLock.AcquireDirectoryAsync(
                        projectPaths[index]
                    );

                    resolved[index] = await _projectAnalyzer.ScanResolvedPackagesAsync(
                        projectPaths[index],
                        options.IncludeTransitive
                    );
                }
            }
        );

        // Phase two: MSBuild. Serial, and deliberately so — see above.
        for (var index = 0; index < projectPaths.Count; index++)
        {
            var (references, scanned, declared) = ReadProjectFileParts(
                options,
                projectPaths[index],
                resolved[index]
            );
            CacheScanResults(projectPaths[index], references);

            results[index] = new ProjectScanResult(scanned, 0, references, [], [], [], declared);

            if (!deepScansRequested)
            {
                progress?.Increment(1);
            }
        }

        if (!deepScansRequested)
        {
            return;
        }

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency };

        // Grouped exactly as the resolved scan is: these restore too, so two projects in one directory
        // corrupt each other here as well — and here the consequence is a vulnerability attributed to the
        // wrong project, or a vulnerable one reported clean.
        var deepGroups = Enumerable
            .Range(0, projectPaths.Count)
            .GroupBy(
                index => ProjectDirectoryScanLock.DirectoryKeyFor(projectPaths[index]),
                StringComparer.OrdinalIgnoreCase
            )
            .ToList();

        await Parallel.ForEachAsync(
            deepGroups,
            parallelOptions,
            async (group, _) =>
            {
                foreach (var index in group)
                {
                    // The ceiling has to hold across the whole process, not per scan: --batch-parallel
                    // runs several solutions at once, and a per-scan limit would multiply the advertised
                    // cap by the number of solutions — producing the feed rate-limiting the cap exists to
                    // avoid.
                    using var slot = await ScanConcurrencyGate.AcquireAsync(maxConcurrency);
                    using var directorySlot = await ProjectDirectoryScanLock.AcquireDirectoryAsync(
                        projectPaths[index]
                    );

                    var vulnerabilities = new List<VulnerabilityInfo>();
                    var outdated = new List<OutdatedPackageInfo>();
                    var deprecated = new List<DeprecatedPackageInfo>();

                    var failures = await RunDeepScansAsync(
                        options,
                        projectPaths[index],
                        task: null,
                        vulnerabilities,
                        outdated,
                        deprecated
                    );

                    results[index] = results[index] with
                    {
                        DeepScanFailures = failures,
                        Vulnerabilities = vulnerabilities,
                        Outdated = outdated,
                        Deprecated = deprecated,
                    };

                    progress?.Increment(1);
                }
            }
        );
    }

    /// <summary>
    /// One project's scan output, kept separate until every scan has finished so the merge can be
    /// ordered by project rather than by whichever scan won the race.
    /// </summary>
    private sealed record ProjectScanResult(
        bool ReferencesScanned,
        int DeepScanFailures,
        List<PackageReference> References,
        List<VulnerabilityInfo> Vulnerabilities,
        List<OutdatedPackageInfo> Outdated,
        List<DeprecatedPackageInfo> Deprecated,
        List<PackageReference>? DeclaredReferences
    );

    /// <summary>
    /// Re-runs, one at a time, any project whose restore did not land where it was told to.
    ///
    /// The assets file being absent from the isolated directory means the redirection was overridden — by a
    /// <c>Directory.Packages.props</c>, a custom SDK, a <c>DirectoryPackagesPropsPath</c>, or something not
    /// yet thought of. Whatever the cause, that restore wrote somewhere shared and may have collided with
    /// another project running at the same time, so its result is thrown away rather than trusted.
    ///
    /// Serially and without isolation, which is what the scan did before any of this: correct, just slower,
    /// and only for the projects that actually need it.
    /// </summary>
    /// <summary>
    /// The MSBuild half of the reference pass: the XML fallback when the resolved scan failed, and the
    /// declaration read. Kept separate from the subprocess so that only this part has to be serialised.
    /// </summary>
    private (
        List<PackageReference> References,
        bool Success,
        List<PackageReference>? Declared
    ) ReadProjectFileParts(
        Options options,
        string projectPath,
        (List<PackageReference> References, bool Success) resolved
    )
    {
        // Never null in practice — but a null list here surfaced as a NullReferenceException inside a LINQ
        // merge two frames away, which is a poor way to learn that a scan returned nothing.
        var (references, success) = (resolved.References ?? [], resolved.Success);

        if (!success)
        {
            if (options.IncludeTransitive)
            {
                // Deliberately does not touch the project XML. That scan cannot see transitive packages,
                // so standing in for a failed --transitive scan would turn "we could not look" into "there
                // is nothing there". The run reports an incomplete analysis instead.
                return (references, false, null);
            }

            (references, success) = _projectAnalyzer.ScanProjectPackages(projectPath);

            // An empty fallback is not a substitute for a failed scan, it is the same failure wearing a
            // success. This scan drops versionless items — which is every reference under central package
            // management, i.e. most projects this tool is pointed at — so "read the file fine, found
            // nothing" is its ordinary answer for exactly the repositories that matter here.
            //
            // Letting that through is what made the 3.26.0 regression invisible: the resolved scan failed,
            // this stood in for it, reported success with no packages, and the project disappeared from the
            // report with scanFailures 0. Cross-review reproduced it still happening after the frameworks
            // check was added, because the check was upstream of this fallback rather than downstream.
            if (references.Count == 0)
            {
                success = false;
            }

            // Not reused as the declared list, even though it also comes from the project file: this scan
            // exists to stand in for a resolved one, so it drops versionless items — every reference under
            // central package management — but retains conditionality for version-bearing items. Handing
            // it over without that distinction missed duplicates for most users and let the version fixer
            // unify everything to a framework-conditional pin.
            var (fallbackDeclared, fallbackRead) = _projectAnalyzer.ScanDeclaredPackages(
                projectPath
            );
            return fallbackRead
                ? (references, success, fallbackDeclared)
                : ([], false, null);
        }

        // Read the project file as well. The resolved list cannot answer questions about what the file
        // says — resolution collapses duplicate PackageReference items — and this is a local XML parse,
        // not another process, so it costs a fraction of the scan it accompanies. ScanDeclaredPackages
        // rather than ScanProjectPackages: the latter drops versionless items, which is how central
        // package management writes every reference, so it would return nothing for most users of this
        // tool.
        var (declared, declaredRead) = _projectAnalyzer.ScanDeclaredPackages(projectPath);

        return declaredRead
            ? (references, success, declared)
            : ([], false, null);
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
                $"{deepScanFailures} package quer(ies) failed (--audit/--outdated/--deprecated/--licenses); "
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
                var (rescanned, postFixScanFailures, postFixDeepScanFailures) =
                    await ReanalyzeAfterFixesAsync(options, projectPaths: null);

                // The rescan produces a fresh, unsuppressed report. Without reapplying the
                // baseline, findings the team accepted would start failing the build the moment an
                // unrelated fix ran.
                var (postFixReport, postFixBaselineError) = ApplyBaseline(options, rescanned);
                if (postFixBaselineError is not null)
                {
                    _consoleService.Error(postFixBaselineError);
                }

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
    /// Records the current findings as the accepted baseline and stops. Writing and gating are
    /// separate modes: suppressing the findings being recorded would be circular.
    /// </summary>
    private async Task<MigrationResult> WriteBaselineAsync(
        Options options,
        AnalysisReport report,
        ProjectPackageInfo packageInfo,
        string basePath,
        int scanFailures,
        int deepScanFailures,
        int projectsDiscovered
    )
    {
        var path = options.ResolveBaselinePath();

        if (scanFailures > 0 || deepScanFailures > 0)
        {
            // A baseline claims to be the current accepted state. Recording one from a partial scan
            // would permanently accept findings that were simply never looked for — a transient
            // audit failure would silently bless every vulnerability it missed.
            _consoleService.Error(
                "Refusing to record a baseline from an incomplete scan: it would accept findings "
                    + "that were never checked. Fix the scan failures above and re-run."
            );

            return new MigrationResult
            {
                ProjectsProcessed = packageInfo.ProjectCount,
                PackagesCentralized = packageInfo.TotalReferences,
                AnalysisReport = report,
                PackageInfo = packageInfo,
                BasePath = basePath,
                ScanFailures = scanFailures,
                DeepScanFailures = deepScanFailures,
                ProjectsDiscovered = projectsDiscovered,
                ExitCode = ExitCodes.IncompleteAnalysis,
            };
        }

        try
        {
            await _baselineService.WriteAsync(_baselineService.Create(report), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _consoleService.Error($"Failed to write baseline file {path}: {ex.Message}");
            return new MigrationResult
            {
                AnalysisReport = report,
                PackageInfo = packageInfo,
                BasePath = basePath,
                ExitCode = ExitCodes.FileOperationError,
            };
        }

        if (!_quietMode)
        {
            _consoleService.Success(
                $"Recorded {report.TotalIssues} finding(s) as the accepted baseline in {path}."
            );
            _consoleService.Dim(
                "Commit it: subsequent runs report these findings without failing the build, so "
                    + "only new problems break CI."
            );
        }

        return new MigrationResult
        {
            ProjectsProcessed = packageInfo.ProjectCount,
            PackagesCentralized = packageInfo.TotalReferences,
            AnalysisReport = report,
            PackageInfo = packageInfo,
            BasePath = basePath,
            ProjectsDiscovered = projectsDiscovered,
            BaselinePath = path,
            BaselineWritten = true,
            GatedIssueCount = 0,
            ExitCode = ExitCodes.Success,
        };
    }

    /// <summary>
    /// Marks findings the baseline has accepted, and reports entries that no longer match anything.
    /// </summary>
    private (AnalysisReport Report, string? Error) ApplyBaseline(
        Options options,
        AnalysisReport report
    )
    {
        if (!options.UsesBaseline())
        {
            return (report, null);
        }

        var path = options.ResolveBaselinePath();
        var (baseline, error) = _baselineService.Read(path);
        if (baseline is null)
        {
            return (report, error);
        }

        var match = _baselineService.Apply(report, baseline);

        if (!_quietMode && match.Suppressed > 0)
        {
            _consoleService.Dim(
                $"Baseline {path}: {match.Suppressed} finding(s) accepted and excluded from the gate."
            );
        }

        if (!_quietMode && match.Stale.Count > 0)
        {
            // Dead entries accumulate silently and can start suppressing a finding that came back
            // under the same identity, so they are worth naming.
            _consoleService.Warning(
                $"Baseline {path}: {match.Stale.Count} entr(ies) no longer match any finding "
                    + "(fixed since). Re-run with --write-baseline to prune them."
            );
        }

        return (match.Report, null);
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
    /// Says which rules were switched off or re-graded. A report shaped by a policy the reader
    /// cannot see is the same trap as a clean-looking run that skipped half its projects: the
    /// output is honest only if the policy that produced it is on screen next to it.
    /// </summary>
    private void ReportRulePolicy(RulePolicy policy)
    {
        if (_quietMode || policy.IsEmpty)
        {
            return;
        }

        _consoleService.Dim($"Rule policy applied: {policy.Describe()}");
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
        var (basePath, discovered) = await _discoverProjects(options);
        var paths = projectPaths ?? discovered;

        // The cache holds pre-fix references; the point of this pass is to read the new files.
        _cachedProjectScans = null;

        var (packageInfo, scanFailures, deepScanFailures) = await PerformAnalysisScanAsync(
            options,
            paths,
            basePath
        );

        // The rescan runs the analyzers afresh, so the policy has to be reapplied here too —
        // otherwise a rule the team switched off comes back the moment --fix runs, and fails a
        // build on findings that were excluded from the report that prompted the fixes.
        var rescanned = options.ResolveRulePolicy().Apply(_analysisService.Analyze(packageInfo));

        return (rescanned, scanFailures, deepScanFailures);
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
