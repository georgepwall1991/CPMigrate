using CPMigrate.Models;
using CPMigrate.Services.Update;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CPMigrate.Services.Remediation;

/// <summary>
/// Orchestrates <c>--remediate</c>: scan, plan, apply, verify, re-scan.
///
/// Every step that touches the build already exists. The props file is rewritten through
/// <see cref="PropsUpdateTransaction"/>, verified through <see cref="DotNetVerificationRunner"/>, and
/// searched through the same <see cref="IUpdateSearchStrategy"/> implementations
/// <c>--update-packages</c> uses, so a remediation that goes red rolls back exactly the way an update
/// does, and <c>--bisect</c> behaves identically. What is new here is only the question asked of NuGet:
/// the lowest version that clears an advisory rather than the newest that exists.
///
/// The final step has no counterpart in the update path. After a green verification the vulnerability
/// scan is run again, and the receipt reports what that second scan found rather than what the plan
/// intended. A remediation that reports success on the strength of its own plan is asserting the thing
/// it was supposed to prove.
/// </summary>
public sealed class RemediationService : IRemediationService, IDisposable
{
    private readonly IConsoleService _consoleService;
    private readonly IProjectAnalyzer _projectAnalyzer;
    private readonly IDotNetPackageQueryService _packageQuery;
    private readonly PropsGenerator _propsGenerator;
    private readonly INuGetVersionLookupService _nuGetLookup;
    private readonly IAdvisoryOracle _advisoryOracle;
    private readonly IDotNetCliService _dotNetCli;
    private readonly IBackupManager _backupManager;
    private readonly ILogger<RemediationService> _logger;

    private const string StaleAssetsGuidance =
        "obj/project.assets.json still describes the rejected fix until the next "
        + "dotnet restore — restore before building or trusting external tools.";

    /// <summary>
    /// Creates a remediation service.
    /// </summary>
    /// <param name="consoleService">Console sink.</param>
    /// <param name="projectAnalyzer">Project discovery.</param>
    /// <param name="packageQuery">Vulnerability scanning.</param>
    /// <param name="propsGenerator">Props file reader and writer.</param>
    /// <param name="nuGetLookup">Published version lookup.</param>
    /// <param name="advisoryOracle">Advisory version ranges.</param>
    /// <param name="dotNetCli">Restore and test execution.</param>
    /// <param name="backupManager">Backup creation and cleanup.</param>
    /// <param name="logger">Optional logger.</param>
    public RemediationService(
        IConsoleService consoleService,
        IProjectAnalyzer projectAnalyzer,
        IDotNetPackageQueryService packageQuery,
        PropsGenerator propsGenerator,
        INuGetVersionLookupService nuGetLookup,
        IAdvisoryOracle advisoryOracle,
        IDotNetCliService dotNetCli,
        IBackupManager backupManager,
        ILogger<RemediationService>? logger = null
    )
    {
        _consoleService = consoleService;
        _projectAnalyzer = projectAnalyzer;
        _packageQuery = packageQuery;
        _propsGenerator = propsGenerator;
        _nuGetLookup = nuGetLookup;
        _advisoryOracle = advisoryOracle;
        _dotNetCli = dotNetCli;
        _backupManager = backupManager;
        _logger = logger ?? NullLogger<RemediationService>.Instance;
    }

    /// <inheritdoc />
    public async Task<RemediationResult> RemediateAsync(RemediateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var solutionDir = Path.GetFullPath(request.SolutionPath);
        var (basePath, projectPaths) = await _projectAnalyzer.DiscoverProjectsFromSolutionAsync(solutionDir);

        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to remediate.");
            return Failed(ExitCodes.NoProjectsFound, "no projects found");
        }

        var propsPath = FindPropsFile(basePath);
        if (propsPath == null)
        {
            _consoleService.Error(
                "Directory.Packages.props not found. Remediation pins versions centrally, so CPM has to "
                + "be enabled first — run 'cpmigrate' to migrate."
            );
            return Failed(ExitCodes.ValidationError, "Central Package Management is not enabled");
        }

        var currentVersions = PropsGenerator.ReadExistingPackageVersions(propsPath, out _);

        _consoleService.Info($"Scanning {projectPaths.Count} project(s) for known advisories...");
        var (vulnerabilities, scanComplete) = await ScanAsync(projectPaths);

