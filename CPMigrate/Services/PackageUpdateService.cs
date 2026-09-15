using CPMigrate.Models;
using CPMigrate.Services.Update;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace CPMigrate.Services;

/// <summary>
/// Orchestrates updating NuGet packages to latest versions with test verification and rollback.
/// </summary>
public sealed class PackageUpdateService : IPackageUpdateService, IDisposable
{
    private readonly IConsoleService _consoleService;
    private readonly IProjectAnalyzer _projectAnalyzer;
    private readonly PropsGenerator _propsGenerator;
    private readonly INuGetVersionLookupService _nuGetLookup;
    private readonly IDotNetCliService _dotNetCli;
    private readonly IBackupManager _backupManager;
    private readonly ILogger<PackageUpdateService> _logger;
    private readonly DiffFileCollector _diffCollector = new();

    public PackageUpdateService(
        IConsoleService consoleService,
        IProjectAnalyzer projectAnalyzer,
        PropsGenerator propsGenerator,
        INuGetVersionLookupService nuGetLookup,
        IDotNetCliService dotNetCli,
        IBackupManager backupManager,
        ILogger<PackageUpdateService>? logger = null)
    {
        _consoleService = consoleService;
        _projectAnalyzer = projectAnalyzer;
        _propsGenerator = propsGenerator;
        _nuGetLookup = nuGetLookup;
        _dotNetCli = dotNetCli;
        _backupManager = backupManager;
        _logger = logger ?? NullLogger<PackageUpdateService>.Instance;
    }

    public Task<PackageUpdateResult> UpdatePackagesAsync(Options options)
    {
        return UpdatePackagesAsync(PackageUpdateRequest.FromOptions(options));
    }

    /// <inheritdoc />
    public async Task<PackageUpdateResult> UpdatePackagesAsync(PackageUpdateRequest request)
    {
        // The artifact exists from the first moment of the run: empty means "no changes", absent
        // means the run crashed before it could say so — the contract --diff-file keeps everywhere.
        if (request.DryRun && !string.IsNullOrEmpty(request.DiffFilePath))
        {
            _diffCollector.Begin(request.DiffFilePath);
        }

        var load = await DiscoverAndLoadCurrentVersionsAsync(request);
        if (load.EarlyResult != null)
        {
            return load.EarlyResult;
        }

        _consoleService.Info($"Checking {load.CurrentVersions.Count} packages for updates...");
        var (updates, transitiveFound) = await QueryAllUpdatesAsync(load.CurrentVersions, load.ProjectPaths, request);

        ReportFailedLookups();
        ReportNotFoundLookups();

        var availableUpdates = ApplyOnlyFilter(FilterAvailableUpdates(updates), request);
        if (availableUpdates.Count == 0)
        {
            // Deliberately not "Everything up to date!" when a lookup failed or a package is not
            // on the feed at all: that claim would be false, and it is the claim a user acts on.
            if (
                _nuGetLookup.GetFailedLookups().Count > 0
                || _nuGetLookup.GetNotFoundLookups().Count > 0
            )
            {
                _consoleService.Warning(
                    "No updates found, but some packages could not be checked — see above."
                );
            }
            else
            {
                _consoleService.Success("Everything up to date!");
            }

            return BuildNoUpdatesResult(load.CurrentVersions.Count, transitiveFound);
        }

        ShowUpdatesTable(availableUpdates);

        var acceptedUpdates = RunMajorVersionWizard(availableUpdates, request);
        var updatesToApply = acceptedUpdates.Where(u => u.Accepted).ToList();
        var transitiveWithheld = WithholdInertTransitivePins(load, updatesToApply, acceptedUpdates);
        if (updatesToApply.Count == 0)
        {
            _consoleService.Info("No updates selected.");
            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.Success,
                PackagesChecked = load.CurrentVersions.Count,
                PackagesSkipped = load.CurrentVersions.Count,
                TransitivePackagesFound = transitiveFound,
                TransitivePackagesWithheld = transitiveWithheld,
                Updates = acceptedUpdates
            };
        }

        if (request.DryRun)
        {
            return BuildDryRunResult(
                load.CurrentVersions.Count, transitiveFound, updatesToApply, acceptedUpdates, load, request, transitiveWithheld);
        }