        if (!scanComplete)
        {
            // The same rule the analyze gate lives by: a scan that did not finish reports nothing for
            // the part it could not read, and remediating on that basis would leave a package exposed
            // while claiming it had been handled.
            _consoleService.Error(
                "The vulnerability scan did not complete, so the advisory list is incomplete. "
                + "Nothing has been changed."
            );
            return Failed(ExitCodes.IncompleteAnalysis, "vulnerability scan did not complete");
        }

        var advisoriesBefore = CountAdvisories(vulnerabilities);

        if (vulnerabilities.Count == 0)
        {
            _consoleService.Success("No known advisories. Nothing to remediate.");
            return new RemediationResult
            {
                ExitCode = ExitCodes.Success,
                AdvisoriesBefore = 0,
                AdvisoriesAfter = 0,
                ReVerified = true,
                DryRun = request.DryRun,
            };
        }

        _consoleService.Info(
            $"Found {advisoriesBefore} advisory finding(s); computing the smallest version that clears each..."
        );

        var planner = new RemediationPlanner(_advisoryOracle, _nuGetLookup);
        var plan = await planner.PlanAsync(
            vulnerabilities,
            request.AllowMajor,
            request.IncludePrerelease,
            request.OnlyPackages
        );

        ReportPlan(plan);

        if (plan.HasUnavailableAdvisoryData)
        {
            // Fail loud rather than remediating what could be read. A partial fix that reports itself
            // as a fix is worse than no fix: the finding it missed now sits behind a green run.
            _consoleService.Error(
                "Advisory data could not be read for every finding, so no fix can be proven complete. "
                + "Nothing has been changed."
            );
            return new RemediationResult
            {
                ExitCode = ExitCodes.IncompleteAnalysis,
                Actions = plan.Actions,
                AdvisoriesBefore = advisoriesBefore,
                DryRun = request.DryRun,
                Errors = ["advisory data unavailable for one or more findings"],
            };
        }

        var applicable = plan.GetApplicable();

        if (applicable.Count == 0)
        {
            _consoleService.Warning("No advisory could be cleared by a version bump.");
            return new RemediationResult
            {
                ExitCode = ExitCodes.RemediationIncomplete,
                Actions = plan.Actions,
                AdvisoriesBefore = advisoriesBefore,
                DryRun = request.DryRun,
            };
        }

        if (request.DryRun)
        {
            _consoleService.DryRun(
                $"Would move {applicable.Count} package(s) to clear {advisoriesBefore} advisory finding(s)."
            );
            return new RemediationResult
            {
                ExitCode = plan.GetNotApplied().Count > 0
                    ? ExitCodes.RemediationIncomplete
                    : ExitCodes.Success,
                Actions = plan.Actions,
                AdvisoriesBefore = advisoriesBefore,
                DryRun = true,
            };
        }