        var backup = await CreateBackupAsync(request, load.PropsPath);
        if (backup.EarlyResult != null)
        {
            return backup.EarlyResult;
        }

        AnnounceApply(updatesToApply);

        var transaction = await PropsUpdateTransaction.BeginAsync(
            load.PropsPath, load.CurrentVersions, _propsGenerator);
        var runner = new DotNetVerificationRunner(
            _dotNetCli,
            _consoleService,
            FindSolutionFile(load.BasePath) ?? load.BasePath,
            request.BisectTestFilter);
        var strategy = CreateSearchStrategy(request);

        UpdateSearchResult search;
        try
        {
            search = await strategy.SearchAsync(updatesToApply, transaction, runner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The props file may be half-written; get the user back to a known state before surfacing this.
            _consoleService.Error($"Update failed while writing Directory.Packages.props: {ex.Message}");
            return await RecoverFromWriteFailureAsync(
                transaction, backup.Path, backup.Manifest, load.CurrentVersions.Count,
                acceptedUpdates, transitiveFound, transitiveWithheld);
        }

        return FinalizeSearch(
            search, backup.Path, backup.Manifest, load.CurrentVersions.Count,
            acceptedUpdates, transitiveFound, request, transitiveWithheld);
    }

    /// <summary>
    /// Transitive-only updates only move the graph when the workspace opts into central transitive
    /// pinning; without it a <c>PackageVersion</c> for a package nothing references directly is an
    /// inert line restore ignores. Writing it would claim coverage that does not exist — the same
    /// reason remediation reports rather than writes — so these are withheld, named, and excluded
    /// from the set any verification or dry-run preview sees.
    /// </summary>
    /// <returns>How many accepted updates were withheld.</returns>
    private int WithholdInertTransitivePins(
        UpdateLoadContext load,
        List<PackageUpdateEntry> updatesToApply,
        List<PackageUpdateEntry> acceptedUpdates)
    {
        var transitive = updatesToApply.Where(u => u.IsTransitive).ToList();
        if (transitive.Count == 0 || TransitivePinning.IsEnabled(load.PropsPath, load.BasePath))
        {
            return 0;
        }

        for (var i = 0; i < acceptedUpdates.Count; i++)
        {
            if (acceptedUpdates[i] is { Accepted: true, IsTransitive: true })
            {
                acceptedUpdates[i] = acceptedUpdates[i] with { Withheld = true };
            }
        }

        updatesToApply.RemoveAll(u => u.IsTransitive);

        _consoleService.Warning(
            $"{transitive.Count} transitive update(s) withheld: a central pin for a package nothing "
                + "references directly does not move the resolved graph unless "
                + "CentralPackageTransitivePinningEnabled is true. Set it in "
                + "Directory.Packages.props or Directory.Build.props to let these apply: "
                + string.Join(", ", transitive.Select(u => u.PackageName).Order(StringComparer.OrdinalIgnoreCase))
        );

        return transitive.Count;
    }

    private IUpdateSearchStrategy CreateSearchStrategy(PackageUpdateRequest request)
    {
        if (!request.Bisect)
        {
            return new AllOrNothingSearchStrategy();
        }

        _consoleService.Info(
            $"Bisect enabled: holding back only the updates that break verification (budget: {request.BisectBudget} run(s)).");
        return new BisectSearchStrategy(_consoleService, request.BisectBudget);
    }

    private async Task<UpdateLoadContext> DiscoverAndLoadCurrentVersionsAsync(PackageUpdateRequest request)
    {
        var solutionDir = Path.GetFullPath(request.SolutionPath);
        var (basePath, projectPaths) = await _projectAnalyzer.DiscoverProjectsFromSolutionAsync(solutionDir);

        // The empty answer comes first: an ancestor props file can govern a directory that holds
        // no projects at all, and updating that file would touch versions nothing here declared.
        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to update.");
            return UpdateLoadContext.FromEarly(new PackageUpdateResult { ExitCode = ExitCodes.NoProjectsFound });
        }

        var propsPath = FindPropsFile(basePath);
        if (propsPath == null)
        {
            _consoleService.Error("Directory.Packages.props not found. CPM is not enabled. Run 'cpmigrate' first.");
            return UpdateLoadContext.FromEarly(new PackageUpdateResult { ExitCode = ExitCodes.ValidationError });
        }

        var currentVersions = PropsGenerator.ReadExistingPackageVersions(propsPath, out _);
        if (currentVersions.Count == 0)
        {
            _consoleService.Info("No packages found in Directory.Packages.props.");
            return UpdateLoadContext.FromEarly(new PackageUpdateResult { ExitCode = ExitCodes.Success });
        }

        return new UpdateLoadContext(basePath, projectPaths, propsPath, currentVersions);
    }

    private async Task<(List<PackageUpdateEntry> Updates, int TransitiveFound)> QueryAllUpdatesAsync(
        Dictionary<string, HashSet<string>> currentVersions,
        List<string> projectPaths,
        PackageUpdateRequest request)
    {
        var updates = await QueryNuGetForUpdatesAsync(currentVersions, request.IncludePrerelease);

        var transitiveFound = 0;
        if (request.IncludeTransitive)
        {
            var (transitive, found) = await ScanAndQueryTransitiveUpdatesAsync(
                projectPaths, currentVersions, request.IncludePrerelease);
            transitiveFound = found;
            updates.AddRange(transitive);
        }

        return (updates, transitiveFound);
    }

    /// <summary>
    /// Narrows the candidate set to the packages named by <c>--only</c>. Warns about names that matched
    /// nothing so a typo does not look like "already up to date".
    /// </summary>
    private List<PackageUpdateEntry> ApplyOnlyFilter(List<PackageUpdateEntry> updates, PackageUpdateRequest request)
    {
        if (request.OnlyPackages is not { Count: > 0 } only)
        {
            return updates;
        }

        var wanted = new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
        var filtered = updates.Where(u => wanted.Contains(u.PackageName)).ToList();

        var unmatched = only
            .Where(name => !updates.Any(u => string.Equals(u.PackageName, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (unmatched.Count > 0)
        {
            _consoleService.Warning(
                $"--only matched no available update for: {string.Join(", ", unmatched)}");
        }

        return filtered;
    }

    private static List<PackageUpdateEntry> FilterAvailableUpdates(List<PackageUpdateEntry> updates)
    {
        return updates
            .Where(u =>
            {
                var current = NuGetVersion.TryParse(u.CurrentVersion, out var c) ? c : null;
                var latest = NuGetVersion.TryParse(u.LatestVersion, out var l) ? l : null;
                return current != null && latest != null && latest > current;
            })
            .ToList();
    }

    private static PackageUpdateResult BuildNoUpdatesResult(int packagesChecked, int transitiveFound)
    {
        return new PackageUpdateResult
        {
            ExitCode = ExitCodes.Success,
            PackagesChecked = packagesChecked,
            PackagesSkipped = packagesChecked,
            TransitivePackagesFound = transitiveFound
        };
    }

    private PackageUpdateResult BuildDryRunResult(
        int packagesChecked, int transitiveFound,
        List<PackageUpdateEntry> updatesToApply, List<PackageUpdateEntry> acceptedUpdates,
        UpdateLoadContext load, PackageUpdateRequest request, int transitiveWithheld)
    {
        var directDryRun = updatesToApply.Where(u => !u.IsTransitive).ToList();
        var transitiveDryRun = updatesToApply.Where(u => u.IsTransitive).ToList();
        _consoleService.DryRun($"Would update {directDryRun.Count} direct package(s)" +
            (transitiveDryRun.Count > 0 ? $" and pin {transitiveDryRun.Count} transitive package(s)." : "."));
        ShowDryRunSummary(updatesToApply);
        ShowPlannedPropsDiff(load, updatesToApply, request);
        return new PackageUpdateResult
        {
            ExitCode = ExitCodes.Success,
            PackagesChecked = packagesChecked,
            PackagesUpdated = directDryRun.Count,
            PackagesSkipped = packagesChecked - directDryRun.Count,
            TransitivePackagesFound = transitiveFound,
            TransitivePackagesUpdated = transitiveDryRun.Count,
            TransitivePackagesWithheld = transitiveWithheld,
            Updates = acceptedUpdates
        };
    }

    private async Task<UpdateBackupContext> CreateBackupAsync(PackageUpdateRequest request, string propsPath)
    {
        // Disabled backups produce no directory and no manifest — writing one anyway would land
        // a stray backup_manifest.json in the working directory, since an empty backup path
        // resolves there.
        if (!request.Backup.Enabled)
        {
            return new UpdateBackupContext(
                string.Empty,
                new BackupManifest { PropsFilePath = propsPath }
            );
        }

        string backupPath;
        try
        {
            backupPath = BackupManager.CreateBackupDirectory(request.Backup);
        }
        catch (IOException ex)
        {
            _consoleService.Error($"Failed to create backup directory: {ex.Message}");
            return UpdateBackupContext.FromEarly(new PackageUpdateResult { ExitCode = ExitCodes.FileOperationError });
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupEntry = _backupManager.CreateBackupForProject(request.Backup, propsPath, backupPath, timestamp);
        var manifest = new BackupManifest
        {
            Timestamp = timestamp,
            PropsFilePath = propsPath,
            PropsFileExisted = true,
            Backups = backupEntry != null ? [backupEntry] : []
        };
        await BackupManager.WriteManifestAsync(backupPath, manifest);

        return new UpdateBackupContext(backupPath, manifest);
    }

    private const string StaleAssetsGuidance =
        "obj/project.assets.json still describes the rejected update until the next "
        + "dotnet restore — restore before building or trusting external tools.";

    private void AnnounceApply(List<PackageUpdateEntry> updatesToApply)
    {
        var directCount = updatesToApply.Count(u => !u.IsTransitive);
        var transitiveCount = updatesToApply.Count(u => u.IsTransitive);
        var applyMsg = $"Applying {directCount} direct update(s)";
        if (transitiveCount > 0)
        {
            applyMsg += $" and pinning {transitiveCount} transitive package(s)";
        }
        _consoleService.Info(applyMsg + "...");
    }

    /// <summary>
    /// Turns the search outcome into a user-facing report, a cleaned-up backup directory, and a result object.
    /// The props file is already in its final state by the time this runs — the strategy guarantees that.
    /// </summary>
    private PackageUpdateResult FinalizeSearch(
        UpdateSearchResult search,
        string backupPath,
        BackupManifest manifest,
        int packagesChecked,
        List<PackageUpdateEntry> acceptedUpdates,
        int transitiveFound,
        PackageUpdateRequest request,
        int transitiveWithheld = 0)
    {
        ReportSearchOutcome(search, request);
        BackupManager.CleanupBackups(backupPath, manifest);

        var directApplied = search.Applied.Count(u => !u.IsTransitive);
        var transitiveApplied = search.Applied.Count(u => u.IsTransitive);
        var heldByName = search.HeldBack.ToDictionary(h => h.PackageName, StringComparer.OrdinalIgnoreCase);

        // Reflect hold-backs onto the reported entries so JSON consumers can see exactly what was excluded.
        var reportedUpdates = acceptedUpdates
            .Select(u => heldByName.ContainsKey(u.PackageName) ? u with { HeldBack = true } : u)
            .ToList();

        // Mirror what the console was told: a rejected update leaves obj/ stale until the next
        // restore, and held-back bisect probes were reverted without one.
        IReadOnlyList<string>? warnings =
            (!request.Bisect && !search.AnyApplied)
            || (request.Bisect && search.HeldBack.Count > 0)
                ? [StaleAssetsGuidance]
                : null;

        return new PackageUpdateResult
        {
            ExitCode = search.AnyApplied ? ExitCodes.Success : ExitCodes.TestFailure,
            Warnings = warnings,
            PackagesChecked = packagesChecked,
            PackagesUpdated = directApplied,
            PackagesSkipped = packagesChecked - directApplied,
            TransitivePackagesFound = transitiveFound,
            TransitivePackagesUpdated = transitiveApplied,
            TransitivePackagesWithheld = transitiveWithheld,
            PackagesHeldBack = search.HeldBack.Count,
            VerificationRuns = search.VerificationRuns,
            BisectBudgetExhausted = search.BudgetExhausted,
            TestsPassed = search.AnyApplied,
            WasRolledBack = !search.AnyApplied,
            Updates = reportedUpdates
        };
    }

    private void ReportSearchOutcome(UpdateSearchResult search, PackageUpdateRequest request)
    {
        if (search.AllApplied)
        {
            var directApplied = search.Applied.Count(u => !u.IsTransitive);
            var transitiveApplied = search.Applied.Count(u => u.IsTransitive);
            var successMsg = $"All tests passed! Updated {directApplied} package(s)";
            if (transitiveApplied > 0)
            {
                successMsg += $" and pinned {transitiveApplied} transitive package(s)";
            }
            _consoleService.Success(successMsg + ".");
            return;
        }

        if (!request.Bisect)
        {
            // Say which step actually failed: when restore falls over, the tests never ran at all.
            var cause = search.FailureOutcome == VerificationOutcome.RestoreFailed
                ? "dotnet restore failed!"
                : "Tests failed!";
            _consoleService.Error($"{cause} Rolled back to previous versions.");
            _consoleService.Dim(StaleAssetsGuidance);
            _consoleService.Dim(search.FailureOutput ?? string.Empty);
            return;
        }

        WriteBisectReport(search);
    }

    private void WriteBisectReport(UpdateSearchResult search)
    {
        _consoleService.WriteLine();
        _consoleService.Banner("BISECT RESULT");
        _consoleService.WriteLine();

        foreach (var held in search.HeldBack.OrderBy(h => h.PackageName, StringComparer.OrdinalIgnoreCase))
        {
            _consoleService.Warning(
                $"  HELD    {held.PackageName}: {held.CurrentVersion} → {held.LatestVersion} (left at {held.CurrentVersion})");
        }

        foreach (var applied in search.Applied.OrderBy(a => a.PackageName, StringComparer.OrdinalIgnoreCase))
        {
            _consoleService.Dim($"  APPLIED {applied.PackageName}: {applied.CurrentVersion} → {applied.LatestVersion}");
        }

        _consoleService.WriteLine();

        // Every held-back package was probed and reverted without a following restore, so obj/
        // describes whichever probe ran last — not the kept subset.
        if (search.HeldBack.Count > 0)
        {
            _consoleService.Dim(StaleAssetsGuidance);
        }

        var total = search.Applied.Count + search.HeldBack.Count;
        if (search.AnyApplied)
        {
            _consoleService.Success(
                $"Kept {search.Applied.Count}/{total} update(s) with tests green " +
                $"({search.VerificationRuns} verification run(s)).");
        }
        else if (search.BaselineBroken)
        {
            _consoleService.Error(
                "No updates applied: verification fails even with zero updates, so no package could be cleared. " +
                "Fix the existing failures first.");
        }
        else
        {
            _consoleService.Error(
                $"No updates could be kept — all {total} broke verification " +
                $"({search.VerificationRuns} verification run(s)).");
        }

        if (search.BudgetExhausted)
        {
            _consoleService.Warning(
                $"Bisect budget of {search.VerificationRuns} run(s) was exhausted before the search finished. " +
                "Unresolved updates were held back. Re-run with a higher --bisect-budget to narrow further.");
        }

        if (search.HeldBack.Count > 0)
        {
            var names = string.Join(",", search.HeldBack.Select(h => h.PackageName).Order(StringComparer.OrdinalIgnoreCase));
            _consoleService.Dim($"  Investigate with: cpmigrate --update-packages --only {names}");
        }
    }

    /// <summary>
    /// Best-effort recovery when the props file could not be written mid-search. Prefers the in-memory
    /// baseline, then the on-disk backup, and finally tells the user where to recover from by hand.
    /// </summary>
    private async Task<PackageUpdateResult> RecoverFromWriteFailureAsync(
        IUpdateTransaction transaction,
        string backupPath,
        BackupManifest manifest,
        int packagesChecked,
        List<PackageUpdateEntry> acceptedUpdates,
        int transitiveFound,
        int transitiveWithheld = 0)
    {
        try
        {
            await transaction.RevertAsync();
            _consoleService.Success("Rolled back to previous versions.");
            _consoleService.Dim(StaleAssetsGuidance);
            BackupManager.CleanupBackups(backupPath, manifest);

            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.FileOperationError,
                PackagesChecked = packagesChecked,
                TransitivePackagesFound = transitiveFound,
                TransitivePackagesWithheld = transitiveWithheld,
                TestsPassed = false,
                WasRolledBack = true,
                Warnings = [StaleAssetsGuidance],
                Updates = acceptedUpdates
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _consoleService.Error($"Rollback failed: {ex.Message}");
            _consoleService.Warning($"Manual recovery: restore from backup at {backupPath}");

            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.FileOperationError,
                PackagesChecked = packagesChecked,
                TransitivePackagesFound = transitiveFound,
                TransitivePackagesWithheld = transitiveWithheld,
                TestsPassed = false,
                WasRolledBack = false,
                Updates = acceptedUpdates
            };
        }
    }

    public void Dispose()
    {
        _nuGetLookup.Dispose();
    }

    private static string? FindPropsFile(string basePath) =>
        GoverningFiles.FindNearestPropsFile(basePath);

    private static string? FindSolutionFile(string basePath) =>
        GoverningFiles.FindSolutionFile(basePath);

    private async Task<List<PackageUpdateEntry>> QueryNuGetForUpdatesAsync(
        Dictionary<string, HashSet<string>> currentVersions,
        bool includePrerelease)
    {
        var updates = new List<PackageUpdateEntry>();
        using var semaphore = new SemaphoreSlim(8);
        var tasks = new List<Task<PackageUpdateEntry?>>();

        foreach (var (packageName, versions) in currentVersions)
        {
            var currentVersion = ResolveCurrentVersion(versions);
            if (currentVersion == null)
            {
                _logger.LogWarning("Could not parse any version for {PackageName}, skipping", packageName);
                continue;
            }
            tasks.Add(QuerySinglePackageAsync(packageName, currentVersion, includePrerelease, semaphore));
        }

        var results = await Task.WhenAll(tasks);
        updates.AddRange(results.Where(r => r != null).Cast<PackageUpdateEntry>());

        return updates;
    }

    private static string? ResolveCurrentVersion(HashSet<string> versions)
    {
        if (versions.Count == 1)
        {
            return versions.First();
        }

        return versions
            .Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
            .Where(v => v != null)
            .OrderByDescending(v => v)
            .FirstOrDefault()
            ?.ToNormalizedString();
    }

    private async Task<PackageUpdateEntry?> QuerySinglePackageAsync(
        string packageName,
        string currentVersion,
        bool includePrerelease,
        SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            var latestVersion = await _nuGetLookup.GetLatestVersionAsync(packageName, includePrerelease);
            if (latestVersion == null)
            {
                _logger.LogWarning("Could not fetch version for {PackageName}, skipping", packageName);
                return null;
            }

            var currentNuGet = NuGetVersion.TryParse(currentVersion, out var parsed) ? parsed : null;
            if (currentNuGet == null)
            {
                _logger.LogWarning("Could not parse current version {Version} for {PackageName}", currentVersion, packageName);
                return null;
            }

            var isMajor = latestVersion.Major != currentNuGet.Major;

            return new PackageUpdateEntry(
                packageName,
                currentVersion,
                latestVersion.ToNormalizedString(),
                isMajor,
                !isMajor);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Names the packages whose version could not be determined. A failed lookup returns the same
    /// "no newer version" as a package that is genuinely current, so without this the run reports a
    /// silently incomplete result as a clean one.
    /// </summary>
    private void ReportFailedLookups()
    {
        var failed = _nuGetLookup.GetFailedLookups();
        if (failed.Count == 0)
        {
            return;
        }

        _consoleService.Warning(
            $"Could not check {failed.Count} package(s) after retries: "
                + string.Join(", ", failed.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        );
        _consoleService.Dim("These are reported as unchanged, not as up to date.");
    }

    /// <summary>
    /// Names the packages the feed answered 404 for. That is a definitive answer, not a failure —
    /// but it means the tool never saw the package's versions, so a private-feed or unpublished
    /// package must not read as "checked and current".
    /// </summary>
    private void ReportNotFoundLookups()
    {
        var notFound = _nuGetLookup.GetNotFoundLookups();
        if (notFound.Count == 0)
        {
            return;
        }

        _consoleService.Warning(
            $"Not found on nuget.org — private feed or unpublished?: "
                + string.Join(", ", notFound.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        );
        _consoleService.Dim("These are reported as unchanged, not as up to date.");
    }

    private void ShowUpdatesTable(List<PackageUpdateEntry> updates)
    {
        var directUpdates = updates.Where(u => !u.IsTransitive).OrderBy(u => u.PackageName).ToList();
        var transitiveUpdates = updates.Where(u => u.IsTransitive).OrderBy(u => u.PackageName).ToList();

        if (directUpdates.Count > 0)
        {
            _consoleService.WriteLine();
            _consoleService.Banner("DIRECT UPDATES");
            _consoleService.WriteLine();

            foreach (var update in directUpdates)
            {
                var label = update.IsMajorUpdate ? " (MAJOR)" : "";
                _consoleService.Info($"  {update.PackageName}: {update.CurrentVersion} → {update.LatestVersion}{label}");
            }

            _consoleService.WriteLine();
        }

        if (transitiveUpdates.Count > 0)
        {
            _consoleService.WriteLine();
            _consoleService.Banner("TRANSITIVE UPDATES");
            _consoleService.WriteLine();

            foreach (var update in transitiveUpdates)
            {
                var label = update.IsMajorUpdate ? " (MAJOR)" : "";
                _consoleService.Info($"  {update.PackageName}: {update.CurrentVersion} → {update.LatestVersion}{label}");
            }

            _consoleService.WriteLine();
        }
    }

    private List<PackageUpdateEntry> RunMajorVersionWizard(List<PackageUpdateEntry> updates, PackageUpdateRequest request)
    {
        var result = new List<PackageUpdateEntry>();
        // A redirected stdout cannot service the major-version prompt either, so it counts as
        // non-interactive alongside --quiet and --output Json: major updates are skipped, not
        // silently accepted.
        var nonInteractive = request.Output.IsNonInteractive || !_consoleService.IsInteractive;

        foreach (var update in updates)
        {
            if (!update.IsMajorUpdate)
            {
                result.Add(update with { Accepted = true });
                continue;
            }

            if (nonInteractive)
            {
                result.Add(update with { Accepted = false });
                continue;
            }

            var choices = new[]
            {
                $"Accept major update to {update.LatestVersion}",
                "Skip this package"
            };

            _consoleService.Warning($"{update.PackageName}: {update.CurrentVersion} → {update.LatestVersion} (MAJOR VERSION CHANGE)");
            var selection = _consoleService.AskSelection(
                $"How would you like to handle {update.PackageName}?",
                choices);

            var accepted = selection == choices[0];
            result.Add(update with { Accepted = accepted });
        }

        return result;
    }

    private void ShowDryRunSummary(List<PackageUpdateEntry> updates)
    {
        _consoleService.WriteLine();
        foreach (var update in updates.OrderBy(u => u.PackageName))
        {
            _consoleService.DryRun($"  {update.PackageName}: {update.CurrentVersion} → {update.LatestVersion}");
        }
        _consoleService.WriteLine();
    }

    /// <summary>
    /// The dry run's real preview: the props file the pass would write, computed with the same
    /// recipe the write path uses. Rendered whole by default and as a unified diff under
    /// <c>--diff</c>; the <c>--diff-file</c> artifact collects the diff either way.
    /// </summary>
    private void ShowPlannedPropsDiff(
        UpdateLoadContext load,
        List<PackageUpdateEntry> updatesToApply,
        PackageUpdateRequest request)
    {
        var planned = UpdatePropsPreview.PlannedContent(
            _propsGenerator, load.PropsPath, load.CurrentVersions, updatesToApply);
        if (planned is null)
        {
            return;
        }

        if (request.ShowDiff || _diffCollector.IsEnabled)
        {
            var diff = UnifiedDiffGenerator.Generate(
                File.ReadAllText(load.PropsPath), planned, load.PropsPath);
            _diffCollector.Append(diff);
            if (request.ShowDiff)
            {
                _consoleService.WriteDiff(diff);
                return;
            }
        }

        _consoleService.WritePropsPreview(planned);
    }

    /// <summary>
    /// Scans all projects for transitive dependencies, deduplicates them, excludes those already
    /// managed as direct deps, and queries NuGet for their latest versions.
    /// </summary>
    /// <returns>A tuple of (update entries, total transitive deps found before filtering).</returns>
    private async Task<(List<PackageUpdateEntry> Updates, int TotalFound)> ScanAndQueryTransitiveUpdatesAsync(
        List<string> projectPaths,
        Dictionary<string, HashSet<string>> currentVersions,
        bool includePrerelease)
    {
        _consoleService.Info("Scanning transitive dependencies...");

        var allTransitive = new List<PackageReference>();
        var anySuccess = false;

        foreach (var projectPath in projectPaths)
        {
            try
            {
                var (refs, success) = await _projectAnalyzer.ScanTransitivePackagesAsync(projectPath);
                if (success)
                {
                    allTransitive.AddRange(refs);
                    anySuccess = true;
                }
                else
                {
                    _logger.LogWarning("Transitive scan failed for {Project}", Path.GetFileName(projectPath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transitive scan failed for {Project}", Path.GetFileName(projectPath));
            }
        }

        if (!anySuccess)
        {
            _consoleService.Warning("Could not scan transitive dependencies. Continuing with direct updates only.");
            return ([], 0);
        }

        var deduplicated = allTransitive
            .GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var highest = g
                    .Select(r => (Ref: r, Parsed: NuGetVersion.TryParse(r.Version, out var v) ? v : null))
                    .Where(x => x.Parsed != null)
                    .OrderByDescending(x => x.Parsed)
                    .FirstOrDefault();
                return highest.Ref;
            })
            .Where(r => r != null)
            .Select(r => r)
            .ToList();

        var totalFound = deduplicated.Count;

        var transitiveOnly = deduplicated
            .Where(r => !currentVersions.ContainsKey(r.PackageName))
            .ToList();

        if (transitiveOnly.Count == 0)
        {
            _consoleService.Info("No transitive updates found (all already managed as direct dependencies).");
            return ([], totalFound);
        }

        _consoleService.Info($"Found {transitiveOnly.Count} transitive dependencies to check...");

        using var semaphore = new SemaphoreSlim(8);
        var tasks = transitiveOnly.Select(r =>
            QuerySingleTransitivePackageAsync(r.PackageName, r.Version, includePrerelease, semaphore));

        var results = await Task.WhenAll(tasks);
        var updates = results.Where(r => r != null).Cast<PackageUpdateEntry>().ToList();

        return (updates, totalFound);
    }

    private async Task<PackageUpdateEntry?> QuerySingleTransitivePackageAsync(
        string packageName,
        string currentVersion,
        bool includePrerelease,
        SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            var latestVersion = await _nuGetLookup.GetLatestVersionAsync(packageName, includePrerelease);
            if (latestVersion == null)
            {
                _logger.LogWarning("Could not fetch version for transitive dep {PackageName}, skipping", packageName);
                return null;
            }

            var currentNuGet = NuGetVersion.TryParse(currentVersion, out var parsed) ? parsed : null;
            if (currentNuGet == null)
            {
                _logger.LogWarning("Could not parse transitive version {Version} for {PackageName}", currentVersion, packageName);
                return null;
            }

            var isMajor = latestVersion.Major != currentNuGet.Major;

            return new PackageUpdateEntry(
                packageName,
                currentVersion,
                latestVersion.ToNormalizedString(),
                isMajor,
                !isMajor,
                IsTransitive: true);
        }
        finally
        {
            semaphore.Release();
        }
    }

}

internal sealed record UpdateLoadContext(
    string BasePath,
    List<string> ProjectPaths,
    string PropsPath,
    Dictionary<string, HashSet<string>> CurrentVersions)
{
    public PackageUpdateResult? EarlyResult { get; private init; }

    public static UpdateLoadContext FromEarly(PackageUpdateResult result) =>
        new(string.Empty, new(), string.Empty, new()) { EarlyResult = result };
}

internal sealed record UpdateBackupContext(string Path, BackupManifest Manifest)
{
    public PackageUpdateResult? EarlyResult { get; private init; }

    public static UpdateBackupContext FromEarly(PackageUpdateResult result) =>
        new(string.Empty, null!) { EarlyResult = result };
}