        return await ApplyAsync(request, plan, applicable, propsPath, basePath, currentVersions, projectPaths, advisoriesBefore);
    }

    private async Task<RemediationResult> ApplyAsync(
        RemediateRequest request,
        RemediationPlan plan,
        IReadOnlyList<RemediationAction> applicable,
        string propsPath,
        string basePath,
        Dictionary<string, HashSet<string>> currentVersions,
        List<string> projectPaths,
        int advisoriesBefore
    )
    {
        var backup = await CreateBackupAsync(request, propsPath);
        if (backup == null)
        {
            return Failed(ExitCodes.FileOperationError, "could not create a backup");
        }

        var entries = applicable.Select(ToUpdateEntry).ToList();

        _consoleService.Info($"Applying {entries.Count} security fix(es)...");

        var verificationTarget = FindSolutionFile(basePath) ?? basePath;
        var transaction = await PropsUpdateTransaction.BeginAsync(propsPath, currentVersions, _propsGenerator);
        var runner = new DotNetVerificationRunner(
            _dotNetCli,
            _consoleService,
            verificationTarget,
            request.BisectTestFilter
        );
        var strategy = CreateSearchStrategy(request);

        UpdateSearchResult search;
        try
        {
            search = await strategy.SearchAsync(entries, transaction, runner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _consoleService.Error($"Remediation failed while writing Directory.Packages.props: {ex.Message}");
            await TryRevertAsync(transaction);
            BackupManager.CleanupBackups(backup.Value.Path, backup.Value.Manifest);
            return Failed(ExitCodes.FileOperationError, ex.Message);
        }

        BackupManager.CleanupBackups(backup.Value.Path, backup.Value.Manifest);

        if (!search.AnyApplied)
        {
            _consoleService.Error(
                search.BaselineBroken
                    ? "Verification failed before any fix was applied — the tree was already red."
                    : "Every security fix broke verification; the props file has been rolled back."
            );

            return new RemediationResult
            {
                ExitCode = ExitCodes.TestFailure,
                Actions = plan.Actions,
                HeldBack = search.HeldBack.Select(h => h.PackageName).ToList(),
                AdvisoriesBefore = advisoriesBefore,
                VerificationRuns = search.VerificationRuns,
                BisectBudgetExhausted = search.BudgetExhausted,
                WasRolledBack = true,
                Warnings = [StaleAssetsGuidance],
            };
        }

        var remediated = search.Applied.Select(a => a.PackageName).ToList();
        var heldBack = search.HeldBack.Select(h => h.PackageName).ToList();

        if (heldBack.Count > 0)
        {
            _consoleService.Warning(
                $"Held back {heldBack.Count} fix(es) that broke verification: {string.Join(", ", heldBack)}"
            );
        }

        // The proof. Everything above is what CPMigrate decided; this is what the SDK says afterwards,
        // and it is what the receipt reports.
        //
        // The restore is load-bearing, not hygiene. A bisected search ends by re-applying the good
        // subset *after* its final verification, so obj/project.assets.json still describes the
        // probe that was rejected. dotnet list package reads that file, so without this the
        // confirming scan would measure a state that no longer exists on disk -- and advisoriesAfter
        // is the number that decides exit 0.
        _consoleService.Info("Restoring before the confirming scan...");
        var (restoreOutput, restoreSucceeded) = await _dotNetCli.RunRestoreAsync(verificationTarget);

        if (!restoreSucceeded)
        {
            _logger.LogWarning("Restore before the confirming scan failed: {Output}", restoreOutput);
            return BuildFinalResult(plan, search, remediated, heldBack, advisoriesBefore, 0, afterComplete: false);
        }

        _consoleService.Info("Re-scanning to confirm the advisories are gone...");
        _packageQuery.ClearCache();
        var (after, afterComplete) = await ScanAsync(projectPaths);
        var advisoriesAfter = CountAdvisories(after);

        return BuildFinalResult(
            plan, search, remediated, heldBack, advisoriesBefore, advisoriesAfter, afterComplete
        );
    }

    private RemediationResult BuildFinalResult(
        RemediationPlan plan,
        UpdateSearchResult search,
        IReadOnlyList<string> remediated,
        IReadOnlyList<string> heldBack,
        int advisoriesBefore,
        int advisoriesAfter,
        bool afterComplete
    )
    {
        if (!afterComplete)
        {
            _consoleService.Warning(
                "Fixes were applied, but the confirming scan did not complete — the result is unproven."
            );

            return new RemediationResult
            {
                ExitCode = ExitCodes.IncompleteAnalysis,
                Actions = plan.Actions,
                Remediated = remediated,
                HeldBack = heldBack,
                AdvisoriesBefore = advisoriesBefore,
                VerificationRuns = search.VerificationRuns,
                BisectBudgetExhausted = search.BudgetExhausted,
                ReVerified = false,
            };
        }

        var cleared = advisoriesBefore - advisoriesAfter;

        if (advisoriesAfter == 0)
        {
            _consoleService.Success(
                $"Cleared {cleared} advisory finding(s) across {remediated.Count} package(s); "
                + "a fresh scan reports none remaining."
            );
        }
        else
        {
            _consoleService.Warning(
                $"Cleared {cleared} advisory finding(s); {advisoriesAfter} still reported."
            );
        }

        var complete = advisoriesAfter == 0 && plan.GetNotApplied().Count == 0 && heldBack.Count == 0;

        return new RemediationResult
        {
            ExitCode = complete ? ExitCodes.Success : ExitCodes.RemediationIncomplete,
            Actions = plan.Actions,
            Remediated = remediated,
            HeldBack = heldBack,
            AdvisoriesBefore = advisoriesBefore,
            AdvisoriesAfter = advisoriesAfter,
            VerificationRuns = search.VerificationRuns,
            BisectBudgetExhausted = search.BudgetExhausted,
            ReVerified = true,
            Warnings = heldBack.Count > 0 ? [StaleAssetsGuidance] : null,
        };
    }

    /// <summary>
    /// Converts a planned action into the entry shape the update pipeline applies.
    ///
    /// <see cref="PackageUpdateEntry.LatestVersion"/> carries the remediation target rather than the
    /// newest published version — the field names the version to write, and writing the newest is
    /// precisely what remediation exists not to do.
    /// </summary>
    private static PackageUpdateEntry ToUpdateEntry(RemediationAction action)
    {
        return new PackageUpdateEntry(
            action.PackageName,
            action.CurrentVersion,
            action.TargetVersion!,
            action.IsMajorBump,
            Accepted: true,
            action.IsTransitive
        );
    }

    private async Task<(List<VulnerabilityInfo> Vulnerabilities, bool Complete)> ScanAsync(
        IReadOnlyList<string> projectPaths
    )
    {
        var all = new List<VulnerabilityInfo>();
        var complete = true;

        foreach (var projectPath in projectPaths)
        {
            var (vulnerabilities, success) = await _packageQuery.ScanVulnerabilitiesAsync(projectPath);

            if (!success)
            {
                _logger.LogWarning("Vulnerability scan failed for {ProjectPath}", projectPath);
                complete = false;
                continue;
            }

            all.AddRange(vulnerabilities);
        }

        return (all, complete);
    }

    /// <summary>
    /// Counts distinct advisory findings: one per package-and-advisory pair, not one per project or
    /// target framework. A multi-target project reports the same advisory several times, and counting
    /// those separately would make the before/after numbers move for reasons that are not fixes.
    /// </summary>
    private static int CountAdvisories(IReadOnlyList<VulnerabilityInfo> vulnerabilities)
    {
        return vulnerabilities
            .Select(v => $"{v.PackageName}|{v.Id}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private IUpdateSearchStrategy CreateSearchStrategy(RemediateRequest request)
    {
        if (!request.Bisect)
        {
            return new AllOrNothingSearchStrategy();
        }

        _consoleService.Info(
            $"Bisect enabled: holding back only the fixes that break verification (budget: {request.BisectBudget} run(s))."
        );
        return new BisectSearchStrategy(_consoleService, request.BisectBudget);
    }

    private void ReportPlan(RemediationPlan plan)
    {
        foreach (var action in plan.Actions)
        {
            var identifiers = action.Cves.Count > 0
                ? string.Join(", ", action.Cves)
                : string.Join(", ", action.AdvisoryIds);

            switch (action.Outcome)
            {
                case RemediationOutcome.Planned:
                    _consoleService.Info(
                        $"  {action.PackageName} {action.CurrentVersion} → {action.TargetVersion}"
                            + $"{(action.IsTransitive ? " (new central pin)" : string.Empty)}"
                            + $"{(action.IsMajorBump ? " [major]" : string.Empty)}  {identifiers}"
                    );
                    break;

                case RemediationOutcome.WithheldMajor:
                    _consoleService.Warning(
                        $"  {action.PackageName} {action.CurrentVersion} → {action.TargetVersion} withheld: {action.Reason}"
                    );
                    break;

                default:
                    _consoleService.Warning($"  {action.PackageName} {action.CurrentVersion}: {action.Reason}");
                    break;
            }
        }
    }

    private async Task<(string Path, BackupManifest Manifest)?> CreateBackupAsync(
        RemediateRequest request,
        string propsPath
    )
    {
        string backupPath;
        try
        {
            backupPath = BackupManager.CreateBackupDirectory(request.Backup);
        }
        catch (IOException ex)
        {
            _consoleService.Error($"Failed to create backup directory: {ex.Message}");
            return null;
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupEntry = _backupManager.CreateBackupForProject(request.Backup, propsPath, backupPath, timestamp);
        var manifest = new BackupManifest
        {
            Timestamp = timestamp,
            PropsFilePath = propsPath,
            PropsFileExisted = true,
            Backups = backupEntry != null ? [backupEntry] : [],
        };
        await BackupManager.WriteManifestAsync(backupPath, manifest);

        return (backupPath, manifest);
    }

    private async Task TryRevertAsync(IUpdateTransaction transaction)
    {
        try
        {
            await transaction.RevertAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _consoleService.Error(
                $"Could not restore Directory.Packages.props: {ex.Message}. Use 'cpmigrate --rollback'."
            );
        }
    }

    private static RemediationResult Failed(int exitCode, string error)
    {
        return new RemediationResult { ExitCode = exitCode, Errors = [error] };
    }

    /// <summary>
    /// Disposes the network clients this service owns, matching how
    /// <see cref="PackageUpdateService"/> owns its version lookup.
    /// </summary>
    public void Dispose()
    {
        _nuGetLookup.Dispose();
        _advisoryOracle.Dispose();
    }

    private static string? FindPropsFile(string basePath)
    {
        var candidate = Path.Combine(basePath, "Directory.Packages.props");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindSolutionFile(string basePath)
    {
        return Directory
            .EnumerateFiles(basePath, "*.sln*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f =>
                f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            );
    }
}
